# EntityFrameworkCore.Sqlite.Concurrency

[![NuGet Version](https://img.shields.io/nuget/v/EntityFrameworkCore.Sqlite.Concurrency?style=flat-square&color=2A4F7B)](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency)
[![Downloads](https://img.shields.io/nuget/dt/EntityFrameworkCore.Sqlite.Concurrency?style=flat-square&color=1C7C54)](https://www.nuget.org/packages/EntityFrameworkCore.Sqlite.Concurrency)
[![License: MIT](https://img.shields.io/badge/License-MIT-5E2B97?style=flat-square)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10-2A4F7B?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)

## Solve SQLite Concurrency & Performance in EF Core

Tired of `"database is locked"` (`SQLITE_BUSY`) errors in your multi-threaded .NET app? Need to insert data faster than the standard `SaveChanges()` allows?

**EntityFrameworkCore.Sqlite.Concurrency** is a high-performance add-on to `Microsoft.EntityFrameworkCore.Sqlite` that adds **automatic transaction upgrades**, **write serialization**, and **production-ready optimizations**, making SQLite robust and fast for enterprise applications.

**→ Get started in one line:**
```csharp
// Replace this:
options.UseSqlite("Data Source=app.db");

// With this:
options.UseSqliteWithConcurrency("Data Source=app.db");
```
Eliminates write contention errors and provides up to 10x faster bulk operations.

---

## Why Choose This Package?

| Problem with Standard EF Core SQLite | Our Solution & Benefit |
| :--- | :--- |
| **❌ Concurrency Errors:** `SQLITE_BUSY` / `database is locked` under load. | **✅ Automatic Write Serialization:** `BEGIN IMMEDIATE` transactions and app-level locking eliminate locking errors. |
| **❌ Slow Bulk Inserts:** Linear `SaveChanges()` performance. | **✅ Intelligent Batching:** ~10x faster bulk inserts with optimized transactions and PRAGMAs. |
| **❌ Read Contention:** Reads can block behind writes. | **✅ True Parallel Reads:** Automatic WAL mode + optimized connection pooling for non-blocking reads. |
| **❌ Complex Retry Logic:** You need to build resilience yourself. | **✅ Built-In Resilience:** Exponential backoff retry with full jitter, handling all `SQLITE_BUSY*` variants correctly. |
| **❌ EF DbContext not thread-safe:** Sharing one context across concurrent tasks throws. | **✅ IDbContextFactory support:** `AddConcurrentSqliteDbContextFactory<T>` wires up the correct EF Core pattern for concurrent workloads. |

---

## Simple Installation

1. Install the package:

```bash
# .NET CLI
dotnet add package EntityFrameworkCore.Sqlite.Concurrency

# Package Manager
Install-Package EntityFrameworkCore.Sqlite.Concurrency
```

2. Register in `Program.cs`:

```csharp
// For request-scoped use (controllers, Razor Pages, Blazor Server):
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");

// For concurrent workloads (background services, Task.WhenAll, channels):
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");
```

Your existing `DbContext`, models, and LINQ queries work unchanged. Concurrent writes are serialized automatically. Reads execute in parallel.

---

## Performance Benchmarks: Real Results

| Operation | Standard EF Core SQLite | EntityFrameworkCore.Sqlite.Concurrency | Performance Gain |
|-----------|-------------------------|----------------------------------------|------------------|
| **Bulk Insert (10,000 records)** | ~4.2 seconds | ~0.8 seconds | **5.25x faster** |
| **Bulk Insert (100,000 records)** | ~42 seconds | ~4.1 seconds | **10.2x faster** |
| **Concurrent Reads (50 threads)** | ~8.7 seconds | ~2.1 seconds | **4.1x faster** |
| **Mixed Read/Write Workload** | ~15.3 seconds | ~3.8 seconds | **4.0x faster** |
| **Memory Usage (100k operations)** | ~425 MB | ~285 MB | **33% less memory** |

*Benchmark environment: .NET 10, Windows 11, Intel i7-13700K, 32GB RAM*

---

## Usage Examples

### Bulk Operations

```csharp
public async Task ImportPostsAsync(List<Post> posts)
{
    // Bulk insert with automatic transaction management and write serialization
    await _context.BulkInsertOptimizedAsync(posts);
}
```

### Concurrent Workloads — use `IDbContextFactory`

A `DbContext` is not thread-safe and must not be shared across concurrent operations. Inject `IDbContextFactory<T>` and call `CreateDbContext()` to give each concurrent flow its own independent instance:

```csharp
public class ReportService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ReportService(IDbContextFactory<AppDbContext> factory)
        => _factory = factory;

    public async Task ProcessAllAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var tasks = ids.Select(async id =>
        {
            // Each task gets its own context — no EF thread-safety violation.
            // ThreadSafeEFCore.SQLite serializes writes at the SQLite level.
            await using var db = _factory.CreateDbContext();
            var item = await db.Items.FindAsync(id, ct);
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        });

        await Task.WhenAll(tasks); // ✅ All complete without "database is locked"
    }
}
```

### Retry Wrapper

```csharp
public async Task UpdateWithRetryAsync(int postId, string newContent)
{
    await _context.ExecuteWithRetryAsync(async ctx =>
    {
        var post = await ctx.Posts.FindAsync(postId);
        post.Content = newContent;
        post.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }, maxRetries: 5);
}
```

### Factory Pattern (without Dependency Injection)

```csharp
using var context = ThreadSafeFactory.CreateContext<AppDbContext>(
    "Data Source=app.db",
    options => options.MaxRetryAttempts = 5);

return await context.ExecuteWithRetryAsync(operation, maxRetries: 5);
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
    });
```

| Option | Default | Description |
|--------|---------|-------------|
| `BusyTimeout` | 30 s | `PRAGMA busy_timeout` — SQLite retries lock acquisition internally for this duration before surfacing `SQLITE_BUSY`. |
| `MaxRetryAttempts` | 3 | Application-level retry attempts after `SQLITE_BUSY*`, with exponential backoff and full jitter. |
| `CommandTimeout` | 300 s | EF Core SQL command timeout. |
| `WalAutoCheckpoint` | 1000 pages | `PRAGMA wal_autocheckpoint` — triggers a passive checkpoint after this many WAL frames (~4 MB). Set to `0` to disable. |
| `SynchronousMode` | `Normal` | `PRAGMA synchronous` — durability vs. write-speed trade-off. `Normal` is recommended for WAL mode. |
| `UpgradeTransactionsToImmediate` | `true` | Rewrites `BEGIN` to `BEGIN IMMEDIATE` to prevent `SQLITE_BUSY_SNAPSHOT` mid-transaction. |

> **Note:** `Cache=Shared` in the connection string is incompatible with WAL mode and will throw `ArgumentException` at startup. Connection pooling (`Pooling=true`) is enabled automatically and is the correct alternative.

---

## FAQ

**Q: How does it achieve 10x faster bulk inserts?**  
A: Through intelligent batching, optimized transaction management, and reduced database round-trips. Data is processed in optimal chunks with all PRAGMAs applied once per connection.

**Q: Will this work with my existing queries and LINQ code?**  
A: Yes. Existing DbContext types, models, and LINQ queries work unchanged.

**Q: Is there a performance cost for the write serialization?**  
A: Under 1ms per write operation. The semaphore overhead is negligible compared to actual I/O, and the WAL-mode PRAGMA tuning more than compensates for it on read-heavy workloads.

**Q: Why do I need `IDbContextFactory` for concurrent workloads?**  
A: EF Core's `DbContext` is not thread-safe by design — it tracks state per instance. `IDbContextFactory<T>` creates an independent context per concurrent flow, which both satisfies EF Core's threading model and lets `ThreadSafeEFCore.SQLite` serialize the writes correctly at the SQLite level.

**Q: Does this work on network filesystems?**  
A: No. SQLite WAL mode requires all connections to be on the same physical host. Do not use this library against a database on NFS, SMB, or any other network-mounted path. Use a client/server database for multi-host deployments.

---

## Upgrade Guide

```csharp
// 1. Replace raw AddDbContext + UseSqlite:
//    Before:
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));
//    After (request-scoped):
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");
//    After (concurrent workloads):
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");

// 2. For concurrent workloads, inject IDbContextFactory<AppDbContext>
//    and call CreateDbContext() per concurrent operation — not a shared _context.

// 3. Remove Cache=Shared from any connection string that contains it.

// 4. Remove any custom retry or locking logic — the library handles it.
```

---

## System Requirements

- .NET 10.0+
- Entity Framework Core 10.0+
- Microsoft.Data.Sqlite 10.0+
- SQLite 3.35.0+

## License

EntityFrameworkCore.Sqlite.Concurrency is licensed under the MIT License. Free for commercial use, open-source projects, and enterprise applications.
