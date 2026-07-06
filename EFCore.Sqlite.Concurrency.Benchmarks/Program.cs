using BenchmarkDotNet.Running;
using EFCore.Sqlite.Concurrency.Benchmarks;

// Usage:
//   dotnet run -c Release                    — full precision run (slow, for README numbers)
//   dotnet run -c Release -- --job short     — quick developer run (3 iterations)
//   dotnet run -c Release -- --filter *Bulk* — run only BulkInsertBenchmarks

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
