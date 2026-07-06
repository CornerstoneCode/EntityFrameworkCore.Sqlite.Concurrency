# Performance and WAL Tuning Guide

This guide explains how **EntityFrameworkCore.Sqlite.Concurrency** achieves high
throughput on SQLite and how to tune it for your workload.

---

## WAL Mode — How Parallel Reads Work

SQLite's default journal mode (`DELETE`) uses an exclusive write lock: while a write
is in progress, all reads must wait. WAL mode (Write-Ahead Logging) separates the write
log from the main database file, allowing readers and the writer to operate concurrently:

- **Readers** read from the main database file at a consistent snapshot point
- **The writer** appends to the WAL file
- **Checkpoint** periodically merges WAL pages back into the main file

This library enables WAL mode automatically on every new connection via `PRAGMA journal_mode=WAL`.
You do not need to configure it.

---

## The Channel-Based Write Queue

Prior to v10.1.0, write serialization used `SemaphoreSlim(1,1)` per database file.
Under high concurrent write load, `SemaphoreSlim.Release()` wakes all N waiting threads
simultaneously. N-1 immediately go back to sleep — this is the **thundering herd problem**:
CPU spikes, context-switch storms, unfair scheduling.

Since v10.1.0, a `Channel<IWriteRequest>` replaces the semaphore:

```
Producer 1 ──┐
Producer 2 ──┼──► Channel<IWriteRequest> ──► Background Writer ──► SQLite
Producer N ──┘      (FIFO queue)               (single task)
```

Each producer enqueues a `WriteRequest<T>` containing its work lambda and a
`TaskCompletionSource<T>`, then awaits the completion source. The background writer
pops one request at a time, executes the lambda, and signals the `TaskCompletionSource`.

**Result:** No wakeup storm. Callers park until their turn. FIFO fairness.
Throughput under concurrent load is higher and more predictable.

---

## BulkInsertOptimizedAsync

For importing large numbers of records, `BulkInsertOptimizedAsync` outperforms repeated
individual `SaveChangesAsync` calls by:

1. **Batching** — entities are processed in chunks of 1,000 rather than one giant transaction
2. **`ChangeTracker.Clear()`** — the change tracker is cleared after each batch, preventing
   memory from growing linearly with total entity count
3. **Write queue** — the entire operation is enqueued as a single work item, holding the
   write slot for the duration and avoiding per-batch lock overhead
4. **WAL PRAGMAs** — `busy_timeout`, `wal_autocheckpoint`, and `synchronous` are already
   tuned per connection; no additional setup needed

```csharp
// Import 100,000 records — automatically batched in 1,000-record chunks
await context.BulkInsertOptimizedAsync(records);
```

For even higher throughput during a controlled bulk import where durability is not required:

```csharp
// Scratch/import database — set SynchronousMode.Off during import, restore afterward
options.UseSqliteWithConcurrency("Data Source=import.db", o =>
{
    o.SynchronousMode = SqliteSynchronousMode.Off;
});
```

---

## PRAGMA Tuning Reference

| PRAGMA | Option | Default | Guidance |
|---|---|---|---|
| `busy_timeout` | `BusyTimeout` | 30 s | Increase to 60 s+ if multiple OS processes write to the same database. The write queue handles in-process contention; cross-process contention still goes through SQLite's native timeout. |
| `wal_autocheckpoint` | `WalAutoCheckpoint` | 1000 pages | Each page is 4,096 bytes by default (~4 MB per 1,000 frames). Increase to 5,000–10,000 for write-heavy workloads to reduce checkpoint frequency. Set to `0` to disable automatic checkpoints and manage manually. |
| `synchronous` | `SynchronousMode` | `Normal` | `Normal` is safe after an application crash (recommended for WAL). Use `Full` for power-loss safety. Use `Off` only for disposable bulk-import databases. |
| `journal_mode` | — | `WAL` (set automatically) | Do not change. WAL mode is required for parallel reads. |

