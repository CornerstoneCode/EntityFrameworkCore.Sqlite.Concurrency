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
    public static DbContextOptionsBuilder UseSqliteWithConcurrency(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
    {
        var options = new SqliteConcurrencyOptions();
        configure?.Invoke(options);

        // Get the enhanced connection string
        var enhancedConnectionString = SqliteConnectionEnhancer
            .GetOptimizedConnectionString(connectionString);
        
        // Use the connection string with EF Core to allow proper pooling
        optionsBuilder.UseSqlite(enhancedConnectionString, sqliteOptions =>
        {
            // Configure command timeout
            sqliteOptions.CommandTimeout(options.CommandTimeout);
        });
        
        // Add interceptors for PRAGMAs, performance, and concurrency
        var interceptor = SqliteConnectionEnhancer.GetInterceptor(enhancedConnectionString, options);
        optionsBuilder.AddInterceptors(interceptor);
        
        return optionsBuilder;
    }
 
    

    /// <summary>
    /// Executes an operation with automatic retry on SQLITE_BUSY errors.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="context">The database context.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="maxRetries">The maximum number of retries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
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
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries)
            {
                attempt++;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
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

            // Batch inserts
            var batchSize = 1000;
            var batches = entities.Chunk(batchSize);

            foreach (var batch in batches)
            {
                await context.AddRangeAsync(batch, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
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