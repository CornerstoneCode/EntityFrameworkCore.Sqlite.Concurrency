using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;

namespace EntityFrameworkCore.Sqlite.Concurrency;

/// <summary>
/// A thread-safe SQLite context that provides application-level serialization for writes.
/// </summary>
/// <typeparam name="TContext">The type of the actual DbContext.</typeparam>
public class ThreadSafeSqliteContext<TContext> : DbContext where TContext : DbContext
{
    private SemaphoreSlim? _writeLock;
    private readonly string? _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadSafeSqliteContext{TContext}"/> class.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    public ThreadSafeSqliteContext(string connectionString)
    {
        _connectionString = SqliteConnectionEnhancer.GetOptimizedConnectionString(connectionString);
        _writeLock = SqliteConnectionEnhancer.GetWriteLock(_connectionString);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadSafeSqliteContext{TContext}"/> class using options.
    /// </summary>
    /// <param name="options">The options.</param>
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

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured && _connectionString != null)
        {
            optionsBuilder.UseSqliteWithConcurrency(_connectionString);
        }
    }

    /// <summary>
    /// Executes a write operation with app-level serialization and automatic transaction management.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
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
                // Use explicit transaction. The interceptor will ensure BEGIN IMMEDIATE.
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

    /// <summary>
    /// Executes a write operation with app-level serialization and automatic transaction management.
    /// </summary>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="ct">The cancellation token.</param>
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

    /// <summary>
    /// Executes a read operation. No locking is performed.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public async Task<T> ExecuteReadAsync<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        return await operation((TContext)(object)this);
    }

    /// <summary>
    /// Performs a bulk insert with optimized settings and app-level locking.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="ct">The cancellation token.</param>
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

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}