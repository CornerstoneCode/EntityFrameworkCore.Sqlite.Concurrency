# EntityFrameworkCore.Sqlite.Concurrency — Quick Start

Getting `SQLite Error 5: 'database is locked'` or `SQLITE_BUSY` errors in your .NET app?
**EntityFrameworkCore.Sqlite.Concurrency** fixes this with a one-line change.

## Install

```bash
dotnet add package EntityFrameworkCore.Sqlite.Concurrency
```

## The One-Line Fix

```csharp
// Before:
options.UseSqlite("Data Source=app.db");

// After — eliminates SQLITE_BUSY, serializes writes, enables WAL-mode parallel reads:
options.UseSqliteWithConcurrency("Data Source=app.db");
```

## Registration Patterns

### Pattern 1 — Request-Scoped (ASP.NET Core, Razor Pages, Blazor Server)

One context per HTTP request. Use when each request runs on its own thread.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");
```

Inject `AppDbContext` directly into controllers, services, and Razor Pages as normal.

### Pattern 2 — Concurrent Workloads (Background Services, Task.WhenAll, Channels)

`DbContext` is not thread-safe — never share one instance across concurrent tasks.
Use `IDbContextFactory<T>` to get an independent context per task. Writes are still
serialized at the SQLite level automatically.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");

// Usage — one context per concurrent operation
public class MyService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public MyService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task ProcessManyAsync(IEnumerable<int> ids)
    {
        var tasks = ids.Select(async id =>
        {
            await using var db = _factory.CreateDbContext(); // independent context per task
            var item = await db.Items.FindAsync(id);
            item.Done = true;
            await db.SaveChangesAsync(); // writes serialized — no SQLITE_BUSY
        });
        await Task.WhenAll(tasks);
    }
}
```

## Next Steps

- [Full README](../README.md) — all features, configuration options, FAQ
- [Troubleshooting SQLITE_BUSY](../docs/troubleshooting-sqlite-busy.md) — error code reference
- [Concurrent EF Core patterns](../docs/concurrent-efcore-patterns.md) — pattern guide
- [Migration guide](../docs/migration-guide.md) — checklist for upgrading from plain `UseSqlite`
