# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- Added a `SaveChangesSerializedAsync` overload that accepts a save delegate, enabling safe serialized `DbContext.SaveChangesAsync` overrides without recursion.

---

## [10.1.1] - 2026-10-05

### Security
- **Upgraded SQLitePCLRaw.core to 2.1.12** — addresses vulnerability [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
  (SQLite embedded library security fix). All transitive `SQLitePCLRaw.*` packages updated to 2.1.12.
  Vulnerability suppression comment removed from project file.

### Compatibility
- **Zero API changes.** Fully backward compatible with v10.1.0.
- **Zero migration effort.** All existing code works without modification.

---

## [10.1.0] - 2026-07-05

### Added
- **Channel-based write queue** — replaces `SemaphoreSlim` with `Channel<IWriteRequest>` + single
  background writer. Eliminates thundering-herd wakeup storms under high concurrent write load.
  All callers park in FIFO order; zero API change required.
- **`WriteQueueCapacity` option** — new `int?` on `SqliteConcurrencyOptions` (default `null` = unbounded).
  When set, creates a `BoundedChannel` with `BoundedChannelFullMode.Wait` for back-pressure control.
- **netstandard 2.0 TFM** — `SqliteConnectionEnhancer`, `SqliteWriteQueue`, and `SqliteConcurrencyOptions`
  now available on any runtime supporting netstandard 2.0. EF Core APIs remain net10.0-only.
- **BenchmarkDotNet project** — `EFCore.Sqlite.Concurrency.Benchmarks/` with reproducible benchmarks
  for bulk insert, concurrent writes, and parallel reads.
- **Stress tests** — `EFCore.Sqlite.Concurrency.Test/` with four correctness-under-load xUnit tests
  covering all write paths at 50 concurrent writers.

### Fixed
- `ThreadSafeSqliteContext.ExecuteWriteAsync` always used default `SqliteConcurrencyOptions` instead
  of reading the registered interceptor's configured options. Fixed via `TryGetInterceptor` lookup.
- `BulkInsertSafeAsync` used `Skip(n).Take(1000)` — O(n²) in LINQ-to-objects for large lists.
  Replaced with `Chunk(1000)` for O(n) enumeration.
- `BulkInsertSafeAsync` did not call `ChangeTracker.Clear()` between batches, causing unbounded
  memory growth during large imports. Fixed.

---

## [10.0.3] - 2026-01-15

### Added
- `AddConcurrentSqliteDbContextFactory<T>` — registers `IDbContextFactory<T>` with all concurrency
  settings. Recommended pattern for background services, `Task.WhenAll`, and `Channel<T>` consumers.
- Structured logging for `SQLITE_BUSY*` events via `ILoggerFactory` (resolved from DI automatically).
- `GetWalCheckpointStatusAsync` — runs `PRAGMA wal_checkpoint(PASSIVE)` and returns a typed
  `WalCheckpointStatus` (IsBusy, TotalWalFrames, CheckpointedFrames, CheckpointProgress).
- `TryReleaseMigrationLockAsync` — detects and optionally clears a stale `__EFMigrationsLock` row
  left by a crashed migration process.
- `SynchronousMode` option — configures `PRAGMA synchronous` (Off / Normal / Full / Extra).
- `UpgradeTransactionsToImmediate` option — opt out of the `BEGIN → BEGIN IMMEDIATE` rewrite.

### Fixed
- `SQLITE_BUSY_SNAPSHOT` (extended code 517) now correctly restarts the full operation lambda
  instead of retrying the same statement — the only correct fix for a stale WAL read snapshot.
- Exponential backoff now uses full jitter (`[baseDelay, 2×baseDelay]`) to prevent thundering herd.

---

## [10.0.2] - 2025-12-01

### Added
- Startup validation for `SqliteConcurrencyOptions` — invalid values (`MaxRetryAttempts ≤ 0`,
  negative `BusyTimeout`) now throw `ArgumentOutOfRangeException` at startup.

### Fixed
- `Cache=Shared` in connection strings now throws `ArgumentException` at startup (was silently
  breaking WAL mode semantics in prior versions).

---

## [10.0.1] - 2025-11-01

### Fixed
- Corrected minor bug causing READ-ONLY lock file error on startup (`PrepareForConnectionOpen`
  now strips `FileAttributes.ReadOnly` and deletes stale `.db-shm` files).

---

## [10.0.0] - 2025-10-01

### Added
- Initial release.
- `UseSqliteWithConcurrency` — drop-in replacement for `UseSqlite` with WAL mode, write
  serialization, `BEGIN IMMEDIATE` transaction upgrade, and exponential-backoff retry.
- `AddConcurrentSqliteDbContext<T>` — DI registration for request-scoped workloads.
- `BulkInsertOptimizedAsync` — batched bulk insert with WAL-optimized transactions.
- `SaveChangesSerializedAsync` — explicit serialized save with retry.
- `ExecuteWithRetryAsync` — generic operation retry wrapper.
- `ThreadSafeSqliteContext<T>` — optional base `DbContext` with write serialization built in.
- `ThreadSafeFactory` — DI-free factory for non-DI environments.
- Per-database `SemaphoreSlim` write lock via `SqliteConnectionEnhancer`.
- `SqliteDiagnostics` — opt-in Spectre.Console diagnostics UI (`-p:IncludeSpectre=true`).
- `MemoryPackExtensions` — opt-in MemoryPack serialization (`-p:IncludeMemoryPack=true`).

[Unreleased]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.1.1...HEAD
[10.1.1]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.1.0...v10.1.1
[10.1.0]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.0.3...v10.1.0
[10.0.3]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.0.2...v10.0.3
[10.0.2]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.0.1...v10.0.2
[10.0.1]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/compare/v10.0.0...v10.0.1
[10.0.0]: https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/releases/tag/v10.0.0
