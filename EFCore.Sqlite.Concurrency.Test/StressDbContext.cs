using EntityFrameworkCore.Sqlite.Concurrency;
using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Test;

public class StressDbContext : DbContext
{
    private readonly string _connectionString;

    public StressDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<StressEntity> Entities => Set<StressEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqliteWithConcurrency(_connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<StressEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasMaxLength(256);
        });
}
