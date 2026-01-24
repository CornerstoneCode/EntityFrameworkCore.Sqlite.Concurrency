using EFCore.Sqlite.Concurrency.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.Sqlite.Concurrency.ExtensionMethods;

public static class SqliteConcurrencyServiceCollectionExtensions
{
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