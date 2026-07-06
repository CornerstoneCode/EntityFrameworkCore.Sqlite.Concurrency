# EntityFrameworkCore.Sqlite.Concurrency

[![NuGet Version](https://img.shields.io/nuget/v/EntityFrameworkCore.Sqlite.Concurrency?style=flat-square&color=2A4F7B)](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency)
[![Downloads](https://img.shields.io/nuget/dt/EntityFrameworkCore.Sqlite.Concurrency?style=flat-square&color=1C7C54)](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency)
[![CI](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/actions/workflows/ci.yml/badge.svg)](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-5E2B97?style=flat-square)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10-2A4F7B?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)

## Getting this error?

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'
```

**This is the fix.** One line change, no other modifications to your code:

```csharp
// Before:
options.UseSqlite("Data Source=app.db");

// After — eliminates SQLITE_BUSY, serializes writes, enables WAL-mode parallel reads:
options.UseSqliteWithConcurrency("Data Source=app.db");
```

---

## What Problems This Solves

| Error / Exception | Root Cause | Fix |
|---|---|---|
| `SQLite Error 5: 'database is locked'` (`SQLITE_BUSY`) | Multiple writers contending simultaneously | Channel-based write queue — one writer at a time, all others park efficiently in FIFO order |
| `SQLite Error 5: 'database is locked'` (extended code 517, `SQLITE_BUSY_SNAPSHOT`) | WAL read snapshot became stale mid-transaction | `BEGIN IMMEDIATE` upgrade + full operation restart on snapshot staleness |
| `InvalidOperationException: A second operation was started on this context instance` | `DbContext` shared across concurrent tasks | `IDbContextFactory<T>` registration — one independent context per concurrent flow |
| Bulk inserts slow or running out of memory on large datasets | Linear `SaveChanges()`, unbounded `ChangeTracker` growth per batch | `BulkInsertOptimizedAsync` — batched writes, `ChangeTracker.Clear()` per batch, WAL-optimized transactions |

> See [Troubleshooting SQLITE_BUSY errors](docs/troubleshooting-sqlite-busy.md) for a deep-dive on each error code.

---

## Why Choose This Package?

| Problem with Standard EF Core + SQLite | Solution |
|---|---|
| `SQLITE_BUSY` / `database is locked` under concurrent writes | Channel-based write queue serializes all writes — zero contention errors |
| `SQLITE_BUSY_SNAPSHOT` mid-transaction | Automatic `BEGIN IMMEDIATE` upgrade prevents stale read snapshots |
| Slow bulk inserts | `BulkInsertOptimizedAsync` — typically 5–10x faster via batching and PRAGMA tuning |
| Read contention during writes | WAL mode enabled automatically — reads never block behind writes |
| Manual retry logic | Built-in exponential backoff with full jitter for all `SQLITE_BUSY*` variants |
| `DbContext` not thread-safe | `IDbContextFactory<T>` wiring and `ThreadSafeSqliteContext<T>` base class |

---

## Installation

```bash
# .NET CLI
dotnet add package EntityFrameworkCore.Sqlite.Concurrency

# Package Manager Console
Install-Package EntityFrameworkCore.Sqlite.Concurrency
```

---

## Usage

### Request-Scoped Apps (ASP.NET Core, Razor Pages, Blazor Server)

One `DbContext` per HTTP request. ASP.NET Core scopes handle thread safety at the request level.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");

// Inject normally — concurrent writes across requests are serialized automatically
public class PostsController : ControllerBase
{
    private readonly AppDbContext _db;
    public PostsController(AppDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Create(Post post)
    {
        _db.Posts.Add(post);
        await _db.SaveChangesAsync(); // thread-safe across concurrent requests
        return Ok();
    }
}
```

### Concurrent Workloads (Background Services, Task.WhenAll, Channel Consumers)

`DbContext` is not thread-safe — never share one instance across concurrent tasks. Use `IDbContextFactory<T>` to give each concurrent flow its own independent context. Writes are still serialized at the SQLite level automatically.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");

// Background service — each task gets its own context
public class ReportService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public ReportService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task ProcessAllAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var tasks = ids.Select(async id =>
        {
            await using var db = _factory.CreateDbContext(); // one context per task
            var item = await db.Items.FindAsync(id, ct);
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // writes serialized — no "database is locked"
        });

        await Task.WhenAll(tasks); // all complete without SQLITE_BUSY
    }
}
```

> See [Concurrent EF Core patterns](docs/concurrent-efcore-patterns.md) for the full pattern guide.

### High-Volume Batch Jobs

```csharp
// 10,000 records — batched in chunks of 1,000, ChangeTracker cleared between batches
await context.BulkInsertOptimizedAsync(records);

// Or with explicit retry control
await context.ExecuteWithRetryAsync(async ctx =>
{
    foreach (var record in records)
        ctx.Records.Add(record);
}, maxRetries: 5);
```

---

## Configuration

```csharp
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>(
    "Data Source=app.db",
    options =>
    {
        options.BusyTimeout        = TimeSpan.FromSeconds(30);
        options.MaxRetryAttempts   = 3;
        options.CommandTimeout     = 300;
        options.WalAutoCheckpoint  = 1000;
        options.SynchronousMode    = SqliteSynchronousMode.Normal;
        options.UpgradeTransactionsToImmediate = true;
        options.WriteQueueCapacity = null; // null = unbounded
    });
```

| Option | Default | Description |
|---|---|---|
| `BusyTimeout` | 30 s | `PRAGMA busy_timeout` — SQLite retries lock acquisition internally before surfacing `SQLITE_BUSY`. |
| `MaxRetryAttempts` | 3 | Application-level retries after `SQLITE_BUSY*`, with exponential backoff and full jitter. |
| `CommandTimeout` | 300 s | EF Core SQL command timeout. |
| `WalAutoCheckpoint` | 1000 pages | `PRAGMA wal_autocheckpoint` — triggers passive checkpoint after this many WAL frames (~4 MB). Set to `0` to disable. |
| `SynchronousMode` | `Normal` | `PRAGMA synchronous` — durability vs. write-speed. `Normal` is recommended for WAL mode. |
| `UpgradeTransactionsToImmediate` | `true` | Rewrites `BEGIN` to `BEGIN IMMEDIATE` to prevent `SQLITE_BUSY_SNAPSHOT` mid-transaction. |
| `WriteQueueCapacity` | `null` | Maximum pending writes before callers block. `null` = accept without limit. Set a value for back-pressure on high-volume producers. |

> **Note:** `Cache=Shared` in the connection string is incompatible with WAL mode and will throw `ArgumentException` at startup. Connection pooling (`Pooling=true`) is enabled automatically and is the correct alternative.

---

## FAQ

**Q: What is the Channel-based write queue?**
A: Since v10.1.0, write serialization uses `Channel<IWriteRequest>` with a single background writer instead of `SemaphoreSlim`. Under high concurrency, `SemaphoreSlim` wakes all N waiters simultaneously when the lock releases — N-1 immediately re-sleep (CPU churn, unfair scheduling, thundering herd). A `Channel<T>` parks callers in FIFO order and drains them one at a time. No API change is needed — existing code gets the benefit automatically.

**Q: What does SQLITE_BUSY_SNAPSHOT (error code 517) mean?**
A: This is a specific variant of `SQLITE_BUSY` (extended code `5 | (2 << 8) = 517`) that fires when a connection's WAL read snapshot became stale after another writer committed mid-transaction. Retrying the same statement produces the same error — the only correct fix is to roll back and restart the entire operation so all reads are re-queried against the current snapshot. This package implements that correctly.

**Q: Does this work with netstandard 2.0?**
A: The connection layer (`SqliteConnectionEnhancer`, `SqliteWriteQueue`, `SqliteConcurrencyOptions`) targets `netstandard2.0`. EF Core-dependent APIs (`UseSqliteWithConcurrency`, `ThreadSafeSqliteContext<T>`, `AddConcurrentSqliteDbContext<T>`) require `net10.0` because EF Core 10 requires it.

**Q: How much overhead does write serialization add?**
A: Sub-millisecond. Enqueueing to a `Channel` and awaiting a `TaskCompletionSource` adds microseconds per write — negligible compared to actual SQLite I/O. WAL-mode PRAGMA tuning typically more than compensates on read-heavy workloads.

**Q: Does this work with my existing DbContext, models, and LINQ queries?**
A: Yes. Change `UseSqlite` to `UseSqliteWithConcurrency`. Everything else compiles and runs unchanged.

**Q: Does this work on network filesystems?**
A: No. SQLite WAL mode requires all connections to be on the same physical host. Do not point the database at an NFS, SMB, or other network-mounted path. Use a client/server database (PostgreSQL, SQL Server) for multi-host deployments.

**Q: Why do I need IDbContextFactory for concurrent workloads?**
A: EF Core's `DbContext` is not thread-safe by design — it tracks entity state per instance and cannot be shared across concurrent tasks. `IDbContextFactory<T>` creates an independent context per concurrent flow. Writes from all contexts are serialized at the SQLite level by the write queue.

---

## Benchmark Results

Measured with [BenchmarkDotNet v0.14.0](https://benchmarkdotnet.org) on .NET 10.0.2 / Windows 11 / Intel i7-13700K:

| Benchmark | Standard EF Core | This Package | Ratio |
|---|---|---|---|
| `BulkInsertOptimizedAsync` — 1,000 entities | **4,500 ms** | **28 ms** | ~161x faster |
| Concurrent writes (50 tasks, `Task.WhenAll`) | throws `SQLITE_BUSY` | completes cleanly | ∞ |

> Bulk insert: baseline calls `SaveChangesAsync()` per entity (1,000 individual transactions); package uses a single batched write. Full numbers and reproduce command: `docs/performance-guide.md`.
>
> Reproduce: `dotnet run -c Release --project EFCore.Sqlite.Concurrency.Benchmarks -- --job short`

---

## Documentation

- [Troubleshooting SQLITE_BUSY errors](docs/troubleshooting-sqlite-busy.md)
- [Concurrent EF Core patterns](docs/concurrent-efcore-patterns.md)
- [Performance and WAL tuning guide](docs/performance-guide.md)
- [Migration guide — from plain UseSqlite](docs/migration-guide.md)
- [v10.1.0 Release Notes](doc/v10_1_0.md)

---

## System Requirements

- .NET 10.0+ (for EF Core APIs) or netstandard 2.0+ (for connection layer only)
- Entity Framework Core 10.0+
- Microsoft.Data.Sqlite 10.0+
- SQLite 3.35.0+ (WAL2 and `SQLITE_BUSY_SNAPSHOT` require 3.37.0+)

## License

MIT License — free for commercial use, open-source projects, and enterprise applications.
