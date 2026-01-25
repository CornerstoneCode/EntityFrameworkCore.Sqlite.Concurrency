using EntityFrameworkCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    public static IServiceCollection AddConcurrentSqliteDbContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        Action<SqliteConcurrencyOptions>? configure = null,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((provider, options) =>
        {
            options.UseSqliteWithConcurrency(connectionString, configure);
        }, contextLifetime);
            
        return services;
    }
}