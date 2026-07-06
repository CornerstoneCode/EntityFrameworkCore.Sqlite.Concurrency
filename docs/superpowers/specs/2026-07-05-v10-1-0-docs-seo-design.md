# v10.1.0 Documentation & SEO Refresh — Design Spec

**Date:** 2026-07-05
**Version target:** 10.1.0 (from 10.0.4)
**Approach:** Option A (Problem-First Rewrite) + Option C (Microsite-Style Docs)
**Scope:** csproj metadata, README.md, doc/QUICKSTART.md, docs/ microsite pages, doc/v10_1_0.md

---

## Goal

Make the package maximally discoverable when a developer searches for `SQLITE_BUSY C#`,
`database is locked EF Core`, `SQLITE_BUSY_SNAPSHOT`, or `EF Core concurrent writes` — on
Google, NuGet search, and AI assistants (Claude, Copilot). Every surface (NuGet metadata,
GitHub README, docs pages) is rewritten to lead with the exact error strings developers paste
into search engines.

Nothing is additive-only. Files may be completely replaced, deleted, or created from scratch.

---

## Section 1: csproj Metadata

**File:** `EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj`

### Version
`10.0.4` → `10.1.0`

### Description
Replace current description with:

> Fixes `SQLITE_BUSY` / `"database is locked"` / `SQLITE_BUSY_SNAPSHOT` errors in Entity
> Framework Core + SQLite apps. Provides Channel-based write serialization, automatic
> `BEGIN IMMEDIATE` transaction upgrades, exponential-backoff retry, WAL-mode optimization,
> and 10x faster bulk inserts. Drop-in replacement for `UseSqlite()` targeting .NET 10 and
> netstandard 2.0.

### Tags
Add to existing tag list (space-delimited):
```
sqlite-busy-snapshot error-5 write-serialization channel-queue netstandard2
efcore-concurrency write-queue background-service task-whenall
```

### PackageReleaseNotes
Replace v10.0.3 notes with v10.1.0 (see Section 5 for full content).

---

## Section 2: README.md

**File:** `README.md` — complete replacement.

### Structure (top to bottom)

#### 1. Hook — the exact error
```
# EntityFrameworkCore.Sqlite.Concurrency

Getting this error?

    Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'

This is the fix.
```
Badges follow immediately after the one-line fix.

#### 2. One-line fix
```csharp
// Before:
options.UseSqlite("Data Source=app.db");

// After — eliminates SQLITE_BUSY, adds write serialization and WAL optimization:
options.UseSqliteWithConcurrency("Data Source=app.db");
```

#### 3. The four problems this solves
Each problem is introduced with the exact exception message the developer would see in their
stack trace:

| Exception / Error | Root Cause | What This Package Does |
|---|---|---|
| `SQLite Error 5: 'database is locked'` (`SQLITE_BUSY`) | Multiple writers contending simultaneously | Channel-based write queue — one writer at a time, all others park efficiently |
| `SQLite Error 5: 'database is locked'` (extended code 517, `SQLITE_BUSY_SNAPSHOT`) | WAL read snapshot became stale mid-transaction | `BEGIN IMMEDIATE` upgrade + full operation restart on snapshot staleness |
| `InvalidOperationException: A second operation was started on this context` | `DbContext` shared across concurrent tasks | `IDbContextFactory<T>` registration pattern, one context per concurrent flow |
| Bulk inserts slow / OOM on large datasets | Linear `SaveChanges()`, unbounded ChangeTracker growth | `BulkInsertOptimizedAsync` — batched, WAL-optimized, ChangeTracker cleared per batch |

#### 4. Why choose this package
Existing comparison table, moved here (position 4, after problem is established).

#### 5. Installation
```bash
dotnet add package EntityFrameworkCore.Sqlite.Concurrency
```

#### 6. Three scenario sections

**Request-scoped (ASP.NET Core controllers, Razor Pages, Blazor Server)**
```csharp
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");
```

**Concurrent workloads (background services, Task.WhenAll, Channel<T>)**
```csharp
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");
// Inject IDbContextFactory<AppDbContext>, call CreateDbContext() per concurrent operation.
```

**High-volume batch jobs**
```csharp
await context.BulkInsertOptimizedAsync(records);
```
Each section includes a complete, realistic code example.

#### 7. Configuration table
All existing rows kept. New row added:

| `WriteQueueCapacity` | `null` (unbounded) | Maximum number of pending writes queued before callers block. `null` = accept writes without limit. Set a value to apply back-pressure on producers. |

#### 8. FAQ
Existing questions kept. Four new entries added:

