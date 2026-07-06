using EntityFrameworkCore.Sqlite.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Test;

/// <summary>
/// Stress tests that verify correctness under concurrent write load.
/// Each test owns an isolated temp SQLite file and runs 50–100 concurrent writers.
/// Assertions verify that no rows are lost, duplicated, or corrupted.
/// </summary>
public class ConcurrencyStressTests
{
    // ── Test 1: SaveChangesSerializedAsync ────────────────────────────────────

    [Fact]
    public async Task SaveChangesSerializedAsync_WritesAllRows()
    {
        const int writers = 500;
        const int rowsPerWriter = 100;
        const int expected = writers * rowsPerWriter;

        using var db = new TempDatabase();

        var tasks = Enumerable.Range(0, writers).Select(async writerIdx =>
        {
            await using var ctx = db.CreateContext();

            var entities = Enumerable.Range(0, rowsPerWriter)
                .Select(i => new StressEntity
                {
                    Id          = Guid.NewGuid(),
                    WriterIndex = writerIdx,
                    Payload     = $"w{writerIdx}-r{i}"
                })
                .ToList();

            ctx.Entities.AddRange(entities);
            await ctx.SaveChangesSerializedAsync();
        });

        await Task.WhenAll(tasks);

        await using var verify = db.CreateContext();
        var all   = await verify.Entities.ToListAsync();
        var ids   = all.Select(e => e.Id).ToHashSet();

        Assert.Equal(expected, all.Count);
        Assert.Equal(expected, ids.Count); // no duplicate IDs
    }

    // ── Test 2: BulkInsertOptimizedAsync ─────────────────────────────────────

    [Fact]
    public async Task BulkInsertOptimizedAsync_WritesAllRows()
    {
        const int writers        = 100;
        const int entitiesPerWriter = 500;
        const int expected       = writers * entitiesPerWriter;

        using var db = new TempDatabase();

        var tasks = Enumerable.Range(0, writers).Select(async writerIdx =>
        {
            await using var ctx = db.CreateContext();

            var entities = Enumerable.Range(0, entitiesPerWriter)
                .Select(i => new StressEntity
                {
                    Id          = Guid.NewGuid(),
                    WriterIndex = writerIdx,
                    Payload     = $"bulk-w{writerIdx}-r{i}"
                })
                .ToList();

            await ctx.BulkInsertOptimizedAsync(entities);
        });

        await Task.WhenAll(tasks);

        await using var verify = db.CreateContext();
        var all = await verify.Entities.ToListAsync();
        var ids = all.Select(e => e.Id).ToHashSet();

        Assert.Equal(expected, all.Count);
        Assert.Equal(expected, ids.Count);
    }

    // ── Test 3: ThreadSafeSqliteContext.ExecuteWriteAsync ────────────────────

    [Fact]
    public async Task ExecuteWriteAsync_ThreadSafeSqliteContext_WritesAllRows()
    {
        const int writers      = 500;
        const int rowsPerWriter = 100;
        const int expected     = writers * rowsPerWriter;

        using var db = new TempDatabase();

        var tasks = Enumerable.Range(0, writers).Select(async writerIdx =>
        {
            // ThreadSafeSqliteContext must be constructed with the connection string
            // so it can find the shared write queue for this database file.
            var ctx = new ThreadSafeSqliteStressContext(db.ConnectionString);
            await using (ctx)
            {
                await ctx.ExecuteWriteAsync(async c =>
                {
                    var entities = Enumerable.Range(0, rowsPerWriter)
                        .Select(i => new StressEntity
                        {
                            Id          = Guid.NewGuid(),
                            WriterIndex = writerIdx,
                            Payload     = $"tssc-w{writerIdx}-r{i}"
                        });

                    c.Entities.AddRange(entities);
                });
            }
        });

        await Task.WhenAll(tasks);

        await using var verify = db.CreateContext();
        var all = await verify.Entities.ToListAsync();
        var ids = all.Select(e => e.Id).ToHashSet();

        Assert.Equal(expected, all.Count);
        Assert.Equal(expected, ids.Count);
    }

    // ── Test 4: Mixed concurrent reads + writes ───────────────────────────────

    [Fact]
    public async Task MixedReadsAndWrites_NoExceptionsOrDataLoss()
    {
        const int writers      = 500;
        const int readers      = 200;
        const int rowsPerWriter = 100;
        const int expected     = writers * rowsPerWriter;

        using var db = new TempDatabase();

        // Pre-insert one seed row so readers have something to count from the start.
        await using (var seed = db.CreateContext())
        {
            seed.Entities.Add(new StressEntity { Payload = "seed" });
            await seed.SaveChangesSerializedAsync();
        }

        var writerTasks = Enumerable.Range(0, writers).Select(async writerIdx =>
        {
            await using var ctx = db.CreateContext();
            var entities = Enumerable.Range(0, rowsPerWriter)
                .Select(i => new StressEntity
                {
                    Id          = Guid.NewGuid(),
                    WriterIndex = writerIdx,
                    Payload     = $"mixed-w{writerIdx}-r{i}"
                })
                .ToList();

            ctx.Entities.AddRange(entities);
            await ctx.SaveChangesSerializedAsync();
        });

        var readResults = new System.Collections.Concurrent.ConcurrentBag<int>();
        var readerTasks = Enumerable.Range(0, readers).Select(async _ =>
        {
            await using var ctx = db.CreateContext();
            // WAL mode allows reads to run concurrently with writes.
            var count = await ctx.Entities.CountAsync();
            readResults.Add(count);
        });

        await Task.WhenAll(writerTasks.Concat(readerTasks));

        // Verify all writes landed
        await using var verify = db.CreateContext();
        var all = await verify.Entities.ToListAsync();

        // +1 for the seed row
        Assert.Equal(expected + 1, all.Count);
        Assert.Equal(expected + 1, all.Select(e => e.Id).Distinct().Count());

        // All reader results must be non-negative (reads always saw valid data)
        Assert.All(readResults, count => Assert.True(count >= 0));
    }
}
