using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using EntityFrameworkCore.Sqlite.Concurrency.Models;

namespace EntityFrameworkCore.Sqlite.Concurrency;

/// <summary>
/// Interceptor for SQLite that handles WAL mode, busy timeouts, and transaction upgrades.
/// </summary>
public class SqliteConcurrencyInterceptor : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private readonly SqliteConcurrencyOptions _options;
    private readonly SemaphoreSlim _writeLock;
    private readonly string _connectionString;

    /// <summary>
    /// Gets the concurrency options configured for this interceptor.
    /// </summary>
    public SqliteConcurrencyOptions Options => _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteConcurrencyInterceptor"/> class.
    /// </summary>
    /// <param name="options">The concurrency options.</param>
    /// <param name="connectionString">The connection string.</param>
    public SqliteConcurrencyInterceptor(SqliteConcurrencyOptions options, string connectionString)
    {
        _options = options;
        _connectionString = connectionString;
        _writeLock = SqliteConnectionEnhancer.GetWriteLock(connectionString);
    }

    // --- Connection Management ---

    /// <inheritdoc />
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SqliteConnectionEnhancer.ApplyRuntimePragmas(connection, _options);
    }

    /// <inheritdoc />
    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SqliteConnectionEnhancer.ApplyRuntimePragmas(connection, _options);
        return Task.CompletedTask;
    }

    // --- Command Interception ---

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        UpgradeToBeginImmediate(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        UpgradeToBeginImmediate(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        UpgradeToBeginImmediate(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        UpgradeToBeginImmediate(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        UpgradeToBeginImmediate(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        UpgradeToBeginImmediate(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static void UpgradeToBeginImmediate(DbCommand command)
    {
        var text = command.CommandText.Trim();
        if (text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) && 
            !text.Contains("IMMEDIATE", StringComparison.OrdinalIgnoreCase) && 
            !text.Contains("EXCLUSIVE", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText = "BEGIN IMMEDIATE";
            }
        }
    }

    // --- Transaction Interception ---

    /// <inheritdoc />
    public InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        return new(result);
    }

    /// <inheritdoc />
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result) => result;
    /// <inheritdoc />
    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public InterceptionResult TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public InterceptionResult TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public InterceptionResult CreatingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    /// <inheritdoc />
    public Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc />
    public InterceptionResult RollingBackToSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> RollingBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    /// <inheritdoc />
    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc />
    public InterceptionResult ReleasingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> ReleasingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);
    /// <inheritdoc />
    public void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    /// <inheritdoc />
    public Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc />
    public InterceptionResult TransactionExplictlyStarted(DbConnection connection, TransactionEndEventData eventData, InterceptionResult result) => result;
    /// <inheritdoc />
    public ValueTask<InterceptionResult> TransactionExplictlyStartedAsync(DbConnection connection, TransactionEndEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => new(result);

    // --- Connection Management (IConnectionInterceptor) ---
    /// <inheritdoc />
    public void ConnectionOpening(DbConnection connection, ConnectionEventData eventData) { }
    /// <inheritdoc />
    public Task ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc />
    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) { }
    /// <inheritdoc />
    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData) => Task.CompletedTask;
    /// <inheritdoc />
    public void ConnectionClosing(DbConnection connection, ConnectionEventData eventData) { }
    /// <inheritdoc />
    public Task ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData) => Task.CompletedTask;
    /// <inheritdoc />
    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) { }
    /// <inheritdoc />
    public Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData) => Task.CompletedTask;
}