- **What is the Channel-based write queue?** Replaces the `SemaphoreSlim` in v10.0.x. Under high concurrency, SemaphoreSlim wakes all waiters simultaneously (thundering herd). A `Channel<T>` parks callers in FIFO order and drains them one at a time — no wakeup storm, lower CPU, fairer scheduling.
- **What does SQLITE_BUSY_SNAPSHOT (error 517) mean?** The connection's WAL read snapshot became stale after another writer committed. Naive retry of the same statement produces the same error. The correct fix (which this package implements) is to roll back and restart the entire operation so data is re-read against the current snapshot.
- **Does this work with netstandard 2.0?** The write queue and connection layer (`SqliteConnectionEnhancer`, `SqliteWriteQueue`, `SqliteConcurrencyOptions`) target netstandard 2.0. EF Core-dependent APIs (`UseSqliteWithConcurrency`, `ThreadSafeSqliteContext`) require net10.0 because EF Core 10 does.
- **What's the actual per-write overhead?** Sub-millisecond. Enqueueing to a Channel and awaiting a `TaskCompletionSource` adds microseconds per operation — negligible against real SQLite I/O.

#### 9. Benchmark table
Kept but relabeled "Typical Results" with footnote:
> Results observed in practice on .NET 10 / Windows 11. Not a formal benchmark suite.
> Actual gains depend on write volume, hardware, and WAL page size.

#### 10. Links to docs/ pages
```
→ Troubleshooting SQLITE_BUSY errors
→ Concurrent EF Core patterns
→ Performance and WAL tuning guide
→ Migration guide (from plain UseSqlite)
```

#### 11. System Requirements + License
Unchanged, moved to bottom.

---

## Section 3: doc/QUICKSTART.md

**File:** `doc/QUICKSTART.md` — complete replacement.

### Goal
A developer who lands here cold gets to working code in under 60 seconds. Nothing else.

### Structure

#### Opening (3 sentences max)
State the problem, state the package name, state the install command.

#### The one-line change
Before/after `UseSqlite` → `UseSqliteWithConcurrency`.

#### Two registration patterns (no more, no less)

**Pattern 1 — Request-scoped**
When: ASP.NET Core controllers, Razor Pages, Blazor Server (one context per HTTP request).
```csharp
builder.Services.AddConcurrentSqliteDbContext<AppDbContext>("Data Source=app.db");
```

**Pattern 2 — Concurrent workloads**
When: background services, `Task.WhenAll`, `Parallel.ForEachAsync`, `Channel<T>`.
```csharp
builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");
// Inject IDbContextFactory<AppDbContext>. Call CreateDbContext() per concurrent operation.
```

#### Next steps
Four links: README, troubleshooting page, concurrent patterns page, migration guide.

### What is removed
- All references to `ThreadSafeEFCore.SQLite` (old package name) — replaced with
  `EntityFrameworkCore.Sqlite.Concurrency` everywhere
- Wrong/right DbContext-sharing comparison (belongs in concurrent patterns page)
- Reading-data example (reads just work, no config needed)
- Factory-without-DI example (belongs in README)
- Error handling section (belongs in troubleshooting page)

---

## Section 4: docs/ Microsite Pages

**Directory:** `docs/` (new, at repo root)

Four files. Each page is self-contained — no assumed prior reading.

### docs/troubleshooting-sqlite-busy.md

**Primary search terms:** `SQLITE_BUSY C#`, `SQLite Error 5 database is locked`, `SQLITE_BUSY_SNAPSHOT EF Core`, `SQLite error 517`

**Content outline:**
1. The three error codes and what each means (SQLITE_BUSY=5, SQLITE_BUSY_SNAPSHOT=517, SQLITE_LOCKED=6)
2. Why naive retry doesn't fix SQLITE_BUSY_SNAPSHOT (stale snapshot semantics)
3. Why `busy_timeout` alone isn't enough (process-level contention vs. SQLite-level)
4. What this package does for each error code — specific, concrete
5. Diagnostic checklist (connection string, Cache=Shared, WAL status)
6. WAL checkpoint health check code snippet

### docs/concurrent-efcore-patterns.md

**Primary search terms:** `EF Core DbContext thread safe`, `Task.WhenAll SaveChanges EF Core`, `IDbContextFactory concurrent`, `DbContext concurrent operations`

**Content outline:**
1. Why DbContext is not thread-safe (EF Core change tracker, not a SQLite limitation)
2. The three correct patterns:
   - Request-scoped: `AddConcurrentSqliteDbContext<T>` → inject `T` directly
   - Factory-per-task: `AddConcurrentSqliteDbContextFactory<T>` → inject `IDbContextFactory<T>`, call `CreateDbContext()` per task
   - `ThreadSafeSqliteContext<T>`: base class with `ExecuteWriteAsync` built in
3. Wrong-vs-right code for each pattern
4. When to use which (decision table)
5. How the Channel-based write queue serializes writes across all patterns

### docs/performance-guide.md

**Primary search terms:** `SQLite bulk insert C# EF Core`, `EF Core SQLite performance`, `WAL mode Entity Framework`, `SQLite PRAGMA tuning C#`

