using DigitalBanking.BuildingBlocks.Observability;
using DigitalBanking.Gateway;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

var (routes, clusters) = TopologyProxyConfig.FromConfiguration(builder.Configuration);
builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
builder.Services.AddHttpClient("topology");
builder.Services.AddHostedService<TopologyProxyConfig>();

// The gateway is the edge: it logs every call and enforces the admin's traffic rules.
builder.Services.AddBankingTelemetry(
    builder.Configuration,
    "gateway",
    enforceTrafficRules: true,
    targetResolver: context => context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Cluster?.ClusterId);

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://localhost", "capacitor://localhost", "ionic://localhost")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Report which pod served each request, and how the load balancer currently sees every pod.
var telemetry = app.Services.GetRequiredService<TelemetryOptions>();
var proxyState = app.Services.GetRequiredService<IProxyStateLookup>();
telemetry.InstanceResolver = context =>
    context.Features.Get<IReverseProxyFeature>()?.ProxiedDestination?.Model.Config.Address.Replace("http://", string.Empty);
telemetry.DestinationReporter = () => proxyState.GetClusters()
    .SelectMany(cluster => cluster.Destinations.Values.Select(destination => new DestinationHealthDto
    {
        Cluster = cluster.ClusterId,
        Address = destination.Model.Config.Address.Replace("http://", string.Empty),
        Active = destination.Health.Active.ToString(),
        Passive = destination.Health.Passive.ToString(),
        InFlight = destination.ConcurrentRequestCount
    }))
    .ToList();

app.UseCors("frontend");
app.UseWebSockets();
app.UseBankingTelemetry();
app.MapReverseProxy();
app.Run();
