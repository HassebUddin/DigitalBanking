using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
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

    // Creates the tables in all 4 province databases; old data (one database per service) is moved into Punjab once.
    public static async Task EnsureRegionalDatabasesAsync<TContext>(this WebApplication app, string legacyDatabase)
        where TContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RegionalDb<TContext>>();
        foreach (var region in BankRegions.All)
        {
            await DatabaseBootstrap.EnsureTablesAsync(db.For(region));
        }

        await DatabaseBootstrap.ImportLegacyAsync(db.For(BankRegions.Default), legacyDatabase);
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
    }
}
