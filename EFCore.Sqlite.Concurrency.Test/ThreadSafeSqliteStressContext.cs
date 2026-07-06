using EntityFrameworkCore.Sqlite.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace EFCore.Sqlite.Concurrency.Test;

/// <summary>
/// Concrete subclass of <see cref="ThreadSafeSqliteContext{TContext}"/> for stress tests.
/// </summary>
public class ThreadSafeSqliteStressContext : ThreadSafeSqliteContext<ThreadSafeSqliteStressContext>
{
    public ThreadSafeSqliteStressContext(string connectionString) : base(connectionString) { }

    public DbSet<StressEntity> Entities => Set<StressEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<StressEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasMaxLength(256);
        });
}