---

## WriteQueueCapacity — Back-Pressure for High-Throughput Producers

By default the write queue is unbounded — any number of producers can enqueue writes
without blocking. For workloads where producers significantly outpace the writer,
an unbounded queue can lead to memory growth.

Set `WriteQueueCapacity` to apply back-pressure:

```csharp
options.UseSqliteWithConcurrency("Data Source=app.db", o =>
{
    o.WriteQueueCapacity = 500;
    // Producers block asynchronously once 500 writes are pending
});
```

When the queue is full, `Channel.Writer.WriteAsync` blocks the producer until the
background writer drains a slot. This is `BoundedChannelFullMode.Wait` — no writes
are dropped, no exceptions thrown — producers simply slow down to match the writer's pace.

**When to set it:** When you have bursty producers (e.g., a `Parallel.ForEachAsync`
spinning up thousands of tasks simultaneously) and want to limit peak memory usage.

**When to leave it null:** Most applications. The write queue processes requests as fast
as SQLite can commit them; backlog only builds if producers are genuinely faster than storage.

---

## WAL Checkpoint Health Monitoring

Long-running read transactions block WAL checkpoint completion. When a checkpoint cannot
complete, the WAL file grows unboundedly and read performance degrades (readers must scan
more WAL pages to reconstruct their snapshot).

Monitor WAL health periodically in production:

```csharp
var connection = db.Database.GetDbConnection();
await connection.OpenAsync();
var status = await SqliteConnectionEnhancer.GetWalCheckpointStatusAsync(connection);

logger.LogInformation(
    "WAL: {Total} frames, {Done} checkpointed ({Pct:F1}%), busy={Busy}",
    status.TotalWalFrames,
    status.CheckpointedFrames,
    status.CheckpointProgress,
    status.IsBusy);

if (status.IsBusy && status.TotalWalFrames > 5000)
    logger.LogWarning("WAL checkpoint blocked — a long-running reader may be " +
                      "preventing WAL reclamation.");
```

---

## Benchmark Results

Measured with **BenchmarkDotNet v0.14.0** on `.NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2` / Windows 11 / Intel i7-13700K:

| Method | EntityCount | Mean | StdDev | N |
|---|---|---|---|---|
| `Baseline_SaveChangesPerEntity` (plain EF Core) | 1,000 | 4,500 ms | 80 ms | 3 |
| `Package_BulkInsertOptimizedAsync` | 1,000 | **28.0 ms** | 3.3 ms | 84 |

**Ratio: ~161x faster** for batch-insert workloads.

The baseline calls `SaveChangesAsync()` once per entity — 1,000 individual transactions, each with its own disk sync. The package uses a single batched write: one transaction, `ChangeTracker.Clear()` between chunks, WAL-tuned PRAGMAs.

Concurrent write benchmarks (`Task.WhenAll` with 50 concurrent writers) have no meaningful baseline — plain EF Core throws `SQLITE_BUSY` immediately under concurrent write load. The package handles 50 concurrent writers with zero exceptions.

---

## Running the Benchmarks

```bash
cd EFCore.Sqlite.Concurrency.Benchmarks

# Quick run (~5 minutes, ShortRun job)
dotnet run -c Release -- --job short

# Full run (~30 minutes, production-quality measurements)
dotnet run -c Release

# Specific benchmark class only
dotnet run -c Release -- --filter "*BulkInsert*" --job short
```

Results are written to `BenchmarkDotNet.Artifacts/results/` in Markdown, HTML, and CSV formats. The `BenchmarkDotNet.Artifacts/` directory is git-ignored — run locally to reproduce.

---

## Related Pages

- [Troubleshooting SQLITE_BUSY errors](troubleshooting-sqlite-busy.md)
- [Concurrent EF Core patterns](concurrent-efcore-patterns.md)
- [Migration guide](migration-guide.md)
- [Full README](../README.md)