**Content outline:**
1. How WAL mode enables parallel reads (conceptual, one paragraph)
2. The Channel-based write queue — why it beats SemaphoreSlim under load
3. `BulkInsertOptimizedAsync` — what it does (batching, PRAGMA, ChangeTracker.Clear)
4. PRAGMA reference table (busy_timeout, wal_autocheckpoint, synchronous) with recommended values
5. `WriteQueueCapacity` — when to set it (high-throughput producers, back-pressure scenarios)
6. WAL checkpoint monitoring (`GetWalCheckpointStatusAsync`)
7. Typical result ranges (mirrors README benchmark table with same "observed in practice" disclaimer)

### docs/migration-guide.md

**Primary search terms:** `migrate from UseSqlite`, `upgrade EF Core SQLite concurrency`

**Content outline:**
1. Before/after for every registration pattern (exact code, no prose)
2. Migration checklist:
   - [ ] Remove `Cache=Shared` from connection strings
   - [ ] Remove manual `SemaphoreSlim` or lock-based retry code
   - [ ] Switch concurrent workloads from shared `DbContext` to `IDbContextFactory<T>`
   - [ ] Remove custom `PRAGMA` setup (the library sets WAL, busy_timeout, autocheckpoint)
3. Version history table (what changed in each 10.x release)
4. Breaking changes per release (none so far — note explicitly)

---

## Section 5: doc/v10_1_0.md

**File:** `doc/v10_1_0.md` — new file, same structure as `doc/v10_0_3.md`.

### Headline
"Channel-Based Write Queue, netstandard 2.0 Support, and Bug Fixes"

### New Features

**Channel-based write queue (headline)**
- Replaces `SemaphoreSlim` per-database with `Channel<IWriteRequest>` + single background writer Task
- Why it matters: SemaphoreSlim wakes all N waiters simultaneously when released; N-1 immediately
  re-sleep (thundering herd, CPU churn, unfair scheduling). Channel parks callers in FIFO order
  and drains them one at a time — no wakeup storm
- Zero API change — existing call sites get the benefit automatically
- Reentrancy detection preserved via `AsyncLocal<bool> IsWriteLockHeld`

**`WriteQueueCapacity` option**
- New `int?` property on `SqliteConcurrencyOptions` (default `null` = unbounded)
- When set, creates a `BoundedChannel` with `BoundedChannelFullMode.Wait` — callers block
  asynchronously when queue is full rather than exceeding capacity
- Use for high-throughput producer scenarios where back-pressure is desired

**netstandard 2.0 TFM**
- Package now multi-targets `net10.0;netstandard2.0`
- Available on netstandard 2.0: `SqliteConnectionEnhancer`, `SqliteWriteQueue`, `SqliteConcurrencyOptions`
- net10.0-only (EF Core 10 required): `UseSqliteWithConcurrency`, `SqliteConcurrencyInterceptor`,
  `ThreadSafeSqliteContext<T>`, `AddConcurrentSqliteDbContext<T>`, `AddConcurrentSqliteDbContextFactory<T>`
- Polyfills added for netstandard 2.0: `IsExternalInit`, `HashCode.Combine`, `Channel.Reader.ReadAllAsync`,
  `Task.WaitAsync`

### Bug Fixes

**`ThreadSafeSqliteContext` always used default options**
- `ExecuteWriteAsync` was constructing `new SqliteConcurrencyOptions()` ignoring what the caller
  configured via `UseSqliteWithConcurrency`
- Fixed: options are now read from the registered interceptor via `TryGetInterceptor`

**`BulkInsertSafeAsync` O(n²) and unbounded ChangeTracker growth**
- `Skip(n).Take(1000)` loop was O(n²) in LINQ-to-objects for large entity lists
- `ChangeTracker.Clear()` was missing between batches, causing memory to grow linearly with
  total entity count
- Fixed: `Chunk(1000)` replaces `Skip/Take`; `ChangeTracker.Clear()` added after each batch `SaveChangesAsync`

### No Breaking Changes
All existing `UseSqliteWithConcurrency`, `AddConcurrentSqliteDbContext`,
`AddConcurrentSqliteDbContextFactory`, `ExecuteWithRetryAsync`, `BulkInsertOptimizedAsync`,
and `SaveChangesSerializedAsync` call sites compile and behave correctly without modification.
`WriteQueueCapacity` is additive. netstandard 2.0 TFM is additive.

### Configuration Reference
Full table including new `WriteQueueCapacity` row.

---

## File Change Summary

| File | Action |
|---|---|
| `EFCore.Sqlite.Concurrency.csproj` | Modify — version, description, tags, release notes |
| `README.md` | Replace completely |
| `doc/QUICKSTART.md` | Replace completely |
| `doc/v10_1_0.md` | Create new |
| `docs/troubleshooting-sqlite-busy.md` | Create new |
| `docs/concurrent-efcore-patterns.md` | Create new |
| `docs/performance-guide.md` | Create new |
| `docs/migration-guide.md` | Create new |

---

## Out of Scope

- API reference documentation (XML doc comments / IntelliSense already covers this)
- Contributing guide
- Changelog consolidation (individual `doc/v*.md` files are the changelog)
- Benchmark harness (numbers remain "observed in practice" until a formal bench project exists)
- CI/CD changes
