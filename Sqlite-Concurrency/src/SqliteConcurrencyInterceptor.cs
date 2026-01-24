using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;
using EFCore.Sqlite.Concurrency.Models;


namespace EFCore.Sqlite.Concurrency;
 
 
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
        
        // ✅ CORRECT: Returns ValueTask<InterceptionResult<DbDataReader>>
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
                        // Ensure immediate transaction for writes
                        await EnsureImmediateTransaction(command, eventData, cancellationToken);
                        result = await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
                        tcs.SetResult(result);
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
        
        // ✅ CORRECT: Returns ValueTask<InterceptionResult<int>>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsWriteCommand(command.CommandText))
            {
                var tcs = new TaskCompletionSource<InterceptionResult<int>>();
                await _writeQueue.Writer.WriteAsync(async () =>
                {
                    try
                    {
                        await EnsureImmediateTransaction(command, eventData, cancellationToken);
                        result = await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
                        tcs.SetResult(result);
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
        
        // ✅ CORRECT: Returns ValueTask<InterceptionResult<object>>
        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            if (IsWriteCommand(command.CommandText))
            {
                var tcs = new TaskCompletionSource<InterceptionResult<object>>();
                await _writeQueue.Writer.WriteAsync(async () =>
                {
                    try
                    {
                        await EnsureImmediateTransaction(command, eventData, cancellationToken);
                        result = await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
                        tcs.SetResult(result);
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
        
        private static async Task EnsureImmediateTransaction(
            DbCommand command,
            CommandEventData eventData,
            CancellationToken cancellationToken)
        {
            if (command.Connection is SqliteConnection sqliteConnection && 
                command.Transaction == null &&
                IsWriteCommand(command.CommandText))
            {
                using var beginCommand = sqliteConnection.CreateCommand();
                beginCommand.CommandText = "BEGIN IMMEDIATE";
                await beginCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        
        private static bool IsWriteCommand(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
                return false;
                
            var normalized = commandText.TrimStart().ToUpperInvariant();
            
            // Check for DML commands (Data Manipulation Language)
            var isDml = normalized.StartsWith("INSERT") ||
                        normalized.StartsWith("UPDATE") ||
                        normalized.StartsWith("DELETE") ||
                        normalized.StartsWith("MERGE") ||
                        normalized.StartsWith("REPLACE");
            
            // Also check for DDL commands (Data Definition Language) that modify schema
            var isDdl = normalized.StartsWith("CREATE") ||
                        normalized.StartsWith("DROP") ||
                        normalized.StartsWith("ALTER") ||
                        normalized.StartsWith("TRUNCATE");
            
            // Check for pragmas that write to database
            var isWritePragma = normalized.StartsWith("PRAGMA") && 
                               (normalized.Contains("JOURNAL_MODE") ||
                                normalized.Contains("AUTO_VACUUM") ||
                                normalized.Contains("ENCODING") ||
                                normalized.Contains("PAGE_SIZE") ||
                                normalized.Contains("CACHE_SIZE"));
            
            return isDml || isDdl || isWritePragma;
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
