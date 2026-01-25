namespace EntityFrameworkCore.Sqlite.Concurrency.Models;

/// <summary>
/// Options for configuring SQLite concurrency and performance.
/// </summary>
public class SqliteConcurrencyOptions
{
    /// <summary>
    /// The maximum number of retry attempts for SQLITE_BUSY errors.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// The busy timeout for SQLite connections.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The command timeout for SQLite commands.
    /// </summary>
    public int CommandTimeout { get; set; } = 300; // 5 minutes

    /// <summary>
    /// The number of pages for WAL auto-checkpoint.
    /// </summary>
    public int WalAutoCheckpoint { get; set; } = 1000;
}