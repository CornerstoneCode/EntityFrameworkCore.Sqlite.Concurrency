using EntityFrameworkCore.Sqlite.Concurrency;

namespace EFCore.Sqlite.Concurrency.Test;

public sealed class OverrideSaveChangesDbContext(string connectionString) : StressDbContext(connectionString)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        this.SaveChangesSerializedAsync(
            ct => base.SaveChangesAsync(ct),
            cancellationToken: cancellationToken);
}
