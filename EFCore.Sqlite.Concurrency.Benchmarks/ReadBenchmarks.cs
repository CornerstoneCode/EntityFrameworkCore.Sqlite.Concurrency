using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Benchmarks;

/// <summary>
/// Compares parallel read throughput: WAL mode (package) vs default journal mode (baseline).
/// Baseline uses plain UseSqlite without WAL — reads block behind any active write.
/// Package uses WAL mode — reads run concurrently with writes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class ReadBenchmarks
{
    private const int ParallelReaders = 20;
    private const int SeedRows = 500;

    private string _baselinePath = string.Empty;
    private string _concurrentPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _baselinePath = Path.Combine(Path.GetTempPath(), $"bench_read_baseline_{Guid.NewGuid():N}.db");
        _concurrentPath = Path.Combine(Path.GetTempPath(), $"bench_read_concurrent_{Guid.NewGuid():N}.db");

        // Seed both databases with SeedRows rows
        using (var b = new BaselineSqliteContext($"Data Source={_baselinePath}"))
        {
            b.Database.EnsureCreated();
            b.Entities.AddRange(Enumerable.Range(0, SeedRows)
                .Select(i => new BenchmarkEntity { Payload = $"seed-{i}", Value = i }));
            b.SaveChanges();
        }

        using (var c = new ConcurrentSqliteContext($"Data Source={_concurrentPath}"))
        {
            c.Database.EnsureCreated();
            c.Entities.AddRange(Enumerable.Range(0, SeedRows)
                .Select(i => new BenchmarkEntity { Payload = $"seed-{i}", Value = i }));
            c.SaveChanges();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var path in new[] { _baselinePath, _concurrentPath })
        foreach (var ext in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + ext)) try { File.Delete(path + ext); } catch { }
    }

    /// <summary>Baseline: parallel reads using default journal mode (no WAL).</summary>
    [Benchmark(Baseline = true, Description = "Plain EF Core parallel reads")]
    public async Task Baseline_ParallelReads()
    {
        var cs = $"Data Source={_baselinePath}";
        var tasks = Enumerable.Range(0, ParallelReaders).Select(async _ =>
        {
            await using var ctx = new BaselineSqliteContext(cs);
            return await ctx.Entities.CountAsync();
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>Package: parallel reads using WAL mode — reads never block behind writes.</summary>
    [Benchmark(Description = "WAL-mode parallel reads")]
    public async Task Package_ParallelReads()
    {
        var cs = $"Data Source={_concurrentPath}";
        var tasks = Enumerable.Range(0, ParallelReaders).Select(async _ =>
        {
            await using var ctx = new ConcurrentSqliteContext(cs);
            return await ctx.Entities.CountAsync();
        });
        await Task.WhenAll(tasks);
    }
}
