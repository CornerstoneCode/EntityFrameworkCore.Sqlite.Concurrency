using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;
using EntityFrameworkCore.Sqlite.Concurrency.Models;


namespace EntityFrameworkCore.Sqlite.Concurrency;

public class SqliteConcurrencyInterceptor : DbCommandInterceptor
{
    private readonly SqliteConcurrencyOptions _options;
    private readonly SemaphoreSlim _writeLock;
    private readonly Channel<Func<ValueTask>> _writeQueue;
    private readonly Task _queueProcessor;

    public SqliteConcurrencyInterceptor(SqliteConcurrencyOptions options, string connectionString)
    {
        _options = options;
        _writeLock = SqliteConnectionEnhancer.GetWriteLock(connectionString);
        _writeQueue = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _queueProcessor = Task.Run(ProcessWriteQueue);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (IsWriteCommand(command.CommandText))
        {
            if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
            {
                UpgradeToBeginImmediate(command);
                return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            var tcs = new TaskCompletionSource<InterceptionResult<DbDataReader>>();
            await _writeQueue.Writer.WriteAsync(async () =>
            {
                try
                {
                    UpgradeToBeginImmediate(command);
                    var r = await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
                    tcs.SetResult(r);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, cancellationToken);

            return await tcs.Task;
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (IsWriteCommand(command.CommandText))
        {
            if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
            {
                UpgradeToBeginImmediate(command);
                return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }

            var tcs = new TaskCompletionSource<InterceptionResult<int>>();
            await _writeQueue.Writer.WriteAsync(async () =>
            {
                try
                {
                    UpgradeToBeginImmediate(command);
                    var r = await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
                    tcs.SetResult(r);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, cancellationToken);

            return await tcs.Task;
        }

        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        if (IsWriteCommand(command.CommandText))
        {
            if (SqliteConnectionEnhancer.IsWriteLockHeld.Value)
            {
                UpgradeToBeginImmediate(command);
                return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
            }

            var tcs = new TaskCompletionSource<InterceptionResult<object>>();
            await _writeQueue.Writer.WriteAsync(async () =>
            {
                try
                {
                    UpgradeToBeginImmediate(command);
                    var r = await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
                    tcs.SetResult(r);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, cancellationToken);

            return await tcs.Task;
        }

        return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
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

    private async Task ProcessWriteQueue()
    {
        await foreach (var writeOperation in _writeQueue.Reader.ReadAllAsync())
        {
            await _writeLock.WaitAsync();
            SqliteConnectionEnhancer.IsWriteLockHeld.Value = true;
            try
            {
                await writeOperation();
            }
            finally
            {
                SqliteConnectionEnhancer.IsWriteLockHeld.Value = false;
                _writeLock.Release();
            }
        }
    }

    private static bool IsWriteCommand(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return false;

        var normalized = commandText.TrimStart().ToUpperInvariant();

        // Skip SELECT and read-only PRAGMAs
        if (normalized.StartsWith("SELECT") ||
            (normalized.StartsWith("PRAGMA") && (normalized.Contains("TABLE_INFO") || 
                                                 normalized.Contains("INDEX_LIST") || 
                                                 normalized.Contains("INDEX_INFO") ||
                                                 normalized.Contains("FOREIGN_KEY_LIST"))))
            return false;

        return normalized.StartsWith("INSERT") ||
               normalized.StartsWith("UPDATE") ||
               normalized.StartsWith("DELETE") ||
               normalized.StartsWith("CREATE") ||
               normalized.StartsWith("DROP") ||
               normalized.StartsWith("ALTER") ||
               normalized.StartsWith("BEGIN") ||
               normalized.StartsWith("PRAGMA");
    }

}