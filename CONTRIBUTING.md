# Contributing to EntityFrameworkCore.Sqlite.Concurrency

Thank you for your interest in contributing. This document covers prerequisites, build and test commands, benchmark instructions, and the PR process.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- Any editor (Visual Studio, Rider, VS Code with C# Dev Kit)

---

## Build

```bash
# Restore (locked packages enforced)
dotnet restore EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj

# Build (Release)
dotnet build EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj -c Release

# Pack NuGet package
dotnet pack EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj -c Release --no-build -o out
```

Optional feature flags:

```bash
dotnet build ... -p:IncludeMemoryPack=true   # enables MemoryPackExtensions.cs
dotnet build ... -p:IncludeSpectre=true       # enables SqliteDiagnostics.cs
```

---

## Tests

```bash
dotnet test EFCore.Sqlite.Concurrency.Test/EFCore.Sqlite.Concurrency.Test.csproj --framework net10.0 -c Release
```

The test suite contains four correctness-under-load stress tests covering all write paths at 50 concurrent writers with isolated temp databases. All four must pass before submitting a PR.

---

## Benchmarks

The `EFCore.Sqlite.Concurrency.Benchmarks/` project uses [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure real performance. Always run in Release mode.

```bash
cd EFCore.Sqlite.Concurrency.Benchmarks

# Full precision run — used for README/docs numbers (slow: 10–30 min)
dotnet run -c Release -- --filter *

# Quick developer run — 3 iterations, good for relative comparisons
dotnet run -c Release -- --job short --filter *

# Run a specific benchmark class
dotnet run -c Release -- --filter *BulkInsert*
dotnet run -c Release -- --filter *Concurrent*
dotnet run -c Release -- --filter *Read*
```

Results are written to `BenchmarkDotNet.Artifacts/results/` as markdown, CSV, and JSON.

---

## PR Process

1. **Fork** the repository and create a feature branch from `main`.
2. **Write tests** — all new behavior must be covered by the stress test project or a new test class.
3. **Run the full test suite** — `dotnet test` must pass with 0 failures.
4. **Update `CHANGELOG.md`** — add your change under `[Unreleased]` in the appropriate subsection (Added / Fixed / Changed).
5. **Update XML doc comments** — all public APIs must have `<summary>` and `<param>` tags. Missing doc comments produce build warnings that are treated as errors in CI.
6. **Submit PR** — use the PR template. One PR per concern.

---

## Commit Message Convention

```
type: short description (imperative, under 72 chars)

Optional body explaining why, not what.
```

Types: `feat`, `fix`, `docs`, `ci`, `refactor`, `perf`, `test`, `bump`

---

## Architecture Notes

See [CLAUDE.md](CLAUDE.md) for a full component map and key design decisions.

Key invariants to preserve:
- **One write queue per database file** — keyed by normalized connection string in `SqliteConnectionEnhancer._writeQueues`
- **`IsWriteLockHeld` reentrancy guard** — must be set `true` by the queue writer before executing each request, and `false` in `finally`
- **Options equality excludes `LoggerFactory`** — enforced in `SqliteConcurrencyOptions.Equals`
- **`Cache=Shared` must be rejected at startup** — incompatible with WAL mode
