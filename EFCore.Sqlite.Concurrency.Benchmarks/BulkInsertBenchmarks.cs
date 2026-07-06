using BenchmarkDotNet.Attributes;
using EntityFrameworkCore.Sqlite.Concurrency;

namespace EFCore.Sqlite.Concurrency.Benchmarks;

/// <summary>
/// Compares BulkInsertOptimizedAsync against naive per-entity SaveChanges.
/// Both benchmarks use a single writer — this measures batching + WAL gains only,
/// not write serialization (which has no baseline to compare against).
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class BulkInsertBenchmarks
{
    [Params(1_000, 10_000)]
    public int EntityCount { get; set; }

    private string _baselinePath = string.Empty;
    private string _concurrentPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _baselinePath = Path.Combine(Path.GetTempPath(), $"bench_baseline_{Guid.NewGuid():N}.db");
        _concurrentPath = Path.Combine(Path.GetTempPath(), $"bench_concurrent_{Guid.NewGuid():N}.db");

        using var baseline = new BaselineSqliteContext($"Data Source={_baselinePath}");
        baseline.Database.EnsureCreated();

        using var concurrent = new ConcurrentSqliteContext($"Data Source={_concurrentPath}");
        concurrent.Database.EnsureCreated();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            TryDelete(_baselinePath + ext);
            TryDelete(_concurrentPath + ext);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Clear rows between iterations so counts stay stable
        using var b = new BaselineSqliteContext($"Data Source={_baselinePath}");
        b.Entities.RemoveRange(b.Entities);
        b.SaveChanges();

        using var c = new ConcurrentSqliteContext($"Data Source={_concurrentPath}");
        c.Entities.RemoveRange(c.Entities);
        c.SaveChanges();
    }

    /// <summary>Baseline: one SaveChanges call per entity — the common antipattern.</summary>
    [Benchmark(Baseline = true, Description = "Plain EF Core (SaveChanges per entity)")]
    public async Task Baseline_SaveChangesPerEntity()
    {
        await using var ctx = new BaselineSqliteContext($"Data Source={_baselinePath}");
        for (var i = 0; i < EntityCount; i++)
        {
            ctx.Entities.Add(new BenchmarkEntity { Payload = $"item-{i}", Value = i });
            await ctx.SaveChangesAsync();
        }
    }

    /// <summary>Package: BulkInsertOptimizedAsync — batched, WAL-tuned, ChangeTracker cleared.</summary>
    [Benchmark(Description = "BulkInsertOptimizedAsync")]
    public async Task Package_BulkInsertOptimizedAsync()
    {
        await using var ctx = new ConcurrentSqliteContext($"Data Source={_concurrentPath}");
        var entities = Enumerable.Range(0, EntityCount)
            .Select(i => new BenchmarkEntity { Payload = $"item-{i}", Value = i })
            .ToList();
        await ctx.BulkInsertOptimizedAsync(entities);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }
}
