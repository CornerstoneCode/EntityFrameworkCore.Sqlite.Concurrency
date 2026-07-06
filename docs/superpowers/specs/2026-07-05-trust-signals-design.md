# Trust Signals — Design Spec

**Date:** 2026-07-05
**Track:** A — Package quality signals and credibility
**Scope:** CI test integration, BenchmarkDotNet project, CHANGELOG, community files, README badge/numbers update

---

## Goal

Close the gap between a technically excellent library and one that *looks* professionally maintained. A developer who lands on the GitHub repo or NuGet page should see: tests passing in CI, real reproducible benchmark numbers, a standard changelog, and community scaffolding that signals the project welcomes contributors and bug reports.

---

## Section 1: CI Workflow Update

**File:** `.github/workflows/ci.yml`

Add after the existing Build step:

```yaml
- name: Test
  run: dotnet test EFCore.Sqlite.Concurrency.Test/EFCore.Sqlite.Concurrency.Test.csproj --framework net10.0 -c Release --no-build --logger "trx;LogFileName=results.xml"

- name: Publish Test Results
  uses: dorny/test-reporter@v1
  if: always()
  with:
    name: xUnit Tests
    path: "**/*.trx"
    reporter: dotnet-trx
```

The benchmark project is explicitly excluded from CI (too slow for CI; it is a developer tool).

**README:** Add GitHub Actions badge for the CI workflow next to existing badges:
```markdown
[![CI](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/actions/workflows/ci.yml/badge.svg)](https://github.com/CornerstoneCode/EntityFrameworkCore.Sqlite.Concurrency/actions/workflows/ci.yml)
```

---

## Section 2: BenchmarkDotNet Project

**Directory:** `EFCore.Sqlite.Concurrency.Benchmarks/` (new console app)

### Files

| File | Purpose |
|---|---|
| `EFCore.Sqlite.Concurrency.Benchmarks.csproj` | net10.0 console app, BenchmarkDotNet + project ref |
| `Program.cs` | `BenchmarkRunner.Run` entry point with arg switching |
| `BenchmarkConfig.cs` | `ShortRunConfig` (1 warmup, 3 iterations) for quick developer runs |
| `BaselineSqliteContext.cs` | Plain `UseSqlite` DbContext for baseline comparison |
| `BenchmarkEntity.cs` | Simple entity used across all benchmarks |
| `BulkInsertBenchmarks.cs` | Sequential bulk insert: package vs baseline, Params(1000, 10000) |
| `ConcurrentWriteBenchmarks.cs` | 50 concurrent `SaveChangesSerializedAsync` writers |
| `ReadBenchmarks.cs` | 20 parallel reads under concurrent write load |

### Benchmark Design

**BulkInsertBenchmarks:** Compares `BulkInsertOptimizedAsync` against naively looping `SaveChanges()` one entity at a time (the common antipattern). Both use the same temp DB file. Params: 1,000 and 10,000 entities.

**ConcurrentWriteBenchmarks:** 50 tasks each writing 10 rows via `SaveChangesSerializedAsync`. Measures total time for all 500 rows to commit. No baseline (plain SQLite throws SQLITE_BUSY under this load — the baseline IS the error).

**ReadBenchmarks:** 20 parallel readers each running `ToListAsync()` while 5 background writers are active. Measures reader throughput in WAL mode.

### Running

```bash
cd EFCore.Sqlite.Concurrency.Benchmarks
dotnet run -c Release -- --filter * --job short
```

Full precision run (slower, used for README numbers):
```bash
dotnet run -c Release -- --filter *
```

---

## Section 3: CHANGELOG.md

**File:** `CHANGELOG.md` at repo root. Keep A Changelog format (https://keepachangelog.com).

Structure:
- `[Unreleased]`
- `[10.1.0] - 2026-07-05`
- `[10.0.3]`
- `[10.0.2]`
- `[10.0.1]`
- `[10.0.0]`

Each version has subsections: Added, Fixed, Changed, Removed, Security. Content sourced from existing `doc/v*.md` files.

---

## Section 4: Community Files

| File | Content |
|---|---|
| `CONTRIBUTING.md` | Prerequisites, build commands, test commands, benchmark run instructions, PR checklist, commit message convention |
| `SECURITY.md` | How to report vulnerabilities (email, not public issues), response SLA |
| `.github/ISSUE_TEMPLATE/bug_report.yml` | Fields: version, .NET version, error message, connection string (redacted), reproduction steps, expected vs actual |
| `.github/ISSUE_TEMPLATE/feature_request.yml` | Fields: problem description, proposed solution, alternatives considered |
| `.github/PULL_REQUEST_TEMPLATE.md` | Checklist: tests added, CHANGELOG updated, XML docs updated, no breaking changes or documented |

---

## Section 5: README + Docs Updates

**README:** Add CI badge. After benchmarks run, replace "Typical Results" table with real BenchmarkDotNet output (mean times, ratio column). Add footnote with exact benchmark environment and command to reproduce.

**docs/performance-guide.md:** Add "Running the Benchmarks" section linking to the benchmark project with the run command.

---

## File Change Summary

| Action | File |
|---|---|
| Modify | `.github/workflows/ci.yml` |
| Create | `EFCore.Sqlite.Concurrency.Benchmarks/` (8 files) |
| Create | `CHANGELOG.md` |
| Create | `CONTRIBUTING.md` |
| Create | `SECURITY.md` |
| Create | `.github/ISSUE_TEMPLATE/bug_report.yml` |
| Create | `.github/ISSUE_TEMPLATE/feature_request.yml` |
| Create | `.github/PULL_REQUEST_TEMPLATE.md` |
| Modify | `README.md` (badge + benchmark table) |
| Modify | `docs/performance-guide.md` (benchmark run instructions) |

---

## Out of Scope

- Running benchmarks in CI (too slow; developer tool only)
- Automated benchmark regression tracking
- Code coverage reporting
- API reference site generation
