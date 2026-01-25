namespace EntityFrameworkCore.Sqlite.Concurrency.Models;

public class SqliteConcurrencyOptions
{
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int CommandTimeout { get; set; } = 300; // 5 minutes
    public int WalAutoCheckpoint { get; set; } = 1000;
}