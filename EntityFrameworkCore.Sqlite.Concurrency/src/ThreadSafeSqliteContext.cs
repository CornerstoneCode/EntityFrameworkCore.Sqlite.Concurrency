using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.Sqlite.Concurrency;

public class ThreadSafeSqliteContext<TContext> : DbContext, IAsyncDisposable where TContext : DbContext
{
    private static readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;
    private SqliteConnection? _persistentConnection;
    private SqliteTransaction? _currentTransaction;

    public ThreadSafeSqliteContext(string connectionString)
    {
        _connectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(connectionString);
    }

    public ThreadSafeSqliteContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqliteWithConcurrency(_connectionString);
        }
    }

    public async Task<T> ExecuteWriteAsync<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Use immediate transaction for writes
            await using var transaction = await Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            await Database.ExecuteSqlRawAsync("BEGIN IMMEDIATE;", ct);

            var result = await operation((TContext)(object)this);
            await SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
        {
            // Implement exponential backoff retry
            return await HandleBusyRetry(operation, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ExecuteWriteAsync(
        Func<TContext, Task> operation,
        CancellationToken ct = default)
    {
        await ExecuteWriteAsync(async ctx =>
        {
            await operation(ctx);
            return true;
        }, ct);
    }

    // Fast parallel reads - no locking needed
    public async Task<T> ExecuteReadAsync<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        return await operation((TContext)(object)this);
    }

    public async Task BulkInsertSafeAsync<T>(
        IList<T> entities,
        CancellationToken ct = default) where T : class
    {
        await ExecuteWriteAsync(async ctx =>
        {
            // Check if EFCore.BulkExtensions is available
            var bulkExtensionsType =
                Type.GetType("EFCore.BulkExtensions.SqliteBulkExtensions, EFCore.BulkExtensions.Sqlite");
            if (bulkExtensionsType != null)
            {
                var method = bulkExtensionsType.GetMethod("BulkInsertAsync",
                    new[] { typeof(DbContext), typeof(IList<T>), typeof(CancellationToken) });

                if (method != null)
                {
                    await (Task)method.Invoke(null, new object[] { ctx, entities, ct });
                    return;
                }
            }

            // Fallback: Batch inserts
            var batchSize = 1000;
            for (int i = 0; i < entities.Count; i += batchSize)
            {
                var batch = entities.Skip(i).Take(batchSize).ToList();
                await ctx.AddRangeAsync(batch, ct);
                await ctx.SaveChangesAsync(ct);
            }
        }, ct);
    }

    private async Task<T> HandleBusyRetry<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct,
        int attempt = 0)
    {
        var maxRetryAttempts = _options?.MaxRetryAttempts ?? 3;
        if (attempt >= maxRetryAttempts)
            throw new TimeoutException("Database busy timeout after retries");

        await Task.Delay(100 * (int)Math.Pow(2, attempt), ct);
        return await ExecuteWriteAsync(operation, ct);
    }

    private SqliteConcurrencyOptions? _options;

    private SqliteConcurrencyOptions Options
    {
        get
        {
            if (_options == null)
            {
                // Try to get options from context
                _options = new SqliteConcurrencyOptions();
            }

            return _options;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync();
            _currentTransaction = null;
        }

        if (_persistentConnection != null)
        {
            await _persistentConnection.CloseAsync();
            await _persistentConnection.DisposeAsync();
            _persistentConnection = null;
        }

        await base.DisposeAsync();
    }
}