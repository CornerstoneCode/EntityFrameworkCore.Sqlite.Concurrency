using BenchmarkDotNet.Attributes;
using EntityFrameworkCore.Sqlite.Concurrency;

namespace EFCore.Sqlite.Concurrency.Benchmarks;

/// <summary>
/// Measures throughput of concurrent writers via SaveChangesSerializedAsync.
/// No baseline: plain SQLite throws SQLITE_BUSY under this load — the baseline IS the error.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class ConcurrentWriteBenchmarks
{
    [Params(10, 50)]
    public int ConcurrentWriters { get; set; }

    private const int RowsPerWriter = 10;
    private string _dbPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_concurrent_writes_{Guid.NewGuid():N}.db");
        using var ctx = new ConcurrentSqliteContext($"Data Source={_dbPath}");
        ctx.Database.EnsureCreated();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var ext in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + ext)) try { File.Delete(_dbPath + ext); } catch { }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        using var ctx = new ConcurrentSqliteContext($"Data Source={_dbPath}");
        ctx.Entities.RemoveRange(ctx.Entities);
        ctx.SaveChanges();
    }

    /// <summary>N concurrent writers each writing RowsPerWriter rows via the write queue.</summary>
    [Benchmark(Description = "Concurrent SaveChangesSerializedAsync")]
    public async Task ConcurrentSaveChangesSerializedAsync()
    {
        var cs = $"Data Source={_dbPath}";
        var tasks = Enumerable.Range(0, ConcurrentWriters).Select(async writerIdx =>
        {
            await using var ctx = new ConcurrentSqliteContext(cs);
            var entities = Enumerable.Range(0, RowsPerWriter)
                .Select(i => new BenchmarkEntity { Payload = $"w{writerIdx}-r{i}", Value = writerIdx * 100 + i })
                .ToList();
            ctx.Entities.AddRange(entities);
            await ctx.SaveChangesSerializedAsync();
        });
        await Task.WhenAll(tasks);
    }
}
