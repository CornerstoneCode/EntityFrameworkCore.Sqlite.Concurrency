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

    public static string GetOptimizedConnectionString(string originalConnectionString)
    {
        // Cache hit - return pre-computed optimized string
        return _connectionStringCache.GetOrAdd(originalConnectionString, ComputeOptimizedConnectionString);
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

        // Single optimized command with all PRAGMAs
        using var command = sqliteConnection.CreateCommand();
        var pragmas = new StringBuilder(512);

        pragmas.AppendLine($@"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds};
        PRAGMA mmap_size = 268435456;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -20000;
        PRAGMA page_size = 4096;                    -- ✅ Page size set via PRAGMA, not connection string
        PRAGMA journal_size_limit = 134217728;
        PRAGMA auto_vacuum = INCREMENTAL;
        PRAGMA optimize;
    ");


        // Performance enhancements for write-heavy workloads
        if (options.UseWriteQueue)
        {
            pragmas.AppendLine(@"
                PRAGMA locking_mode = NORMAL;             -- Better for concurrent access
                PRAGMA wal_autocheckpoint = 1000;         -- More frequent checkpoints for write queue
                PRAGMA secure_delete = OFF;               -- Faster deletes
            ");
        }

        // Conditional WAL checkpoint management
        if (options.EnableWalCheckpointManagement)
        {
            pragmas.AppendLine($"PRAGMA wal_autocheckpoint = {options.WalAutoCheckpoint};");
            
            // Aggressive checkpointing for high-write scenarios
            if (options.WalAutoCheckpoint < 500)
            {
                pragmas.AppendLine(@"
                    PRAGMA checkpoint_fullfsync = OFF;    -- Faster checkpoints
                ");
            }
        }

        // Execute all PRAGMAs in single round-trip
        command.CommandText = pragmas.ToString();
        command.ExecuteNonQuery();

        // Optional: Run VACUUM on first connection if database is new/small
        TryOptimizeDatabase(sqliteConnection);
    }

    private static void TryOptimizeDatabase(SqliteConnection connection)
    {
        try
        {
            // Only run VACUUM on small/new databases (avoid on large production DBs)
            using var sizeCmd = connection.CreateCommand();
            sizeCmd.CommandText = "SELECT page_count FROM pragma_page_count();";
            var pageCount = Convert.ToInt64(sizeCmd.ExecuteScalar());
            
            if (pageCount < 10000) // ~40MB database
            {
                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // Silent fail - VACUUM is optional optimization
        }
    }

    // New: Method to apply bulk-optimized settings for import operations
    public static void ApplyBulkOptimizationPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection sqliteConnection) 
            return;

        using var command = sqliteConnection.CreateCommand();
        command.CommandText = @"
            PRAGMA synchronous = OFF;                     -- Maximum speed, risk on crash
            PRAGMA journal_mode = MEMORY;                 -- Memory journal for bulk import
            PRAGMA cache_size = -100000;                  -- 100MB cache for large datasets
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = 536870912;                 -- 512MB for massive imports
            PRAGMA page_size = 8192;                      -- Larger pages for sequential writes
        ";
        command.ExecuteNonQuery();
    }

    // New: Method to revert to normal settings after bulk operations
    public static void ApplyNormalOperationPragmas(DbConnection connection, SqliteConcurrencyOptions options)
    {
        ApplyRuntimePragmas(connection, options); // Revert to standard optimized settings
    }
}