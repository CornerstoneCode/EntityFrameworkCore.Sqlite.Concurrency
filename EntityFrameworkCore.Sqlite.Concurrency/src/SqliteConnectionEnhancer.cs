using System.Data.Common;
using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.Data.Sqlite;


namespace EntityFrameworkCore.Sqlite.Concurrency;

public static class SqliteConnectionEnhancer
{
    public static string GetOptimizedConnectionString(string originalConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(originalConnectionString);

        // Set all critical parameters for thread-safe operations
        var optimizations = new Dictionary<string, string>
        {
            ["Journal Mode"] = "WAL", // MUST use string key, not property
            ["Pooling"] = "False",
            ["Cache"] = "Shared",
            ["Synchronous"] = "NORMAL",
            ["Foreign Keys"] = "True",
            ["Recursive Triggers"] = "True"
        };

        foreach (var opt in optimizations)
        {
            if (!builder.ContainsKey(opt.Key))
            {
                builder[opt.Key] = opt.Value; // Use string indexer
            }
        }

        return builder.ToString();
    }

    public static void ApplyRuntimePragmas(DbConnection connection)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            using var command = sqliteConnection.CreateCommand();
            command.CommandText = @"
                    PRAGMA busy_timeout = 30000;
                    PRAGMA journal_size_limit = 67108864; -- 64MB
                    PRAGMA mmap_size = 134217728; -- 128MB
                    PRAGMA temp_store = MEMORY;
                    PRAGMA auto_vacuum = INCREMENTAL;
                ";
            command.ExecuteNonQuery();
        }
    }

    public static void ApplyRuntimePragmas(DbConnection connection, SqliteConcurrencyOptions options)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            using var command = sqliteConnection.CreateCommand();
            command.CommandText = $@"
                    PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds};
                    PRAGMA journal_size_limit = 67108864;
                    PRAGMA mmap_size = 134217728;
                    PRAGMA temp_store = MEMORY;
                    PRAGMA auto_vacuum = INCREMENTAL;
                ";

            if (options.EnableWalCheckpointManagement)
            {
                command.CommandText += $"\nPRAGMA wal_autocheckpoint = {options.WalAutoCheckpoint};";
            }

            command.ExecuteNonQuery();
        }
    }
}