namespace EFCore.Sqlite.Concurrency.Benchmarks;

public class BenchmarkEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Payload { get; set; } = string.Empty;
    public int Value { get; set; }
}
