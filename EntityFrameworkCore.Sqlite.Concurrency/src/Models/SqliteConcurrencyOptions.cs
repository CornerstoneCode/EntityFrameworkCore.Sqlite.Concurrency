namespace EntityFrameworkCore.Sqlite.Concurrency.Models;

public class SqliteConcurrencyOptions
{
    public bool UseWriteQueue { get; set; } = true;
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableWalCheckpointManagement { get; set; } = true;
    public int CommandTimeout { get; set; } = 300; // 5 minutes
    public int WalAutoCheckpoint { get; set; } = 1000;
    public bool EnableMemoryPack { get; set; } = false;
}