using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Observability;
using DigitalBanking.Contracts.Events;
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

// Lets the gateway accept a transfer while the transaction service is down (see /api/transfers/queue-transfer).
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();

// One Swagger for the whole bank (/swagger), with a definition per province.
builder.Services.AddSingleton<DigitalBanking.BuildingBlocks.Regions.RegionalDatabaseOptions>();
builder.Services.AddSingleton<SwaggerAggregator>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        // Allow every origin (any site, phone app, tunnel) to call the API.
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
// Start following the provinces' masters at boot, so the very first Swagger page already shows the right ones.
app.Services.GetRequiredService<DigitalBanking.BuildingBlocks.Regions.RegionalDatabaseOptions>();

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
app.UseAuthentication();
app.UseAuthorization();

// "Send anyway": the transaction service is down, so park the transfer in RabbitMQ.
// The transaction-service queue holds it until that service starts again and runs it.
app.MapPost("/api/transfers/queue-transfer", async (QueueTransferRequest request, CurrentUser currentUser, IEventPublisher publisher, CancellationToken cancellationToken) =>
{
    if (currentUser.IsStaff) return Results.Json(new { code = "forbidden", message = "Only customers can send transfers." }, statusCode: StatusCodes.Status403Forbidden);
    if (request.Amount <= 0) return Results.BadRequest(new { code = "validation_error", message = "Amount must be greater than zero." });
    if (string.IsNullOrWhiteSpace(request.DestinationAccountNumber)) return Results.BadRequest(new { code = "validation_error", message = "Destination account number is required." });

    var transferId = Guid.NewGuid();
    try
    {
        await publisher.PublishAsync(new TransferRequestedEvent
        {
            TransferId = transferId,
            UserId = currentUser.UserId,
            SourceAccountId = request.SourceAccountId,
            DestinationAccountNumber = request.DestinationAccountNumber.Trim(),
            Amount = request.Amount,
            Description = (request.Description ?? string.Empty).Trim()
        }, cancellationToken);
    }
    catch (Exception)
    {
        return Results.Json(new { code = "service_unavailable", message = "The bank cannot accept transfers right now. Please try again later." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { transferId });
}).RequireAuthorization();

app.MapGet("/swagger/{host}/swagger.json", async (string host, SwaggerAggregator swagger, CancellationToken cancellationToken) =>
    Results.Text((await swagger.DocumentAsync(host, cancellationToken)).ToJsonString(), "application/json"));
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "Digital Bank API";
    foreach (var host in SwaggerAggregator.Hosts)
    {
        options.SwaggerEndpoint($"/swagger/{host.Slug}/swagger.json", host.Name);
    }
});

app.MapReverseProxy();
app.Run();

record QueueTransferRequest(Guid SourceAccountId, string DestinationAccountNumber, decimal Amount, string? Description);
