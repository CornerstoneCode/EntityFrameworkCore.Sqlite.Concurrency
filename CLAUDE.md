# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore (locked packages enforced via packages.lock.json)
dotnet restore EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj

# Build
dotnet build EntityFrameworkCore.Sqlite.Concurrency.sln -c Release

# Pack NuGet package
dotnet pack EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj -c Release --no-build -o out

# Build with optional dependencies
dotnet build ... -p:IncludeMemoryPack=true   # enables MemoryPackExtensions.cs
dotnet build ... -p:IncludeSpectre=true       # enables SqliteDiagnostics.cs (Spectre.Console)
```

**No test projects exist.** CI only restores, builds, and packs.

## Project Overview

This is a .NET 10 NuGet library (`EntityFrameworkCore.Sqlite.Concurrency`) that adds write serialization, WAL-mode optimization, retry logic, and bulk-insert performance on top of `Microsoft.EntityFrameworkCore.Sqlite`. It is a pure library — no apps, no test harness.

Target: `net10.0` | Root namespace: `EntityFrameworkCore.Sqlite.Concurrency`

## Architecture

All source lives in `EntityFrameworkCore.Sqlite.Concurrency/src/`.

### Component Map

| File | Role |
|---|---|
| `SqliteConcurrencyOptions.cs` | Central configuration POCO; `Validate()` called at startup |
| `SqliteConnectionEnhancer.cs` | Process-global registry; owns all shared static state |
| `SqliteConcurrencyInterceptor.cs` | EF Core interceptor wiring; applies PRAGMAs and rewrites `BEGIN` |
| `SqliteConcurrencyExtensions.cs` | Public API — `UseSqliteWithConcurrency()`, `SaveChangesSerializedAsync()`, `ExecuteWithRetryAsync()`, `BulkInsertOptimizedAsync()` |
| `SqliteConcurrencyServiceCollectionExtensions.cs` | DI registration (`AddConcurrentSqliteDbContext<T>()`, `AddConcurrentSqliteDbContextFactory<T>()`) |
| `ThreadSafeFactory.cs` | DI-free factory for environments without `IServiceCollection` |
| `ThreadSafeSqliteContext<TContext>` | Optional generic base `DbContext` with write/read/bulk APIs built in |
| `SqliteErrorCodes.cs` | Internal constants and classifiers for SQLite extended error codes |
| `MemoryPackExtensions.cs` | `#if INCLUDEMEMORYPACK` — opt-in MemoryPack integration |
| `SqliteDiagnostics.cs` | `#if INCLUDESPECTRE` — opt-in Spectre.Console diagnostics UI |

### Key Design Decisions

**Write serialization has two layers:**
1. Application-level: one `SemaphoreSlim(1,1)` per database file stored in `SqliteConnectionEnhancer._writeLocks`, keyed by normalized connection string.
2. SQLite-level: `busy_timeout` PRAGMA applied on every connection open.

**`SqliteConnectionEnhancer` is the process-global brain:**
- `_writeLocks` — write-lock registry (one semaphore per DB file)
- `_interceptors` — enforces consistent options; throws `ArgumentException` on option mismatch for same DB
- `_initializedDatabases` / `_preparedDatabases` — once-per-process guards (database-scoped PRAGMAs applied once; connection-scoped PRAGMAs applied on every `ConnectionOpened`)
- `_connectionStringCache` — memoizes optimized connection strings; rejects `Cache=Shared` (incompatible with WAL)
- `IsWriteLockHeld` (`AsyncLocal<bool>`) — reentrancy guard preventing deadlock on nested write calls

**`SQLITE_BUSY_SNAPSHOT` requires full operation restart** (not just statement retry) because the read snapshot is stale. `ExecuteWithRetryAsync()` reruns the entire user-supplied lambda.

**Pre-open file preparation** (`PrepareForConnectionOpen`) strips `FileAttributes.ReadOnly` and deletes stale `.db-shm` — handles a Windows MSBuild quirk where `CopyToOutputDirectory` marks the DB read-only.

**Options equality excludes `LoggerFactory`** — used to enforce consistent interceptor registration per database; logger infrastructure is not behavioral identity.

**Conditional compilation** keeps the default package lean:
- `#if INCLUDEMEMORYPACK` — requires `-p:IncludeMemoryPack=true`
- `#if INCLUDESPECTRE` — requires `-p:IncludeSpectre=true`

### Retry Backoff

Exponential backoff with full jitter: delay ∈ `[baseDelay, 2×baseDelay]` using `Random.Shared`. Prevents thundering-herd storms on concurrent retries.

## Project File Notes

- `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` — always update `packages.lock.json` after adding/changing dependencies
- `<Nullable>enable</Nullable>` and `<GenerateDocumentationFile>true</GenerateDocumentationFile>` — missing XML doc comments produce build warnings
- `<ContinuousIntegrationBuild>` activates deterministic builds on GitHub Actions
- SourceLink and `EmbedUntrackedSources` are configured for NuGet symbol packages
