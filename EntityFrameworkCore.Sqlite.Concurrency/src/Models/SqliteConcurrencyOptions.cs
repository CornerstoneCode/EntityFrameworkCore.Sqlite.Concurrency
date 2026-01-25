namespace EntityFrameworkCore.Sqlite.Concurrency.Models;

/// <summary>
/// Options for configuring SQLite concurrency and performance.
/// </summary>
public class SqliteConcurrencyOptions : IEquatable<SqliteConcurrencyOptions>
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

    /// <inheritdoc />
    public bool Equals(SqliteConcurrencyOptions? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MaxRetryAttempts == other.MaxRetryAttempts && 
               BusyTimeout.Equals(other.BusyTimeout) && 
               CommandTimeout == other.CommandTimeout && 
               WalAutoCheckpoint == other.WalAutoCheckpoint;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((SqliteConcurrencyOptions)obj);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(MaxRetryAttempts, BusyTimeout, CommandTimeout, WalAutoCheckpoint);
    }
}