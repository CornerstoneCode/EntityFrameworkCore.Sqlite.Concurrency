using System.Data;
using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    /// Saves all changes in the context while holding the shared per-database write lock,
    /// serializing concurrent writers at the application level before they contend in SQLite.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="maxRetries">
    /// Maximum number of retry attempts if <c>SQLITE_BUSY</c> is returned even after the
    /// write lock is held. Uses exponential backoff starting at 50 ms, capped at 2 000 ms.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <remarks>
    /// <para>
    /// Use this method instead of <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// in background workers or other scenarios where multiple concurrent callers write to
    /// the same SQLite database. The shared lock ensures that at most one writer is active
    /// per database file at any point, avoiding SQLite-level busy-wait storms when many
    /// writers contend simultaneously.
    /// </para>
    /// <para>
    /// If the calling code is already inside a <see cref="BulkInsertOptimizedAsync{T}"/>
    /// (or any other scope that already holds the write lock), the method skips lock
    /// acquisition to prevent deadlocks.
    /// </para>
    /// </remarks>
    public static async Task<int> SaveChangesSerializedAsync(
        this DbContext context,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
            return await context.SaveChangesAsync(cancellationToken);

        var connectionString = context.Database.GetDbConnection().ConnectionString;
        var enhancedConnectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(connectionString);
        var writeLock = SqliteConnectionEnhancer.GetWriteLock(enhancedConnectionString);

        await writeLock.WaitAsync(cancellationToken);
        SqliteConnectionEnhancer.IsWriteLockHeld.Value = true;

        try
        {
            var delayMs = 50;
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableSqliteBusy(ex))
                {
                    await Task.Delay(delayMs, cancellationToken);
                    delayMs = Math.Min(delayMs * 2, 2000);
                }
            }
        }
        finally
        {
            SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
            writeLock.Release();
        }
    }

    // EF Core wraps SqliteException in DbUpdateException when SaveChangesAsync fails,
    // so we need to unwrap one level to classify the error.
    private static bool IsRetryableSqliteBusy(Exception ex) =>
        ex switch
        {
            SqliteException se                                          => SqliteErrorCodes.IsRetryableBusy(se),
            DbUpdateException { InnerException: SqliteException inner } => SqliteErrorCodes.IsRetryableBusy(inner),
            _                                                           => false
        };

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
