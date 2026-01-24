using System.Data;
using EFCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.Sqlite.Concurrency;

public static class SqliteConcurrencyExtensions
{
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
            
            // Create a configured connection
            var connection = new SqliteConnection(enhancedConnectionString);
            
            // Apply optimizations when connection opens
            connection.StateChange += (sender, args) =>
            {
                if (args.OriginalState == ConnectionState.Closed && 
                    args.CurrentState == ConnectionState.Open)
                {
                    if (sender is SqliteConnection sqliteConnection)
                    {
                        // Apply runtime pragmas
                        SqliteConnectionEnhancer.ApplyRuntimePragmas(sqliteConnection, options);
                        
                        // Set busy timeout
                        SetBusyTimeout(sqliteConnection, options.BusyTimeout);
                        
                        // Additional optimizations
                        if (options.UseWriteQueue)
                        {
                            ConfigureForWriteQueue(sqliteConnection);
                        }
                        
                        if (options.EnableWalCheckpointManagement)
                        {
                            SetWalCheckpoint(sqliteConnection, options.WalAutoCheckpoint);
                        }
                    }
                }
            };

            // Use the configured connection with EF Core
            optionsBuilder.UseSqlite(connection, sqliteOptions =>
            {
                // Configure command timeout
                sqliteOptions.CommandTimeout(options.CommandTimeout);
            });
            
            // Add interceptors if using write queue
            if (options.UseWriteQueue)
            {
                optionsBuilder.AddInterceptors(new SqliteConcurrencyInterceptor(options));
            }
            
            return optionsBuilder;
        }
 
    

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

    public static async Task BulkInsertOptimizedAsync<T>(
        this DbContext context,
        IEnumerable<T> entities,
        CancellationToken cancellationToken = default) where T : class
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("BEGIN IMMEDIATE", cancellationToken);

        // Check if EFCore.BulkExtensions is available via reflection
        var bulkExtensionsType =
            Type.GetType("EFCore.BulkExtensions.SqliteBulkExtensions, EFCore.BulkExtensions.Sqlite");
        if (bulkExtensionsType != null)
        {
            var method = bulkExtensionsType.GetMethod("BulkInsertAsync",
                new[] { typeof(DbContext), typeof(IEnumerable<T>), typeof(CancellationToken) });

            if (method != null)
            {
                await (Task)method.Invoke(null, new object[] { context, entities, cancellationToken });
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        // Fallback to batch inserts
        var batchSize = 1000;
        var batches = entities.Chunk(batchSize);

        foreach (var batch in batches)
        {
            await context.AddRangeAsync(batch, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ConfigureForWriteQueue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
                PRAGMA cache_size = -2000;
                PRAGMA page_size = 4096;
            ";
        command.ExecuteNonQuery();
    }

    private static void SetBusyTimeout(SqliteConnection connection, TimeSpan timeout)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {(int)timeout.TotalMilliseconds};";
        command.ExecuteNonQuery();
    }

    private static void SetWalCheckpoint(SqliteConnection connection, int checkpointPages)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_autocheckpoint = {checkpointPages};";
        command.ExecuteNonQuery();
    }
}