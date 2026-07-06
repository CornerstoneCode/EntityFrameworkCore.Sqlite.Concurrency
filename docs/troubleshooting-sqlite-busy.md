# Troubleshooting SQLITE_BUSY Errors in C# / EF Core

If you're seeing any of these errors in your .NET application, this page explains what they
mean, why they happen, and how **EntityFrameworkCore.Sqlite.Concurrency** fixes them.

---

## The Three Error Codes

### SQLITE_BUSY — Error Code 5

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'
```

**What it means:** Another connection holds a write lock on the database and your connection
could not acquire one within the `busy_timeout` window.

**Why it happens:** SQLite allows only one writer at a time. In a multi-threaded .NET app,
when two threads call `SaveChangesAsync()` simultaneously, one succeeds and the other
gets `SQLITE_BUSY`.

**Why `busy_timeout` alone isn't enough:** `PRAGMA busy_timeout` is a per-connection,
SQLite-level retry. It only helps when the conflicting connection is in the same process
and releases the lock within the timeout window. Under sustained concurrent write load,
multiple threads will still contend and produce errors after the timeout expires.

**How this package fixes it:** A process-wide `Channel<IWriteRequest>` queues all writes
and drains them one at a time through a single background writer. Callers park asynchronously
and are notified when their write completes — no polling, no wakeup storms, no lock contention.

---

### SQLITE_BUSY_SNAPSHOT — Extended Code 517

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'
SqliteException.SqliteExtendedErrorCode == 517
```

**What it means:** This is `SQLITE_BUSY | (2 << 8) = 517`. It fires when a connection's
WAL read snapshot became stale after another writer committed mid-transaction. The connection
is trying to upgrade from a read transaction to a write transaction, but its snapshot of
the database is now behind the current WAL head.

**Why naive retry doesn't work:** Retrying the same statement on the same connection
produces the same error — the snapshot is still stale. The only correct fix is to:
1. Roll back the entire transaction
2. Restart the entire operation from scratch so all reads are re-issued against the current snapshot

**How this package fixes it:** `BEGIN IMMEDIATE` is used for all write transactions
(via the `UpgradeTransactionsToImmediate` option, default `true`). This acquires the
write lock at transaction start rather than at first write — preventing the snapshot from
ever becoming stale mid-transaction. If `SQLITE_BUSY_SNAPSHOT` (517) is still received,
`ExecuteWithRetryAsync` and `ExecuteWriteAsync` detect the extended error code and restart
the full operation lambda.

---

### SQLITE_LOCKED — Error Code 6

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 6: 'database table is locked'
```

**What it means:** A different table or connection within the **same process** holds a
lock that conflicts with your operation. Unlike `SQLITE_BUSY`, this is not a concurrency
issue between processes — it indicates an application-level bug.

**Common causes:**
- A `DataReader` is open on the same connection while a write is attempted
- A transaction was started but not committed or rolled back
- Nested writes without the reentrancy guard

**How this package handles it:** `SQLITE_LOCKED` is logged at `Error` level (not retried)
because it indicates a programming error that retry cannot fix. The exception propagates
to the caller for diagnosis.

---

## Diagnostic Checklist

If you're still seeing `SQLITE_BUSY` after installing this package, work through this list:

- [ ] **Connection string has `Cache=Shared`?**
  Remove it. `Cache=Shared` is incompatible with WAL mode and will throw `ArgumentException`
  at startup if present. Connection pooling (`Pooling=true`) is enabled automatically.

- [ ] **Using a shared `DbContext` across concurrent tasks?**
  `DbContext` is not thread-safe. Use `AddConcurrentSqliteDbContextFactory<T>` and inject
  `IDbContextFactory<T>`. Call `CreateDbContext()` per concurrent operation.
  See [Concurrent EF Core patterns](concurrent-efcore-patterns.md).

- [ ] **Multiple processes writing to the same database?**
  The write queue is process-scoped. Multiple OS processes contend at the SQLite level.
  Increase `BusyTimeout` and `MaxRetryAttempts`, or consider a client/server database.

- [ ] **Database on a network filesystem?**
  WAL mode does not work on NFS, SMB, or other network-mounted paths. Use local disk only.

- [ ] **WAL file growing unboundedly?**
  A long-running read transaction can block checkpoint completion, causing the WAL to grow
  and degrade performance. Check WAL health:

```csharp
var connection = db.Database.GetDbConnection();
await connection.OpenAsync();
var status = await SqliteConnectionEnhancer.GetWalCheckpointStatusAsync(connection);

if (status.IsBusy)
    Console.WriteLine($"WAL blocked: {status.TotalWalFrames} frames, " +
                      $"{status.CheckpointedFrames} checkpointed " +
                      $"({status.CheckpointProgress:F1}%)");
```

---

## Error Code Quick Reference

| Code | Extended Code | Name | Meaning | Retry? |
|---|---|---|---|---|
| 5 | 5 | `SQLITE_BUSY` | Another writer holds the lock | Yes — after backoff |
| 5 | 261 | `SQLITE_BUSY_RECOVERY` | WAL recovery in progress | Yes — after backoff |
| 5 | 517 | `SQLITE_BUSY_SNAPSHOT` | Read snapshot is stale | Yes — **restart entire operation** |
| 6 | 6 | `SQLITE_LOCKED` | Same-connection conflict | No — application bug |

---

## Related Pages

- [Concurrent EF Core patterns](concurrent-efcore-patterns.md)
- [Performance and WAL tuning guide](performance-guide.md)
- [Migration guide](migration-guide.md)
- [Full README](../README.md)
