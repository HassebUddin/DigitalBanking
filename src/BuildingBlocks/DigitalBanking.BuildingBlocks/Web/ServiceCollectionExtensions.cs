using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Caching;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Observability;
using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBanking.BuildingBlocks.Web;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBankingServiceDefaults(
        this IServiceCollection services,
        IConfiguration configuration,
        string queueName)
    {
        services.AddJwtAuthentication(configuration);
        services.AddRabbitMq(configuration, queueName);
        services.AddRedisCache(configuration);
        services.AddSingleton<ProvinceShardMap>();
        // Every service-to-service HTTP call carries the hybrid logical clock (X-Hlc), both ways.
        services.AddTransient<HlcPropagationHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<HlcPropagationHandler>());
        services.AddBankingTelemetry(configuration, ServiceName(queueName));
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        return services;
    }

    public static IServiceCollection AddOutboxPublisher<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IOutboxDbContext
    {
        services.AddHostedService<OutboxPublisherHostedService<TDbContext>>();
        return services;
    }

    public static WebApplication UseBankingServicePipeline(this WebApplication app)
    {
        app.UseBankingTelemetry();
        app.UseMiddleware<HlcMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapLoadProbe(app.Services.GetRequiredService<TelemetryOptions>().ServiceName);
        return app;
    }

    private static string ServiceName(string queueName)
    {
        return queueName.EndsWith("-service", StringComparison.Ordinal) ? queueName[..^"-service".Length] : queueName;
    }

    public static async Task EnsureDatabaseCreatedAsync<TDbContext>(this WebApplication app)
        where TDbContext : DbContext
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }
}
