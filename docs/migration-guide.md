# Migration Guide — Upgrading from Plain UseSqlite

Moving from `Microsoft.EntityFrameworkCore.Sqlite` to
`EntityFrameworkCore.Sqlite.Concurrency` is a drop-in change. This guide covers every
registration pattern and provides a checklist for a clean migration.

---

## Step 1: Install the Package

```bash
dotnet add package EntityFrameworkCore.Sqlite.Concurrency
```

Remove any package that was providing manual retry or locking logic — it is no longer needed.

---

## Step 2: Replace Registration Calls

### Direct UseSqlite → UseSqliteWithConcurrency

```csharp
// Before
options.UseSqlite("Data Source=app.db");

// After
options.UseSqliteWithConcurrency("Data Source=app.db");
```

### AddDbContext → AddConcurrentSqliteDbContext (request-scoped)

```csharp
// Before
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite("Data Source=app.db"));

// After
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");
```

### AddDbContextFactory → AddConcurrentSqliteDbContextFactory (concurrent workloads)

```csharp
// Before
builder.Services.AddDbContextFactory<AppDbContext>(o =>
    o.UseSqlite("Data Source=app.db"));

// After
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");
```

### With Custom Options

```csharp
// After — with options
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>(
    "Data Source=app.db",
    options =>
    {
        options.BusyTimeout      = TimeSpan.FromSeconds(30);
        options.MaxRetryAttempts = 5;
        options.SynchronousMode  = SqliteSynchronousMode.Full;
    });
```

---

## Step 3: Migration Checklist

- [ ] **Remove `Cache=Shared` from all connection strings.**
  `Cache=Shared` is incompatible with WAL mode. The library throws `ArgumentException`
  at startup if it is present. Connection pooling is enabled automatically.

  ```
  // Before (remove this flag)
  "Data Source=app.db;Cache=Shared"

  // After
  "Data Source=app.db"
  ```

- [ ] **Remove manual `SemaphoreSlim` or `lock` write guards.**
  The write queue handles write serialization. Application-level locks around `SaveChangesAsync`
  are redundant and may cause deadlocks when combined with the write queue.

- [ ] **Remove custom retry loops around `SaveChangesAsync`.**
  Built-in exponential backoff with full jitter handles `SQLITE_BUSY` and `SQLITE_BUSY_SNAPSHOT`
  variants. Custom retry code should be removed to avoid double-retry and incorrect snapshot
  restart behavior.

- [ ] **Switch concurrent workloads from shared `DbContext` to `IDbContextFactory<T>`.**
  If any background service, `Task.WhenAll`, or `Parallel.ForEachAsync` block shares a
  single `DbContext` across concurrent tasks, switch to `AddConcurrentSqliteDbContextFactory<T>`
  and inject `IDbContextFactory<AppDbContext>`. Call `CreateDbContext()` per task.
  See [Concurrent EF Core patterns](concurrent-efcore-patterns.md).

- [ ] **Remove custom `PRAGMA` setup from `OnConfiguring` or migration scripts.**
  The library applies `journal_mode=WAL`, `busy_timeout`, `wal_autocheckpoint`, and
  `synchronous` on every connection open. Custom PRAGMA calls may conflict. Use
  `SqliteConcurrencyOptions` to configure these values instead.

- [ ] **Remove any `SaveChangesSerializedAsync` workarounds** (if you had a custom version).
  `SaveChangesSerializedAsync` is a first-class extension method on `DbContext` — call it
  directly anywhere you previously called `SaveChangesAsync`.

---

## Step 4: Verify

```bash
dotnet build
dotnet test
```

Run your normal integration tests. If any test hits `SQLITE_BUSY`, check the checklist
above — the most common cause is a shared `DbContext` across concurrent tasks.

---

## Version History

| Version | Highlights |
|---|---|
| 10.1.0 | Channel-based write queue, `WriteQueueCapacity`, netstandard 2.0 TFM, `BulkInsertSafeAsync` O(n) fix and `ChangeTracker.Clear()` |
| 10.0.3 | `SQLITE_BUSY_SNAPSHOT` correct restart, `AddConcurrentSqliteDbContextFactory<T>`, structured logging, WAL checkpoint monitoring, migration lock recovery |
| 10.0.2 | `SynchronousMode` option, `UpgradeTransactionsToImmediate` option, startup validation |
| 10.0.1 | `Cache=Shared` rejection at startup, full jitter backoff, `GetWalCheckpointStatusAsync` |
| 10.0.0 | Initial release — WAL mode, `BEGIN IMMEDIATE` upgrade, `BulkInsertOptimizedAsync`, DI registration |

## Breaking Changes

None in any version. All releases have been fully backwards-compatible.

---

## Related Pages

- [Troubleshooting SQLITE_BUSY errors](troubleshooting-sqlite-busy.md)
- [Concurrent EF Core patterns](concurrent-efcore-patterns.md)
- [Performance and WAL tuning guide](performance-guide.md)
- [Full README](../README.md)
