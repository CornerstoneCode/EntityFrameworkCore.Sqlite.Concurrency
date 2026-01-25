using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;

namespace EntityFrameworkCore.Sqlite.Concurrency;

public class ThreadSafeSqliteContext<TContext> : DbContext, IAsyncDisposable where TContext : DbContext
{
    private SemaphoreSlim? _writeLock;
    private readonly string? _connectionString;
    private SqliteConnection? _persistentConnection;
    private SqliteTransaction? _currentTransaction;

    public ThreadSafeSqliteContext(string connectionString)
    {
        _connectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(connectionString);
        _writeLock = SqliteConnectionEnhancer.GetWriteLock(_connectionString);
    }

    public ThreadSafeSqliteContext(DbContextOptions options) : base(options)
    {
        // Try to resolve connection string and lock from options
        var extension = options.FindExtension<SqliteOptionsExtension>();
        if (extension?.ConnectionString != null)
        {
            _connectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(extension.ConnectionString);
            _writeLock = SqliteConnectionEnhancer.GetWriteLock(_connectionString);
        }
        else if (extension?.Connection != null)
        {
            _connectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(extension.Connection.ConnectionString);
            _writeLock = SqliteConnectionEnhancer.GetWriteLock(_connectionString);
        }
    }
    
    private SemaphoreSlim WriteLock 
    {
        get
        {
            if (_writeLock != null) return _writeLock;
            
            // Fallback for cases where connection string wasn't available in constructor
            var connectionString = Database.GetDbConnection().ConnectionString;
            _writeLock = SqliteConnectionEnhancer.GetWriteLock(connectionString);
            return _writeLock;
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured && _connectionString != null)
        {
            optionsBuilder.UseSqliteWithConcurrency(_connectionString);
        }
    }

    public async Task<T> ExecuteWriteAsync<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        // Reentrancy check: if this execution flow already holds the lock, just execute.
        // This avoids deadlocks in nested calls on the same thread/flow.
        if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
        {
            return await operation((TContext)(object)this);
        }

        int attempt = 0;
        int maxRetryAttempts = Options.MaxRetryAttempts;

        while (true)
        {
            await WriteLock.WaitAsync(ct);
            SqliteConnectionEnhancer.IsWriteLockHeld.Value = true;

            try
            {
                // Use explicit transaction. The interceptor will bypass queuing 
                // because IsWriteLockHeld is true.
                await using var transaction = await Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);

                var result = await operation((TContext)(object)this);
                await SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                // Release lock before retry wait
                SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
                WriteLock.Release();

                attempt++;
                if (attempt >= maxRetryAttempts)
                    throw new TimeoutException($"Database busy timeout after {attempt} retries", ex);

                await Task.Delay(100 * (int)Math.Pow(2, attempt), ct);
            }
            finally
            {
                if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
                {
                    SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
                    WriteLock.Release();
                }
            }
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
            // Batch inserts
            var batchSize = 1000;
            for (int i = 0; i < entities.Count; i += batchSize)
            {
                var batch = entities.Skip(i).Take(batchSize).ToList();
                await ctx.AddRangeAsync(batch, ct);
                await ctx.SaveChangesAsync(ct);
            }
        }, ct);
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

    public override async ValueTask DisposeAsync()
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