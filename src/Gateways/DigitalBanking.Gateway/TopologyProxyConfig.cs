using System.Net.Http.Json;
using DigitalBanking.BuildingBlocks.Security;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace DigitalBanking.Gateway;

/// <summary>
/// Builds the proxy config from appsettings (routes and default clusters) and overlays the live pod
/// list published by the admin service, so scaling a service changes its load-balanced destinations.
/// Active and passive health checks drop crashed pods within seconds.
/// </summary>
public sealed class TopologyProxyConfig(
    IConfiguration configuration,
    InMemoryConfigProvider provider,
    IHttpClientFactory httpClientFactory,
    ILogger<TopologyProxyConfig> logger) : BackgroundService
{
    private string lastSignature = string.Empty;

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) FromConfiguration(
        IConfiguration configuration, IReadOnlyDictionary<string, List<string>>? topology = null)
    {
        var section = configuration.GetSection("ReverseProxy");
        var routes = section.GetSection("Routes").GetChildren().Select(route => new RouteConfig
        {
            RouteId = route.Key,
            ClusterId = route["ClusterId"],
            Match = new RouteMatch { Path = route["Match:Path"] },
            Transforms = route.GetSection("Transforms").GetChildren()
                .Select(transform => (IReadOnlyDictionary<string, string>)transform.GetChildren().ToDictionary(item => item.Key, item => item.Value ?? string.Empty))
                .ToList()
        }).ToList();

        var clusters = section.GetSection("Clusters").GetChildren().Select(cluster =>
        {
            var defaults = cluster.GetSection("Destinations").GetChildren()
                .ToDictionary(destination => destination.Key, destination => new DestinationConfig { Address = destination["Address"]! });
            var destinations = topology is not null && topology.TryGetValue(cluster.Key, out var pods)
                ? pods.ToDictionary(address => address.Replace("http://", string.Empty), address => new DestinationConfig { Address = address })
                : defaults;

            return new ClusterConfig
            {
                ClusterId = cluster.Key,
                Destinations = destinations,
                LoadBalancingPolicy = "PowerOfTwoChoices",
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = TimeSpan.FromSeconds(2),
                        Timeout = TimeSpan.FromSeconds(3),
                        Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures,
                        Path = "/probe"
                    },
                    Passive = new PassiveHealthCheckConfig
                    {
                        Enabled = true,
                        Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                        ReactivationPeriod = TimeSpan.FromSeconds(10)
                    }
                },
                // Three missed probes in a row (about 6 s): one slow probe on a busy machine must not take a
                // healthy service out of rotation (customers would get 503 "maintenance" for no reason).
                Metadata = new Dictionary<string, string> { ["ConsecutiveFailuresHealthPolicy.Threshold"] = "3" }
            };
        }).ToList();

        return (routes, clusters);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient("topology");
        client.BaseAddress = new Uri(configuration["ServiceEndpoints:Admin"] ?? "http://localhost:5107");
        client.Timeout = TimeSpan.FromSeconds(3);
        client.DefaultRequestHeaders.Add(InternalApiKeyAttribute.HeaderName, configuration["InternalApi:Key"]);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var topology = await client.GetFromJsonAsync<Dictionary<string, List<string>>>("/api/ops/internal/topology", stoppingToken);
                if (topology is not null)
                {
                    var signature = string.Join('|', topology.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={string.Join(',', pair.Value.Order())}"));
                    if (signature != lastSignature)
                    {
                        var (routes, clusters) = FromConfiguration(configuration, topology);
                        provider.Update(routes, clusters);
                        lastSignature = signature;
                        logger.LogInformation("Gateway topology updated: {Topology}", signature);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Keep routing with the last known topology while the admin service is unavailable.
                logger.LogDebug("Topology refresh failed: {Message}", exception.Message);
            }

            await Task.Delay(2_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
