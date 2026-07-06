using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Benchmarks;

/// <summary>Plain UseSqlite context — baseline comparison without concurrency features.</summary>
public class BaselineSqliteContext : DbContext
{
    private readonly string _connectionString;

    public BaselineSqliteContext(string connectionString) => _connectionString = connectionString;

    public DbSet<BenchmarkEntity> Entities => Set<BenchmarkEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(_connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BenchmarkEntity>(e => e.HasKey(x => x.Id));
}
