using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System;
using System.Threading.Tasks;
using EntityFrameworkCore.Sqlite.Concurrency;
using EntityFrameworkCore.Sqlite.Concurrency.Models;

namespace EntityFrameworkCore.Sqlite.Concurrency;

public static class ThreadSafeFactory
{
    public static TContext CreateContext<TContext>(
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
        where TContext : DbContext
    {
        var optionsBuilder = new DbContextOptionsBuilder<TContext>()
            .UseSqliteWithConcurrency(connectionString, configure);

        return (TContext)Activator.CreateInstance(
            typeof(TContext),
            optionsBuilder.Options)!;
    }

    public static ThreadSafeSqliteContext<TContext> CreateThreadSafeContext<TContext>(
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
        where TContext : DbContext
    {
        var options = new SqliteConcurrencyOptions();
        configure?.Invoke(options);

        var enhancedConnectionString = SqliteConnectionEnhancer
            .GetOptimizedConnectionString(connectionString);

        return new ThreadSafeSqliteContext<TContext>(enhancedConnectionString);
    }

    public static (DbContextOptions<TContext> Options, string ConnectionString) CreateOptionsAndConnection<TContext>(
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
        where TContext : DbContext
    {
        var options = new SqliteConcurrencyOptions();
        configure?.Invoke(options);

        var enhancedConnectionString = SqliteConnectionEnhancer
            .GetOptimizedConnectionString(connectionString);

        // Create a generic options builder and configure it
        var optionsBuilder = new DbContextOptionsBuilder<TContext>();

        // Manually configure the options using the extension method
        // We can't chain directly, so we'll use it as a regular method
        SqliteConcurrencyExtensions.UseSqliteWithConcurrency(
            optionsBuilder,
            enhancedConnectionString,
            configure);

        return (optionsBuilder.Options, enhancedConnectionString);
    }

    public static TContext CreateContextFromOptions<TContext>(
        DbContextOptions<TContext> options)
        where TContext : DbContext
    {
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    public static async Task<TContext> CreateContextWithSharedConnectionAsync<TContext>(
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null)
        where TContext : DbContext
    {
        var options = new SqliteConcurrencyOptions();
        configure?.Invoke(options);

        var enhancedConnectionString = SqliteConnectionEnhancer
            .GetOptimizedConnectionString(connectionString);

        // Create a shared connection
        var sharedConnection = new SqliteConnection(enhancedConnectionString);
        await sharedConnection.OpenAsync();

        // Apply runtime optimizations
        SqliteConnectionEnhancer.ApplyRuntimePragmas(sharedConnection, options);

        // We are not using the extension method here because we are passing an already open connection.
        var optionsBuilder = new DbContextOptionsBuilder<TContext>()
            .UseSqlite(sharedConnection);

        // Manually add the interceptor if write queue is enabled
        if (options.UseWriteQueue)
        {
            optionsBuilder.AddInterceptors(new SqliteConcurrencyInterceptor(options));
        }

        return (TContext)Activator.CreateInstance(typeof(TContext), optionsBuilder.Options)!;
    }
}