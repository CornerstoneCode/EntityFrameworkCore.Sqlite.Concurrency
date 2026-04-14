using System.Data;
using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EntityFrameworkCore.Sqlite.Concurrency;

/// <summary>
/// Extension methods for configuring SQLite concurrency and performance in EF Core.
/// </summary>
public static class SqliteConcurrencyExtensions
{
    /// <summary>
    /// Configures the context to use SQLite with optimized concurrency and performance settings.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="configure">An optional action to configure concurrency options.</param>
    /// <returns>The options builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any option value is outside its valid range.
    /// </exception>
    public static DbContextOptionsBuilder UseSqliteWithConcurrency(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
    {
        var options = new SqliteConcurrencyOptions();
        configure?.Invoke(options);
        options.Validate();

        // Get the enhanced connection string
        var enhancedConnectionString = SqliteConnectionEnhancer
            .GetOptimizedConnectionString(connectionString);

        // Use the connection string with EF Core to allow proper pooling
        optionsBuilder.UseSqlite(enhancedConnectionString, sqliteOptions =>
        {
            sqliteOptions.CommandTimeout(options.CommandTimeout);
        });

        // Add interceptors for PRAGMAs, performance, and concurrency
        var interceptor = SqliteConnectionEnhancer.GetInterceptor(enhancedConnectionString, options);
        optionsBuilder.AddInterceptors(interceptor);

        return optionsBuilder;
    }


    /// <summary>
    /// Executes an operation with automatic retry on <c>SQLITE_BUSY</c> and
    /// <c>SQLITE_BUSY_SNAPSHOT</c> errors.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="context">The database context.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="maxRetries">
    /// The maximum number of retry attempts. Each retry waits using exponential backoff
    /// with jitter starting at 100 ms.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    /// <remarks>
    /// <para>
    /// Two classes of busy error are handled:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>SQLITE_BUSY / SQLITE_BUSY_RECOVERY / SQLITE_BUSY_TIMEOUT</b> — another
    ///     connection holds a lock. The operation is retried after a backoff delay.
    ///   </item>
    ///   <item>
    ///     <b>SQLITE_BUSY_SNAPSHOT</b> — the connection's read snapshot became stale
    ///     after another writer committed. The entire operation is restarted so that it
    ///     can acquire a fresh snapshot. Any data read in the failed attempt must be
    ///     re-queried inside the operation lambda.
    ///   </item>
    /// </list>
    /// <para>
    ///   <b>SQLITE_LOCKED</b> (same-connection conflict) is not retried and propagates
    ///   immediately, as it indicates an application-level bug.
    /// </para>
    /// </remarks>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        this DbContext context,
        Func<DbContext, Task<T>> operation,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await operation(context);
            }
            catch (SqliteException ex) when (SqliteErrorCodes.IsAnyBusy(ex) && attempt < maxRetries)
            {
                // SQLITE_LOCKED (code 6) is not caught here — it is an app-level bug and
                // should not be silently retried.

                attempt++;
                var isSnapshot = SqliteErrorCodes.IsBusySnapshot(ex);

                // Exponential backoff with full jitter: delay is uniformly distributed in
                // [baseDelay, 2×baseDelay] to prevent synchronized retry storms ("thundering
                // herd") when multiple threads hit contention simultaneously.
                var baseDelay = 100 * Math.Pow(2, attempt);
                var jitter    = Random.Shared.NextDouble() * baseDelay;
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), cancellationToken);

                // For SQLITE_BUSY_SNAPSHOT the operation lambda will restart on the next
                // loop iteration, which is correct: it allows the caller to re-query any
                // data that may now be stale.
                _ = isSnapshot; // consumed for documentation clarity
            }
        }
    }

    /// <summary>
    /// Performs a bulk insert with optimized settings and optional app-level locking.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="context">The database context.</param>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task BulkInsertOptimizedAsync<T>(
        this DbContext context,
        IEnumerable<T> entities,
        CancellationToken cancellationToken = default) where T : class
    {
        if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
        {
            await context.AddRangeAsync(entities, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            return;
        }

        var connectionString = context.Database.GetDbConnection().ConnectionString;
        var enhancedConnectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(connectionString);
        var writeLock = SqliteConnectionEnhancer.GetWriteLock(enhancedConnectionString);

        await writeLock.WaitAsync(cancellationToken);
        SqliteConnectionEnhancer.IsWriteLockHeld.Value = true;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var batchSize = 1000;
            var batches = entities.Chunk(batchSize);

            foreach (var batch in batches)
            {
                await context.AddRangeAsync(batch, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
            writeLock.Release();
        }
    }
}
