namespace EFCore.Sqlite.Concurrency.Test;

/// <summary>
/// Creates an isolated SQLite temp file for a single test. Deleted on Dispose.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _dbPath;
    private bool _disposed;

    public string ConnectionString { get; }

    public TempDatabase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"stress_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_dbPath}";

        // Create schema
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public StressDbContext CreateContext() => new(ConnectionString);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Allow EF Core connection pool to drain before deleting the file.
        Thread.Sleep(50);

        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + ext;
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
