# Concurrent EF Core Patterns with SQLite

Getting `InvalidOperationException: A second operation was started on this context instance`
or `SQLite Error 5: 'database is locked'` when running concurrent operations? This page
explains why, and shows the correct pattern for each scenario.

---

## Why DbContext Is Not Thread-Safe

`DbContext` maintains per-instance state: the change tracker, open queries, pending
transactions, and the underlying database connection. EF Core does not synchronize access
to this state — sharing one `DbContext` instance across concurrent tasks will produce
either `InvalidOperationException` (EF Core detects the conflict) or silent data
corruption.

This is not a SQLite limitation. The same restriction applies to all EF Core providers.

---

## The Three Correct Patterns

### Pattern 1 — Request-Scoped (ASP.NET Core, Razor Pages, Blazor Server)

**When to use:** Each HTTP request runs on its own DI scope. ASP.NET Core creates one
`DbContext` per scope automatically.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");

// Controller — one context per request, injected by DI
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;
    public OrdersController(AppDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Create(Order order)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        // Concurrent requests each have their own _db — no sharing
        return Ok();
    }
}
```

**Concurrent writes across requests** are serialized automatically by the write queue.
No additional code needed.

---

### Pattern 2 — Factory-Per-Task (Background Services, Task.WhenAll, Channel Consumers)

**When to use:** Multiple tasks run concurrently within the same scope. Each task needs
its own independent `DbContext`.

```csharp
// Program.cs
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");

// Background service
public class BulkProcessorService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public BulkProcessorService(IDbContextFactory<AppDbContext> factory)
        => _factory = factory;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var itemIds = await GetWorkItemsAsync(ct);

        var tasks = itemIds.Select(async id =>
        {
            // Each task creates and disposes its own context
            await using var db = _factory.CreateDbContext();
            var item = await db.Items.FindAsync(id, ct);
            item.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // writes serialized by the write queue
        });

        await Task.WhenAll(tasks); // all complete without SQLITE_BUSY
    }
}
```

**Wrong — do not do this:**

```csharp
// ❌ WRONG: shared context across concurrent tasks
public class BrokenProcessor
{
    private readonly AppDbContext _db; // shared — not safe for concurrent use

    public async Task ProcessAll(IEnumerable<int> ids)
    {
        var tasks = ids.Select(async id =>
        {
            var item = await _db.Items.FindAsync(id); // ❌ concurrent access to shared context
            item.Done = true;
            await _db.SaveChangesAsync(); // ❌ throws InvalidOperationException
        });
        await Task.WhenAll(tasks);
    }
}
```

---

### Pattern 3 — ThreadSafeSqliteContext Base Class

**When to use:** You want write serialization, transaction management, and retry logic
built into the context itself, without injecting `IDbContextFactory`.

```csharp
// Define your context
public class AppDbContext : ThreadSafeSqliteContext<AppDbContext>
{
    public AppDbContext(string connectionString) : base(connectionString) { }
    public DbSet<Order> Orders => Set<Order>();
}

// Use ExecuteWriteAsync — handles write lock, transaction, retry, and commit
await using var db = new AppDbContext("Data Source=app.db");
await db.ExecuteWriteAsync(async ctx =>
{
    ctx.Orders.Add(new Order { /* ... */ });
    // SaveChangesAsync is called automatically at the end of the lambda
});

// Or bulk insert
await db.BulkInsertSafeAsync(orders);
```

---

## Pattern Decision Table

| Scenario | Pattern | Registration |
|---|---|---|
| ASP.NET Core controllers / Razor Pages | Request-scoped | `AddConcurrentSqliteDbContext<T>` |
| Blazor Server | Request-scoped | `AddConcurrentSqliteDbContext<T>` |
| Background service / `IHostedService` | Factory-per-task | `AddConcurrentSqliteDbContextFactory<T>` |
| `Task.WhenAll` with N concurrent writes | Factory-per-task | `AddConcurrentSqliteDbContextFactory<T>` |
| `Parallel.ForEachAsync` | Factory-per-task | `AddConcurrentSqliteDbContextFactory<T>` |
| `Channel<T>` consumer | Factory-per-task | `AddConcurrentSqliteDbContextFactory<T>` |
| Console app / no DI | `ThreadSafeSqliteContext` | None (constructor injection) |
| Desktop app (WPF, WinForms, MAUI) | `ThreadSafeSqliteContext` or factory | Either |

---

## How the Write Queue Serializes Across All Patterns

All three patterns share the same process-wide write queue (keyed by database file path).
When any of them calls a write API (`SaveChangesAsync`, `SaveChangesSerializedAsync`,
`BulkInsertOptimizedAsync`, `ExecuteWriteAsync`), the work is enqueued to the same
`Channel<IWriteRequest>`. The single background writer executes one request at a time.

This means a Pattern 1 controller write and a Pattern 2 background task write are
automatically serialized against each other — you do not need to coordinate between them.

---

## Related Pages

- [Troubleshooting SQLITE_BUSY errors](troubleshooting-sqlite-busy.md)
- [Performance and WAL tuning guide](performance-guide.md)
- [Migration guide](migration-guide.md)
- [Full README](../README.md)
