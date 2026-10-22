using System.Diagnostics;
using System.Net.Http.Json;
using System.Threading.Channels;
using DigitalBanking.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBanking.BuildingBlocks.Observability;

public sealed class TelemetryOptions
{
    public string ServiceName { get; set; } = "service";
    public string AdminBaseUrl { get; set; } = "http://localhost:5107";
    public string? InternalApiKey { get; set; }
    public bool EnforceTrafficRules { get; set; }
    public Func<Microsoft.AspNetCore.Http.HttpContext, string?>? TargetResolver { get; set; }
    public Func<Microsoft.AspNetCore.Http.HttpContext, string?>? InstanceResolver { get; set; }
    public Func<List<DestinationHealthDto>>? DestinationReporter { get; set; }

    /// <summary>This process's own address (host:port), known once Kestrel has bound.</summary>
    public string? SelfInstance { get; set; }
}

/// <summary>Buffers request logs in memory so the request path never waits on the log store.</summary>
public sealed class TelemetryBuffer
{
    private readonly Channel<RequestLogEntry> channel = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private long totalRequests;
    private int inFlight;

    public long TotalRequests => Interlocked.Read(ref totalRequests);
    public int InFlight => Volatile.Read(ref inFlight);
    public ChannelReader<RequestLogEntry> Reader => channel.Reader;

    public void BeginRequest() => Interlocked.Increment(ref inFlight);

    public void EndRequest(RequestLogEntry entry)
    {
        Interlocked.Decrement(ref inFlight);
        Interlocked.Increment(ref totalRequests);
        channel.Writer.TryWrite(entry);
    }
}

/// <summary>Ships logs and process heartbeats to the admin service and pulls the active traffic rules.</summary>
public sealed class TelemetryShipper(
    TelemetryBuffer buffer,
    TrafficRuleCache ruleCache,
    TelemetryOptions options,
    IHttpClientFactory httpClientFactory,
    IServer server,
    ILogger<TelemetryShipper> logger) : BackgroundService
{
    private readonly DateTime startedAtUtc = DateTime.UtcNow;
    private TimeSpan lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime lastSample = DateTime.UtcNow;
    private DateTime lastHeartbeat = DateTime.MinValue;
    private DateTime lastRuleSync = DateTime.MinValue;
    private DateTime lastFailureLog = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient("telemetry");
        client.BaseAddress = new Uri(options.AdminBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(5);
        if (!string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            client.DefaultRequestHeaders.Add(InternalApiKeyAttribute.HeaderName, options.InternalApiKey);
        }

        var batch = new List<RequestLogEntry>(2_000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1_000, stoppingToken);

                batch.Clear();
                while (batch.Count < 5_000 && buffer.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    await client.PostAsJsonAsync("/api/ops/internal/logs", batch, stoppingToken);
                }

                var now = DateTime.UtcNow;
                if (now - lastHeartbeat >= TimeSpan.FromSeconds(2))
                {
                    lastHeartbeat = now;
                    await client.PostAsJsonAsync("/api/ops/internal/heartbeat", SampleHeartbeat(now), stoppingToken);
                }

                if (options.EnforceTrafficRules && now - lastRuleSync >= TimeSpan.FromSeconds(2))
                {
                    lastRuleSync = now;
                    var rules = await client.GetFromJsonAsync<List<TrafficRuleDto>>("/api/ops/internal/rules", stoppingToken);
                    if (rules is not null)
                    {
                        ruleCache.Replace(rules);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The admin service may be down; telemetry must never take a service with it.
                if (DateTime.UtcNow - lastFailureLog > TimeSpan.FromMinutes(1))
                {
                    lastFailureLog = DateTime.UtcNow;
                    logger.LogWarning("Telemetry shipping failed: {Message}", exception.Message);
                }
            }
        }
    }

    private ServiceHeartbeat SampleHeartbeat(DateTime now)
    {
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var elapsed = (now - lastSample).TotalMilliseconds;
        var cpuPercent = elapsed <= 0 ? 0 : (cpu - lastCpu).TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100;
        lastCpu = cpu;
        lastSample = now;

        if (options.SelfInstance is null)
        {
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (address is not null && Uri.TryCreate(address.Replace("://+", "://localhost").Replace("://*", "://localhost"), UriKind.Absolute, out var uri))
            {
                options.SelfInstance = $"localhost:{uri.Port}";
            }
        }

        return new ServiceHeartbeat
        {
            Service = options.ServiceName,
            Instance = options.SelfInstance ?? $"pid-{Environment.ProcessId}",
            ProcessId = Environment.ProcessId,
            Destinations = options.DestinationReporter?.Invoke(),
            TimestampUtc = now,
            CpuPercent = Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
            MemoryMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            GcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
            Threads = process.Threads.Count,
            InFlightRequests = buffer.InFlight,
            TotalRequests = buffer.TotalRequests,
            StartedAtUtc = startedAtUtc
        };
    }
}

public static class TelemetryOptionsFactory
{
    public static TelemetryOptions Create(IConfiguration configuration, string serviceName, bool enforceTrafficRules)
    {
        return new TelemetryOptions
        {
            ServiceName = serviceName,
            AdminBaseUrl = configuration["ServiceEndpoints:Admin"] ?? "http://localhost:5107",
            InternalApiKey = configuration["InternalApi:Key"],
            EnforceTrafficRules = enforceTrafficRules
        };
    }
}
