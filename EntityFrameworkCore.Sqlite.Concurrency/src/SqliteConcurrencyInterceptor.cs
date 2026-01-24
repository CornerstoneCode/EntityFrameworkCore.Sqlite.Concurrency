using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;
using EntityFrameworkCore.Sqlite.Concurrency.Models;


namespace EntityFrameworkCore.Sqlite.Concurrency;

public class SqliteConcurrencyInterceptor : DbCommandInterceptor, IAsyncDisposable
{
    private readonly SqliteConcurrencyOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<Func<ValueTask>> _writeQueue;
    private readonly Task _queueProcessor;
    private bool _disposed;

    public SqliteConcurrencyInterceptor(SqliteConcurrencyOptions options)
    {
        _options = options;
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
            var tcs = new TaskCompletionSource<InterceptionResult<DbDataReader>>();
            await _writeQueue.Writer.WriteAsync(async () =>
            {
                try
                {
                    // NO BEGIN IMMEDIATE - let EF Core handle transactions
                    result = await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, cancellationToken);

            await tcs.Task;
            return result;
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // Same pattern for NonQueryExecutingAsync and ScalarExecutingAsync...

    private async Task ProcessWriteQueue()
    {
        await foreach (var writeOperation in _writeQueue.Reader.ReadAllAsync())
        {
            await _writeLock.WaitAsync();
            try
            {
                await writeOperation();
            }
            finally
            {
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
            (normalized.StartsWith("PRAGMA") && normalized.Contains("TABLE_INFO")))
            return false;

        return normalized.StartsWith("INSERT") ||
               normalized.StartsWith("UPDATE") ||
               normalized.StartsWith("DELETE") ||
               normalized.StartsWith("CREATE") ||
               normalized.StartsWith("DROP") ||
               normalized.StartsWith("ALTER");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _writeQueue.Writer.Complete();
        await _queueProcessor;
        _writeLock.Dispose();

        _disposed = true;
    }
}