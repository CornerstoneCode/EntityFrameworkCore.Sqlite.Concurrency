using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.Sqlite.Concurrency.ExtensionMethods;

/// <summary>
/// Extension methods for IServiceCollection to add concurrent SQLite DbContexts.
/// </summary>
public static class SqliteConcurrencyServiceCollectionExtensions
{
    /// <summary>
    /// Adds a DbContext configured with optimized SQLite concurrency and performance settings.
    /// </summary>
    /// <typeparam name="TContext">The type of the DbContext.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="configure">An optional action to configure concurrency options.</param>
    /// <param name="contextLifetime">The lifetime of the DbContext.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>
    /// This overload automatically resolves <see cref="ILoggerFactory"/> from the DI
    /// container and injects it into the concurrency options so that <c>SQLITE_BUSY*</c>
    /// events and <c>BEGIN IMMEDIATE</c> upgrades are logged through the application's
    /// normal logging pipeline.
    /// </remarks>
    public static IServiceCollection AddConcurrentSqliteDbContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((provider, options) =>
        {
            options.UseSqliteWithConcurrency(connectionString, o =>
            {
                configure?.Invoke(o);

                // Inject the singleton ILoggerFactory so the interceptor can emit
                // structured logs without the caller having to wire it up manually.
                if (o.LoggerFactory is null)
                    o.LoggerFactory = provider.GetService<ILoggerFactory>();
            });
        }, contextLifetime);

        return services;
    }
}
