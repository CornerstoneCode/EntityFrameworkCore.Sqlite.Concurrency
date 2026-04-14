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
    /// <remarks>
    /// <para>
    /// Two classes of <c>SQLITE_BUSY</c> are handled:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>SQLITE_BUSY / SQLITE_BUSY_RECOVERY / SQLITE_BUSY_TIMEOUT</b> — the
    ///     transaction is rolled back and retried after exponential backoff with jitter.
    ///   </item>
    ///   <item>
    ///     <b>SQLITE_BUSY_SNAPSHOT</b> — the read snapshot became stale. The transaction
    ///     is rolled back and the entire operation lambda is restarted so it can re-query
    ///     any data that may now be stale.
    ///   </item>
    /// </list>
    /// <para>
    ///   <b>SQLITE_LOCKED</b> (same-connection conflict) propagates immediately and is
    ///   not retried, as it indicates an application-level bug.
    /// </para>
    /// </remarks>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<TContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        // Reentrancy check: if this execution flow already holds the lock, execute
        // directly to avoid deadlocking on the same SemaphoreSlim.
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
                // The interceptor will upgrade this BEGIN to BEGIN IMMEDIATE, ensuring
                // no later statement in the transaction fails with SQLITE_BUSY before
                // commit (as long as UpgradeTransactionsToImmediate is true).
                await using var transaction = await Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);

                var result = await operation((TContext)(object)this);
                await SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return result;
            }
            catch (SqliteException ex) when (SqliteErrorCodes.IsAnyBusy(ex))
            {
                // Release the lock before sleeping so other writers can make progress.
                SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
                WriteLock.Release();

                attempt++;
                if (attempt >= maxRetryAttempts)
                {
                    var kind = SqliteErrorCodes.IsBusySnapshot(ex)
                        ? "SQLITE_BUSY_SNAPSHOT (stale read snapshot — another writer committed after this transaction began)"
                        : $"SQLITE_BUSY (extended code {ex.SqliteExtendedErrorCode})";

                    throw new TimeoutException(
                        $"SQLite database busy after {attempt} retry attempt(s). " +
                        $"Error: {kind}. " +
                        $"Consider increasing MaxRetryAttempts or BusyTimeout.",
                        ex);
                }

                // Exponential backoff with full jitter: sleep in [baseDelay, 2×baseDelay].
                // Jitter prevents synchronized retry storms when multiple threads contend.
                var baseDelay = 100 * Math.Pow(2, attempt);
                var jitter    = Random.Shared.NextDouble() * baseDelay;
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), ct);

                // Continue to next loop iteration — for BUSY_SNAPSHOT this correctly
                // restarts the entire operation lambda so stale data is re-queried.
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
    /// Executes a read operation without locking.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    /// <remarks>
    /// WAL mode allows reads to proceed concurrently with writes. Keep read transactions
    /// short to avoid blocking WAL checkpoint completion, which can cause the WAL file to
    /// grow and degrade read performance over time.
    /// </remarks>
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
            _options ??= new SqliteConcurrencyOptions();
            return _options;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
