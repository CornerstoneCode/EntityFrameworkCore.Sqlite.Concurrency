using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.Data.Sqlite;


namespace EntityFrameworkCore.Sqlite.Concurrency;

public static class SqliteConnectionEnhancer
{
    // Cache optimized connection strings to avoid repeated parsing
    private static readonly ConcurrentDictionary<string, string> _connectionStringCache = new();
    
    // Shared locks per connection string to ensure serialization across multiple DbContext instances
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();
    
    // Shared interceptors per connection string to avoid leaking background tasks
    private static readonly ConcurrentDictionary<string, SqliteConcurrencyInterceptor> _interceptors = new();

    // Track if current execution flow already holds a write lock to prevent deadlocks with interceptor
    public static readonly AsyncLocal<bool> IsWriteLockHeld = new();

    private static readonly ConcurrentDictionary<string, object> _pragmaLocks = new();

    private static readonly ConcurrentDictionary<string, bool> _initializedDatabases = new();

    public static string GetOptimizedConnectionString(string originalConnectionString)
    {
        // Cache hit - return pre-computed optimized string
        return _connectionStringCache.GetOrAdd(originalConnectionString, ComputeOptimizedConnectionString);
    }

    public static SemaphoreSlim GetWriteLock(string connectionString)
    {
        return _writeLocks.GetOrAdd(connectionString, _ => new SemaphoreSlim(1, 1));
    }

    public static SqliteConcurrencyInterceptor GetInterceptor(string connectionString, SqliteConcurrencyOptions options)
    {
        return _interceptors.GetOrAdd(connectionString, cs => new SqliteConcurrencyInterceptor(options, cs));
    }

    private static string ComputeOptimizedConnectionString(string originalConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(originalConnectionString)
        {
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            RecursiveTriggers = true,
            Mode = SqliteOpenMode.ReadWriteCreate  
        }; 

        return builder.ToString();
    }

    public static void ApplyRuntimePragmas(DbConnection connection)
    {
        ApplyRuntimePragmas(connection, new SqliteConcurrencyOptions());
    }

    public static void ApplyRuntimePragmas(DbConnection connection, SqliteConcurrencyOptions options)
    {
        if (connection is not SqliteConnection sqliteConnection) 
            return;

        var connectionString = sqliteConnection.ConnectionString;

        // 1. Database-scoped Pragmas - Run once per process
        if (_initializedDatabases.TryAdd(connectionString, true))
        {
            var lockObj = _pragmaLocks.GetOrAdd(connectionString, _ => new object());
            lock (lockObj)
            {
                using var initCommand = sqliteConnection.CreateCommand();
                initCommand.CommandText = $@"
                    PRAGMA journal_mode = WAL;
                    PRAGMA page_size = 4096;
                    PRAGMA auto_vacuum = INCREMENTAL;
                    PRAGMA journal_size_limit = 134217728;
                    PRAGMA wal_autocheckpoint = {options.WalAutoCheckpoint};
                ";
                initCommand.ExecuteNonQuery();
            }
        }

        // 2. Connection-scoped Pragmas - Run on every open
        using var command = sqliteConnection.CreateCommand();
        command.CommandText = $@"
            PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds};
            PRAGMA mmap_size = 268435456;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -20000;
            PRAGMA synchronous = NORMAL;
            PRAGMA locking_mode = NORMAL;
            PRAGMA secure_delete = OFF;
        ";
        command.ExecuteNonQuery();
    }
}