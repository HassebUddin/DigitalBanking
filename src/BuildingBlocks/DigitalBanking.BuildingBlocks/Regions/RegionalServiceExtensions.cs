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
        var topology = scope.ServiceProvider.GetRequiredService<RegionalDatabaseOptions>().Topology;
        foreach (var region in BankRegions.All)
        {
            foreach (var node in new[] { 'A', 'B' })
            {
                var database = BankRegions.DatabaseName(region, node);
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
        catch (SqlException exception)
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

    private static void AddRegionCore(IServiceCollection services)
    {
        services.TryAddSingleton<RegionalDatabaseOptions>();
        services.TryAddSingleton<IUserRegionDirectory, UserRegionDirectory>();
        services.TryAddSingleton<StandbyAcknowledger>();
    }
}
