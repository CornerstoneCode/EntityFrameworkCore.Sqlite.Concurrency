namespace EFCore.Sqlite.Concurrency.Test;

public class StressEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int WriterIndex { get; set; }
    public string Payload { get; set; } = string.Empty;
}
