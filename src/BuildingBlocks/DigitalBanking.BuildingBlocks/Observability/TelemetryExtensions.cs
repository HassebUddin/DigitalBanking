using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBanking.BuildingBlocks.Observability;

public static class TelemetryExtensions
{
    public static IServiceCollection AddBankingTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        bool enforceTrafficRules = false,
        Func<HttpContext, string?>? targetResolver = null)
    {
        var options = TelemetryOptionsFactory.Create(configuration, serviceName, enforceTrafficRules);
        options.TargetResolver = targetResolver;
        services.AddSingleton(options);
        services.AddSingleton<TelemetryBuffer>();
        services.AddSingleton<TrafficRuleCache>();
        services.AddHttpClient("telemetry");
        services.AddHostedService<TelemetryShipper>();
        return services;
    }

    public static IApplicationBuilder UseBankingTelemetry(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestTelemetryMiddleware>();
    }

    /// <summary>
    /// Lightweight endpoint the load simulator calls through the gateway. Optional CPU burn makes
    /// service pressure visible on the operations dashboard.
    /// </summary>
    public static IEndpointRouteBuilder MapLoadProbe(this IEndpointRouteBuilder endpoints, string serviceName)
    {
        endpoints.MapGet("/probe", (int? burnMs) =>
        {
            var burn = Math.Clamp(burnMs ?? 0, 0, 200);
            if (burn > 0)
            {
                var watch = Stopwatch.StartNew();
                var spin = 0d;
                while (watch.ElapsedMilliseconds < burn)
                {
                    spin += Math.Sqrt(spin + watch.ElapsedTicks);
                }
            }

            return Results.Ok(new { service = serviceName, burnMs = burn, atUtc = DateTime.UtcNow });
        }).AllowAnonymous();
        return endpoints;
    }
}
