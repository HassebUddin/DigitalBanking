using System.Diagnostics;
using DigitalBanking.BuildingBlocks.Observability;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record LoadGeneratorStats(
    double SentPerSecond, double OkPerSecond, double BlockedPerSecond, double FailedPerSecond, double AvgLatencyMs, int InFlight, long Saturated);

/// <summary>
/// Sends real HTTP traffic through the gateway so request logs, service CPU and traffic rules respond
/// to the simulation exactly as they would to real clients.
/// </summary>
public sealed class LoadGenerator(SimulationEngine engine, IConfiguration configuration, ILogger<LoadGenerator> logger) : BackgroundService
{
    private static readonly string[] NormalTargets = ["account", "transaction", "customer", "identity", "notification"];
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 256,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private readonly SemaphoreSlim concurrency = new(192);
    private readonly Random random = new();
    private readonly object statsSync = new();
    private readonly Queue<(long Second, int Sent, int Ok, int Blocked, int Failed, double Latency)> history = new();
    private long saturated;
    private int sent, ok, blocked, failed;
    private double latencySum;

    public LoadGeneratorStats Stats()
    {
        lock (statsSync)
        {
            var window = Math.Max(1, history.Count);
            var totalSent = history.Sum(item => item.Sent);
            var completed = history.Sum(item => item.Ok + item.Blocked + item.Failed);
            return new LoadGeneratorStats(
                Math.Round(totalSent / (double)window, 1),
                Math.Round(history.Sum(item => item.Ok) / (double)window, 1),
                Math.Round(history.Sum(item => item.Blocked) / (double)window, 1),
                Math.Round(history.Sum(item => item.Failed) / (double)window, 1),
                completed == 0 ? 0 : Math.Round(history.Sum(item => item.Latency) / completed, 1),
                192 - concurrency.CurrentCount,
                Interlocked.Read(ref saturated));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gateway = (configuration["ServiceEndpoints:Gateway"] ?? "http://localhost:5000").TrimEnd('/');
        var carry = new Dictionary<string, double>();
        var currentSecond = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            RollStats(ref currentSecond);
            var settings = engine.Settings();
            if (!settings.Running)
            {
                continue;
            }

            if (settings.HttpLoad)
            {
                for (var index = 0; index < Take(carry, "normal", settings.HttpRps * 0.1); index++)
                {
                    var province = PakistanGeo.PickWeighted(PakistanGeo.Provinces, item => item.PopulationMillions, random);
                    var city = PakistanGeo.PickWeighted(province.Cities, item => item.Weight, random);
                    var clientId = $"app-{city.Name.ToLowerInvariant().Replace(' ', '-')}-{random.Next(1, 400):D3}";
                    Fire($"{gateway}/sim/{NormalTargets[random.Next(NormalTargets.Length)]}/probe", clientId, province.Name);
                }
            }

            if (settings.BotFlood)
            {
                for (var index = 0; index < Take(carry, "bot", settings.BotRps * 0.1); index++)
                {
                    Fire($"{gateway}/sim/{settings.BotTarget}/probe", settings.BotClientId, settings.BotRegion);
                }
            }

            if (settings.CpuBurn)
            {
                for (var index = 0; index < Take(carry, "burn", settings.CpuBurnRps * 0.1); index++)
                {
                    Fire($"{gateway}/sim/{settings.CpuBurnService}/probe?burnMs={settings.CpuBurnMs}", "batch-reconciliation-job", "Islamabad");
                }
            }
        }
    }

    private static int Take(Dictionary<string, double> carry, string key, double amount)
    {
        var total = carry.GetValueOrDefault(key) + amount;
        var whole = (int)total;
        carry[key] = total - whole;
        return whole;
    }

    private void Fire(string url, string clientId, string region)
    {
        if (!concurrency.Wait(0))
        {
            Interlocked.Increment(ref saturated);
            return;
        }

        lock (statsSync)
        {
            sent++;
        }

        _ = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var outcome = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add(TelemetryHeaders.ClientId, clientId);
                request.Headers.Add(TelemetryHeaders.Region, region);
                request.Headers.Add(TelemetryHeaders.Source, "sim");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var status = (int)response.StatusCode;
                outcome = status is 403 or 429 ? 1 : status >= 500 ? 2 : 0;
            }
            catch (Exception exception)
            {
                outcome = 2;
                logger.LogDebug("Load request failed: {Message}", exception.Message);
            }
            finally
            {
                concurrency.Release();
                lock (statsSync)
                {
                    switch (outcome)
                    {
                        case 0: ok++; break;
                        case 1: blocked++; break;
                        default: failed++; break;
                    }

                    latencySum += watch.Elapsed.TotalMilliseconds;
                }
            }
        });
    }

    private void RollStats(ref long currentSecond)
    {
        var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        if (now == currentSecond)
        {
            return;
        }

        lock (statsSync)
        {
            history.Enqueue((currentSecond, sent, ok, blocked, failed, latencySum));
            while (history.Count > 5)
            {
                history.Dequeue();
            }

            sent = ok = blocked = failed = 0;
            latencySum = 0;
        }

        currentSecond = now;
    }

    public override void Dispose()
    {
        client.Dispose();
        base.Dispose();
    }
}
