using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBanking.BuildingBlocks.Regions;

public static class RegionalServiceExtensions
{
    // A service whose data is split by province (customers, accounts, transactions, notifications, audit).
    // Inject RegionalDb<TContext>; the context itself is deliberately not registered, so every query has to pick a province.
    public static IServiceCollection AddRegionalDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        AddRegionCore(services);
        services.AddScoped<RegionalDb<TContext>>();
        return services;
    }

    public static IServiceCollection AddRegionalOutboxPublisher<TContext>(this IServiceCollection services)
        where TContext : DbContext, IOutboxDbContext
    {
        services.AddHostedService<RegionalOutboxPublisherHostedService<TContext>>();
        return services;
    }

    // A service with bank-wide data (logins, staff, chat, admin) in DigitalBanking_Central.
    public static IServiceCollection AddCentralDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        AddRegionCore(services);
        services.AddDbContext<TContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<RegionalDatabaseOptions>().CentralConnectionString));
        return services;
    }

    // Creates the tables on both nodes (primary and standby) of all 4 province databases, so the coordinator
    // can replicate into the standby and promote it; old data (one database per service) is moved into Punjab once.
    public static async Task EnsureRegionalDatabasesAsync<TContext>(this WebApplication app, string legacyDatabase)
        where TContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RegionalDb<TContext>>();
        var options = scope.ServiceProvider.GetRequiredService<RegionalDatabaseOptions>();
        var topology = options.Topology;
        foreach (var region in BankRegions.All)
        {
            foreach (var node in new[] { 'A', 'B' })
            {
                var database = BankRegions.DatabaseName(region, node);

                // A province whose server is down (e.g. its Docker container is stopped) must not stop this service
                // from starting: the other provinces keep working, and this one answers "retry" until it is back.
                if (!await IsServerReachableAsync(options.MasterConnectionStringFor(database)))
                {
                    app.Logger.LogWarning("{Database} is not reachable; skipped at startup.", database);
                    continue;
                }

                try
                {
                    await using var context = db.ForDatabase(database);
                    await DatabaseBootstrap.EnsureTablesAsync(context);
                }
                catch (SqlException exception) when (database != topology.PrimaryDatabase(region))
                {
                    // A standby that is offline gets its tables when it is rebuilt from the primary.
                    app.Logger.LogWarning("Standby {Database} is not reachable yet: {Message}", database, exception.Message);
                }
            }
        }

        try
        {
            await DatabaseBootstrap.ImportLegacyAsync(db.For(BankRegions.Default), legacyDatabase);
        }
        catch (Exception exception) when (exception is SqlException || RegionErrors.IsUnavailable(exception))
        {
            // E.g. fenced: the cluster coordinator has not granted Punjab's primary its lease yet. Retried on the next start.
            app.Logger.LogWarning("Legacy import from {Database} skipped: {Message}", legacyDatabase, exception.Message);
        }
    }

    public static async Task EnsureCentralDatabaseAsync<TContext>(this WebApplication app, string? legacyDatabase = null)
        where TContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();
        await DatabaseBootstrap.EnsureTablesAsync(dbContext);
        if (legacyDatabase is not null)
        {
            await DatabaseBootstrap.ImportLegacyAsync(dbContext, legacyDatabase);
        }
    }

    private static async Task<bool> IsServerReachableAsync(string masterConnectionString)
    {
        try
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static void AddRegionCore(IServiceCollection services)
    {
        services.TryAddSingleton<RegionalDatabaseOptions>();
        services.TryAddSingleton<IUserRegionDirectory, UserRegionDirectory>();
        services.TryAddSingleton<StandbyAcknowledger>();
    }
}
