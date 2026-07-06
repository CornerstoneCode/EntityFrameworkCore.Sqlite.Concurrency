using EntityFrameworkCore.Sqlite.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Benchmarks;

/// <summary>UseSqliteWithConcurrency context for benchmarking.</summary>
public class ConcurrentSqliteContext : DbContext
{
    private readonly string _connectionString;

    public ConcurrentSqliteContext(string connectionString) => _connectionString = connectionString;

    public DbSet<BenchmarkEntity> Entities => Set<BenchmarkEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqliteWithConcurrency(_connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BenchmarkEntity>(e => e.HasKey(x => x.Id));
}
