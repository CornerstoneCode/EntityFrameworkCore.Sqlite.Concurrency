# EntityFrameworkCore.Sqlite.Concurrency — Wiki

> Fix `SQLite Error 5: 'database is locked'`, `SQLITE_BUSY_SNAPSHOT`, and `A second operation was started on this context` in Entity Framework Core + SQLite applications.

**NuGet:** [EntityFrameworkCore.Sqlite.Concurrency](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency) | **Source:** [GitHub](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency) | **Latest:** v10.1.0

---

## Are you seeing one of these errors?

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'
```
```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 517: 'database is locked'
```
```
InvalidOperationException: A second operation was started on this context instance before a previous operation completed.
```

**This package is the fix.** One line change, no rewrites:

```csharp
// Before
options.UseSqlite("Data Source=app.db");

// After
options.UseSqliteWithConcurrency("Data Source=app.db");
```

---

## Wiki Pages

| Page | What it covers |
|---|---|
| [[Fixing SQLite Error 5 database is locked in EF Core]] | Root causes of SQLITE_BUSY (error 5), SQLITE_BUSY_SNAPSHOT (error 517), SQLITE_LOCKED (error 6) and how to fix each one |
| [[Concurrent EF Core with SQLite — Task.WhenAll and Background Services]] | Why DbContext is not thread-safe, the IDbContextFactory pattern, concurrent write serialization |
| [[Bulk Insert Performance with EF Core and SQLite]] | BulkInsertOptimizedAsync, batching, ChangeTracker memory growth, real benchmark results |
| [[WAL Mode and PRAGMA Tuning for SQLite in dotnet]] | How Write-Ahead Logging works, PRAGMA configuration reference, checkpoint monitoring |
| [[Migration Guide — from UseSqlite to UseSqliteWithConcurrency]] | Step-by-step upgrade, common pitfalls (Cache=Shared, SemaphoreSlim, shared DbContext) |

---

## What This Package Solves

| Error / Symptom | Root Cause | Solution |
|---|---|---|
| `SQLite Error 5: 'database is locked'` (SQLITE_BUSY) | Multiple writers contending simultaneously | Channel-based write queue: one writer at a time, all others wait in FIFO order |
| `SQLite Error 5: 'database is locked'` (SQLITE_BUSY_SNAPSHOT, extended code 517) | WAL read snapshot became stale mid-transaction | Automatic `BEGIN IMMEDIATE` upgrade; full operation restart on snapshot error |
| `A second operation was started on this context instance` | `DbContext` shared across concurrent `Task.WhenAll` flows | `IDbContextFactory<T>` registration — one context per concurrent task |
| Bulk inserts slow or running out of memory | One `SaveChangesAsync` per entity; unbounded `ChangeTracker` growth | `BulkInsertOptimizedAsync` — batched writes, `ChangeTracker.Clear()` per batch |
| Read queries slow during writes | Default journal mode blocks reads while writer holds lock | WAL mode enabled automatically — reads never block behind writes |

---

## Quick Installation

```bash
dotnet add package EntityFrameworkCore.Sqlite.Concurrency
```

```xml
<!-- .csproj -->
<PackageReference Include="EntityFrameworkCore.Sqlite.Concurrency" Version="10.1.0" />
```

---

## Setup by App Type

### ASP.NET Core / Razor Pages / Blazor Server

One `DbContext` per HTTP request. Register with `AddConcurrentSqliteDbContext`:

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");

// Use normally — concurrent writes across requests are serialized automatically
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(Order order)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(); // never throws SQLITE_BUSY
        return Ok();
    }
}
```

### Background Services / Worker Processes / Task.WhenAll

`DbContext` is not thread-safe — never share a single instance across concurrent tasks. Use `IDbContextFactory<T>`:

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");

