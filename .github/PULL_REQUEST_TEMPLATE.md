## Summary

<!-- What does this PR do? One paragraph. -->

## Type of Change

- [ ] Bug fix (non-breaking — fixes incorrect behavior)
- [ ] New feature (non-breaking — adds functionality)
- [ ] Breaking change (changes existing public API or behavior)
- [ ] Documentation update
- [ ] CI / build change
- [ ] Performance improvement

## Testing

- [ ] New tests added covering the change
- [ ] All existing tests pass (`dotnet test EFCore.Sqlite.Concurrency.Test/`)
- [ ] If performance-sensitive: benchmarks run and results included below

## Checklist

- [ ] `CHANGELOG.md` updated under `[Unreleased]`
- [ ] XML doc comments added/updated for all public API changes
- [ ] No `Cache=Shared` introduced in any connection string
- [ ] `packages.lock.json` updated if dependencies changed (`dotnet restore`)
- [ ] Breaking changes documented (or none — confirm below)

## Breaking Changes

<!-- List any breaking changes, or write "None." -->

## Benchmark Results (if applicable)

<!-- Paste BenchmarkDotNet output here if this is a performance change -->