// Background service
public class SyncService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public async Task ProcessBatchAsync(IEnumerable<int> ids)
    {
        var tasks = ids.Select(async id =>
        {
            await using var db = _factory.CreateDbContext(); // independent context per task
            var record = await db.Records.FindAsync(id);
            record.SyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(); // writes serialized — zero SQLITE_BUSY
        });

        await Task.WhenAll(tasks); // all 50+ tasks complete without error
    }
}
```

### Bulk Import Jobs

```csharp
// 10,000 records — auto-batched in 1,000-record chunks, ChangeTracker cleared per batch
await context.BulkInsertOptimizedAsync(records);
```

---

## Why SQLITE_BUSY Happens in EF Core

SQLite is a file-based database with a simple locking model:

- Only **one writer** can hold a write lock at a time
- In default journal mode, **all readers** are also blocked while a writer holds the lock
- EF Core opens connections and starts transactions per `SaveChangesAsync` call
- When two requests, two background tasks, or two threads call `SaveChangesAsync` at the same time, SQLite immediately returns `SQLITE_BUSY` (error 5) to the second writer rather than waiting

### Why retrying once usually works (but is fragile)

Because `SQLITE_BUSY` is returned instantly — SQLite does not queue writers. If you retry immediately, the first writer has usually finished by the time the retry executes. This works at low concurrency but breaks under load:

- At 5+ concurrent writers, the retry itself contends again and fails
- `SQLITE_BUSY_SNAPSHOT` (error 517) can never be fixed by retrying the same statement — the read snapshot must be discarded and the full transaction restarted
- Retry loops with `Thread.Sleep` cause thundering-herd: all retrying writers wake simultaneously and contend again

This package solves the root cause: instead of letting writers race and retry, a single background writer processes all write requests in FIFO order.

---

## Why SQLITE_BUSY_SNAPSHOT (Error 517) is Different

`SQLITE_BUSY_SNAPSHOT` is an extended result code (`5 | (2 << 8) = 517`) that only occurs in WAL mode. It fires when:

1. Connection A starts a read transaction and reads some rows
2. Connection B commits a write that modifies those same rows
3. Connection A tries to upgrade to a write transaction

At step 3, Connection A's view of the database is now stale — it was taken before Connection B's commit. SQLite refuses to let Connection A write because it could overwrite Connection B's changes based on an outdated read.

**The only correct fix** is to roll back Connection A's entire transaction and retry from scratch — re-reading all rows with a fresh snapshot. Retrying just the failed statement produces the same error.

This package implements this correctly: `ExecuteWithRetryAsync` reruns the entire user-supplied lambda on `SQLITE_BUSY_SNAPSHOT`, discarding all reads from the stale snapshot.

---

## Performance — Real Benchmark Results

Measured with BenchmarkDotNet v0.14.0 on .NET 10.0.2 / Windows 11 / Intel i7-13700K:

| Operation | Standard EF Core + SQLite | EntityFrameworkCore.Sqlite.Concurrency | Improvement |
|---|---|---|---|
| Bulk insert — 1,000 entities | 4,500 ms (1,000 individual transactions) | **28 ms** (single batched write) | ~161x faster |
| Concurrent writes — 50 `Task.WhenAll` writers | throws `SQLITE_BUSY` immediately | all 50 complete, zero errors | |
| Parallel reads during write | blocked until writer finishes | concurrent — WAL mode | |

Reproduce: `dotnet run -c Release --project EFCore.Sqlite.Concurrency.Benchmarks -- --job short`

---

## Configuration Reference

```csharp
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>(
    "Data Source=app.db",
    options =>
    {
        options.BusyTimeout       = TimeSpan.FromSeconds(30); // PRAGMA busy_timeout
        options.MaxRetryAttempts  = 3;                        // app-level retries
        options.CommandTimeout    = 300;                      // SQL command timeout (seconds)
        options.WalAutoCheckpoint = 1000;                     // PRAGMA wal_autocheckpoint (pages)
        options.SynchronousMode   = SqliteSynchronousMode.Normal; // PRAGMA synchronous
        options.UpgradeTransactionsToImmediate = true;        // prevents SQLITE_BUSY_SNAPSHOT
        options.WriteQueueCapacity = null;                    // null = unbounded queue
    });
```

| Option | Default | Purpose |
|---|---|---|
| `BusyTimeout` | 30 s | How long SQLite itself waits before surfacing `SQLITE_BUSY`. Covers cross-process contention not handled by the write queue. |
| `MaxRetryAttempts` | 3 | Application-level retries with exponential backoff + full jitter. |
| `UpgradeTransactionsToImmediate` | `true` | Rewrites `BEGIN` to `BEGIN IMMEDIATE`, acquiring the write lock up-front and preventing `SQLITE_BUSY_SNAPSHOT`. |
| `WriteQueueCapacity` | `null` | Set an integer to create a bounded queue with back-pressure. Producers block asynchronously when the queue is full. |
| `WalAutoCheckpoint` | 1000 pages | Controls WAL file size (~4 MB at default page size). Set to 0 to disable automatic checkpoints. |

---

## Common Pitfalls

**`Cache=Shared` in the connection string** — incompatible with WAL mode. This package throws `ArgumentException` at startup if it detects `Cache=Shared`. Remove it; connection pooling (`Pooling=true`, enabled automatically) is the correct alternative.

**Sharing a `DbContext` across `Task.WhenAll`** — `DbContext` is not thread-safe by design. Each concurrent flow needs its own instance via `IDbContextFactory<T>`. The write queue serializes the writes at the SQLite level automatically.

**Database on a network filesystem** — WAL mode requires all connections to be on the same physical host. Do not use NFS, SMB, or other network-mounted paths. Use a client/server database for multi-host deployments.

**Disposing the factory before tasks complete** — if you call `factory.CreateDbContext()` in tasks and the DI container disposes the factory before `Task.WhenAll` completes, contexts will be disposed mid-operation. Ensure the factory lifetime exceeds the task lifetime.

---

## Links

- [NuGet Package](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency)
- [GitHub Repository](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency)
- [CHANGELOG](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/blob/main/CHANGELOG.md)
- [CONTRIBUTING](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/blob/main/CONTRIBUTING.md)
- [SECURITY](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/blob/main/SECURITY.md)
- [Report a Bug](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/issues/new?template=bug_report.yml)
- [Request a Feature](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/issues/new?template=feature_request.yml)
- [SQLite Result Codes — sqlite.org](https://sqlite.org/rescode.html)
- [Microsoft.Data.Sqlite Database Errors — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)
- [EF Core DbContext Lifetime and Configuration — Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/)
