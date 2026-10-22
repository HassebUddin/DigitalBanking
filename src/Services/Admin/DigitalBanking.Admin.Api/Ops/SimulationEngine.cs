using System.Data;
using System.Diagnostics;
using System.Threading.Channels;
using System.Collections.Concurrent;
using DigitalBanking.Admin.Api.Ops.Data;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops;

public sealed class SimulationSpike
{
    public string Province { get; set; } = string.Empty;
    public double Multiplier { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class SimulationSettings
{
    public bool Running { get; set; }
    public int BaseTps { get; set; } = 60;
    public Dictionary<string, double> ProvinceMultipliers { get; set; } = PakistanGeo.Provinces.ToDictionary(province => province.Name, _ => 1d);
    public int DbLatencyMs { get; set; }
    public double ErrorRatePercent { get; set; }
    public bool DbStorm { get; set; }
    public bool HttpLoad { get; set; } = true;
    public int HttpRps { get; set; } = 20;
    public bool BotFlood { get; set; }
    public int BotRps { get; set; } = 120;
    public string BotClientId { get; set; } = "bot-185.220.101.47";
    public string BotRegion { get; set; } = "Sindh";
    public string BotTarget { get; set; } = "transaction";
    public bool CpuBurn { get; set; }
    public string CpuBurnService { get; set; } = "account";
    public int CpuBurnMs { get; set; } = 30;
    public int CpuBurnRps { get; set; } = 40;
    public List<SimulationSpike> Spikes { get; set; } = [];

    public SimulationSettings Clone()
    {
        var copy = (SimulationSettings)MemberwiseClone();
        copy.ProvinceMultipliers = new Dictionary<string, double>(ProvinceMultipliers);
        copy.Spikes = Spikes.Select(spike => new SimulationSpike
        {
            Province = spike.Province,
            Multiplier = spike.Multiplier,
            Label = spike.Label,
            ExpiresAtUtc = spike.ExpiresAtUtc
        }).ToList();
        return copy;
    }
}

public sealed class SimulationConfigRequest
{
    public bool? Running { get; set; }
    public int? BaseTps { get; set; }
    public Dictionary<string, double>? ProvinceMultipliers { get; set; }
    public int? DbLatencyMs { get; set; }
    public double? ErrorRatePercent { get; set; }
    public bool? DbStorm { get; set; }
    public bool? HttpLoad { get; set; }
    public int? HttpRps { get; set; }
    public bool? BotFlood { get; set; }
    public int? BotRps { get; set; }
    public string? BotRegion { get; set; }
    public string? BotTarget { get; set; }
    public bool? CpuBurn { get; set; }
    public string? CpuBurnService { get; set; }
    public int? CpuBurnMs { get; set; }
    public int? CpuBurnRps { get; set; }
}

public sealed class SpikeRequest
{
    public string Province { get; set; } = string.Empty;
    public double Multiplier { get; set; } = 3;
    public int Seconds { get; set; } = 90;
    public string? Label { get; set; }
}

public sealed record FeedItem(
    long Seq, DateTime AtUtc, string Customer, long CustomerId, string Province, string City, string Channel, string Type,
    decimal Amount, string Status, string? Reason, int LatencyMs);

public sealed record ProvinceLive(
    string Name, string Code, long Customers, double Tps, decimal VolumePerMinute, double FailureRate, int Blocked,
    double Multiplier, string? SpikeLabel, bool Suspended, string DbState, string? PrimaryNode, int Backlog, IReadOnlyList<CityLive> Cities);

public sealed record CityLive(string Name, double Lat, double Lng, double Tps, long Customers);

public sealed record SimPoint(DateTime AtUtc, int Success, int Failed, int Blocked, decimal Volume, double P95Ms, IReadOnlyDictionary<string, int> ByProvince);

public sealed record WriterStats(int QueueDepth, long Written, long Dropped, double WritesPerSecond, double LastBatchMs, double CommandsPerSecond, string Mode, string? LastError);

public sealed class SimTransaction
{
    public long CustomerId { get; set; }
    public string Province { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public int LatencyMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Generates regional banking traffic, applies admin chaos settings, and persists every transaction
/// to the primary of its region's cluster. Live dashboard numbers come from per-second rollups.
/// </summary>
public sealed class SimulationEngine(
    RegionalDataPlane dataPlane,
    TrafficRuleStore rules,
    ILogger<SimulationEngine> logger) : BackgroundService
{
    private const int Window = 300;
    private readonly object settingsSync = new();
    private readonly object metricsSync = new();
    private readonly SimulationSettings settings = new();
    private readonly Second[] seconds = Enumerable.Range(0, Window).Select(_ => new Second()).ToArray();
    private readonly LinkedList<FeedItem> feed = new();
    private readonly Channel<SimTransaction> writes = Channel.CreateBounded<SimTransaction>(new BoundedChannelOptions(400_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    private readonly Random random = new();
    private long sequence;
    private long totalTransactions;
    private decimal totalVolume;
    private long written;
    private long dropped;
    private long commands;
    private double lastBatchMs;
    private readonly Queue<(DateTime At, long Written, long Commands)> writerHistory = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SimTransaction>> retries = new(StringComparer.OrdinalIgnoreCase);
    private string? lastError;
    private DateTime startedAtUtc = DateTime.UtcNow;

    public SimulationSettings Settings()
    {
        lock (settingsSync)
        {
            settings.Spikes.RemoveAll(spike => spike.ExpiresAtUtc <= DateTime.UtcNow);
            return settings.Clone();
        }
    }

    public SimulationSettings Apply(SimulationConfigRequest request)
    {
        lock (settingsSync)
        {
            if (request.Running is { } running)
            {
                if (running && !settings.Running)
                {
                    startedAtUtc = DateTime.UtcNow;
                }

                settings.Running = running;
            }

            if (request.BaseTps is { } tps) settings.BaseTps = Math.Clamp(tps, 1, 3_000);
            if (request.DbLatencyMs is { } latency) settings.DbLatencyMs = Math.Clamp(latency, 0, 5_000);
            if (request.ErrorRatePercent is { } errorRate) settings.ErrorRatePercent = Math.Clamp(errorRate, 0, 100);
            if (request.DbStorm is { } storm) settings.DbStorm = storm;
            if (request.HttpLoad is { } httpLoad) settings.HttpLoad = httpLoad;
            if (request.HttpRps is { } httpRps) settings.HttpRps = Math.Clamp(httpRps, 1, 500);
            if (request.BotFlood is { } bot) settings.BotFlood = bot;
            if (request.BotRps is { } botRps) settings.BotRps = Math.Clamp(botRps, 10, 1_000);
            if (PakistanGeo.FindProvince(request.BotRegion ?? string.Empty) is { } botRegion) settings.BotRegion = botRegion.Name;
            if (!string.IsNullOrWhiteSpace(request.BotTarget)) settings.BotTarget = request.BotTarget.Trim().ToLowerInvariant();
            if (request.CpuBurn is { } burn) settings.CpuBurn = burn;
            if (!string.IsNullOrWhiteSpace(request.CpuBurnService)) settings.CpuBurnService = request.CpuBurnService.Trim().ToLowerInvariant();
            if (request.CpuBurnMs is { } burnMs) settings.CpuBurnMs = Math.Clamp(burnMs, 1, 200);
            if (request.CpuBurnRps is { } burnRps) settings.CpuBurnRps = Math.Clamp(burnRps, 1, 300);
            if (request.ProvinceMultipliers is not null)
            {
                foreach (var (name, multiplier) in request.ProvinceMultipliers)
                {
                    if (PakistanGeo.FindProvince(name) is { } province)
                    {
                        settings.ProvinceMultipliers[province.Name] = Math.Clamp(multiplier, 0, 20);
                    }
                }
            }

            return settings.Clone();
        }
    }

    public void AddSpike(SpikeRequest request)
    {
        var province = PakistanGeo.FindProvince(request.Province)
            ?? throw new BuildingBlocks.Exceptions.ValidationException("Pick a valid province.");
        lock (settingsSync)
        {
            settings.Spikes.RemoveAll(spike => spike.Province == province.Name);
            settings.Spikes.Add(new SimulationSpike
            {
                Province = province.Name,
                Multiplier = Math.Clamp(request.Multiplier, 1.1, 20),
                Label = string.IsNullOrWhiteSpace(request.Label) ? "Traffic surge" : request.Label.Trim(),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(request.Seconds, 10, 1_800))
            });
            settings.Running = true;
        }
    }

    public void ApplyScenario(string name)
    {
        switch (name)
        {
            case "eid-rush":
                AddSpike(new SpikeRequest { Province = "Punjab", Multiplier = 3.5, Seconds = 120, Label = "Eid shopping rush" });
                AddSpike(new SpikeRequest { Province = "Sindh", Multiplier = 3, Seconds = 120, Label = "Eid shopping rush" });
                AddSpike(new SpikeRequest { Province = "Khyber Pakhtunkhwa", Multiplier = 2.5, Seconds = 120, Label = "Eid shopping rush" });
                break;
            case "salary-day":
                foreach (var province in PakistanGeo.Provinces)
                {
                    AddSpike(new SpikeRequest { Province = province.Name, Multiplier = 2.2, Seconds = 180, Label = "Salary day" });
                }

                break;
            case "bot-attack":
                Apply(new SimulationConfigRequest { BotFlood = true, Running = true });
                break;
            case "cpu-pressure":
                Apply(new SimulationConfigRequest { CpuBurn = true, Running = true });
                break;
            case "db-storm":
                Apply(new SimulationConfigRequest { DbStorm = true, Running = true });
                break;
            case "db-slowdown":
                Apply(new SimulationConfigRequest { DbLatencyMs = 900, Running = true });
                break;
            case "core-errors":
                Apply(new SimulationConfigRequest { ErrorRatePercent = 15, Running = true });
                break;
            case "reset":
                Apply(new SimulationConfigRequest
                {
                    DbLatencyMs = 0, ErrorRatePercent = 0, DbStorm = false, BotFlood = false, CpuBurn = false,
                    ProvinceMultipliers = PakistanGeo.Provinces.ToDictionary(province => province.Name, _ => 1d)
                });
                lock (settingsSync)
                {
                    settings.Spikes.Clear();
                }

                break;
            default:
                throw new BuildingBlocks.Exceptions.ValidationException($"Unknown scenario '{name}'.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var writer = Task.Run(() => WriteLoopAsync(stoppingToken), stoppingToken);
        var tick = TimeSpan.FromMilliseconds(250);
        var watch = Stopwatch.StartNew();
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = watch.Elapsed;
            try
            {
                Generate(Settings(), tick.TotalSeconds);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Simulation tick failed.");
            }

            var remaining = tick - (watch.Elapsed - started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }

        await writer;
    }

    private void Generate(SimulationSettings current, double tickSeconds)
    {
        if (!current.Running)
        {
            return;
        }

        // Traffic follows where customers live; before any are loaded it follows population.
        var useCustomers = PakistanGeo.Provinces.Any(province => dataPlane.CustomerCount(province.Name) > 0);
        var weights = PakistanGeo.Provinces.ToDictionary(
            province => province.Name,
            province => useCustomers ? dataPlane.CustomerCount(province.Name) : province.PopulationMillions);
        var totalWeight = weights.Values.Sum();
        if (totalWeight <= 0)
        {
            return;
        }

        foreach (var province in PakistanGeo.Provinces)
        {
            var share = weights[province.Name] / totalWeight;
            var multiplier = current.ProvinceMultipliers.GetValueOrDefault(province.Name, 1);
            var spike = current.Spikes.FirstOrDefault(item => item.Province == province.Name)?.Multiplier ?? 1;
            var expected = current.BaseTps * tickSeconds * share * multiplier * spike;
            var count = Poisson(expected);
            var suspended = rules.IsRegionBlocked(province.Name);
            var writable = dataPlane.IsWritable(province.Name);
            var backlog = Backlog(province.Name);
            for (var index = 0; index < count; index++)
            {
                Emit(CreateTransaction(province, current, suspended, writable, backlog, useCustomers));
            }
        }
    }

    private SimTransaction CreateTransaction(Province province, SimulationSettings current, bool suspended, bool writable, int backlog, bool useCustomers)
    {
        var cluster = dataPlane.Cluster(province.Name);
        var city = PakistanGeo.PickWeighted(province.Cities, item => useCustomers ? Math.Max(cluster.CityCounts.GetValueOrDefault(item.Name), 0.0001) : item.Weight, random);
        var channel = PakistanGeo.PickWeighted(PakistanGeo.Channels, item => item.Weight, random).Name;
        var (type, amount) = channel switch
        {
            "ATM" => ("Cash Withdrawal", (decimal)new[] { 1_000, 2_000, 5_000, 10_000, 20_000, 25_000 }[random.Next(6)]),
            "RAAST" => ("RAAST Transfer", LogNormal(8.6, 1.0)),
            "IBFT" => ("IBFT Transfer", LogNormal(9.8, 1.0)),
            "POS" => ("Card Purchase", LogNormal(8.0, 0.9)),
            "Branch" => random.NextDouble() < 0.6 ? ("Cash Deposit", LogNormal(10.6, 1.1)) : ("Cash Withdrawal", LogNormal(10.2, 1.0)),
            "Bill Payment" => ("Bill Payment", LogNormal(8.3, 0.7)),
            _ => random.NextDouble() switch
            {
                < 0.5 => ("Fund Transfer", LogNormal(9.4, 1.1)),
                < 0.75 => ("Bill Payment", LogNormal(8.3, 0.7)),
                _ => ("Mobile Top-up", (decimal)new[] { 100, 200, 300, 500, 1_000 }[random.Next(5)])
            }
        };

        var baseLatency = channel switch
        {
            "Mobile App" => 120, "ATM" => 350, "RAAST" => 180, "IBFT" => 450, "POS" => 250, "Branch" => 900, _ => 300
        };
        var latency = (int)(baseLatency * (0.6 + random.NextDouble())) + current.DbLatencyMs + (current.DbStorm ? 60 : 0) + backlog / 40;

        string status = "Success";
        string? reason = null;
        if (suspended)
        {
            status = "Blocked";
            reason = "Region suspended by admin";
        }
        else if (!writable)
        {
            status = "Failed";
            reason = cluster.WritesFrozen ? "Database switchover in progress" : "Regional database unavailable";
            latency += 3_000;
        }
        else
        {
            var timeoutRisk = current.DbLatencyMs >= 1_500 ? 0.3 : current.DbLatencyMs >= 700 ? 0.07 : 0;
            var backlogRisk = backlog > 50_000 ? 0.15 : backlog > 10_000 ? 0.04 : 0;
            var roll = random.NextDouble();
            if (roll < 0.012)
            {
                status = "Failed";
                reason = random.NextDouble() < 0.7 ? "Insufficient balance" : "Daily limit exceeded";
            }
            else if (roll < 0.012 + current.ErrorRatePercent / 100)
            {
                status = "Failed";
                reason = channel == "IBFT" ? "1LINK switch unavailable" : "Core banking error";
            }
            else if (roll < 0.012 + current.ErrorRatePercent / 100 + timeoutRisk + backlogRisk)
            {
                status = "Failed";
                reason = "Timeout - database";
            }
        }

        return new SimTransaction
        {
            CustomerId = dataPlane.PickCustomerId(province.Name, random),
            Province = province.Name,
            City = city.Name,
            Channel = channel,
            Type = type,
            Amount = Math.Round(amount, 0),
            Status = status,
            FailureReason = reason,
            LatencyMs = latency,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private void Emit(SimTransaction transaction)
    {
        // Rejected transactions never reach the core banking database.
        if (transaction.FailureReason is not ("Regional database unavailable" or "Database switchover in progress" or "Region suspended by admin")
            && !writes.Writer.TryWrite(transaction))
        {
            Interlocked.Increment(ref dropped);
        }

        var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        lock (metricsSync)
        {
            var bucket = seconds[second % Window];
            if (bucket.Value != second)
            {
                bucket.Reset(second);
            }

            bucket.Add(transaction);
            totalTransactions++;
            if (transaction.Status == "Success")
            {
                totalVolume += transaction.Amount;
            }

            var seq = ++sequence;
            if (seq % 3 == 0 || transaction.Status != "Success")
            {
                var province = PakistanGeo.FindProvince(transaction.Province)!;
                var first = random.NextDouble() < 0.48
                    ? PakistanGeo.FemaleNames[random.Next(PakistanGeo.FemaleNames.Length)]
                    : PakistanGeo.MaleNames[random.Next(PakistanGeo.MaleNames.Length)];
                var surname = province.Surnames[random.Next(province.Surnames.Count)];
                feed.AddFirst(new FeedItem(seq, transaction.CreatedAtUtc, $"{first} {surname[0]}.", transaction.CustomerId, transaction.Province,
                    transaction.City, transaction.Channel, transaction.Type, transaction.Amount, transaction.Status, transaction.FailureReason,
                    transaction.LatencyMs));
                while (feed.Count > 40)
                {
                    feed.RemoveLast();
                }
            }
        }
    }

    public LiveSnapshot Snapshot(int seriesSeconds = 120)
    {
        var current = Settings();
        lock (metricsSync)
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond - 1;
            var series = new List<SimPoint>(seriesSeconds);
            var recent = new Aggregate();
            var minute = new Aggregate();
            for (var second = now - Math.Max(seriesSeconds, 60) + 1; second <= now; second++)
            {
                var bucket = seconds[second % Window];
                var live = bucket.Value == second;
                if (second > now - seriesSeconds)
                {
                    series.Add(live
                        ? new SimPoint(new DateTime(second * TimeSpan.TicksPerSecond, DateTimeKind.Utc), bucket.Success, bucket.Failed, bucket.Blocked,
                            bucket.Volume, bucket.P95(), bucket.Provinces.ToDictionary(pair => pair.Key, pair => pair.Value.Count))
                        : new SimPoint(new DateTime(second * TimeSpan.TicksPerSecond, DateTimeKind.Utc), 0, 0, 0, 0, 0, new Dictionary<string, int>()));
                }

                if (!live)
                {
                    continue;
                }

                minute.Merge(bucket);
                if (second > now - 10)
                {
                    recent.Merge(bucket);
                }
            }

            var provinces = PakistanGeo.Provinces.Select(province =>
            {
                var recentProvince = recent.Provinces.GetValueOrDefault(province.Name) ?? new Counter();
                var minuteProvince = minute.Provinces.GetValueOrDefault(province.Name) ?? new Counter();
                var spike = current.Spikes.FirstOrDefault(item => item.Province == province.Name);
                var cluster = dataPlane.Cluster(province.Name);
                return new ProvinceLive(
                    province.Name,
                    province.Code,
                    dataPlane.CustomerCount(province.Name),
                    Math.Round(recentProvince.Count / 10d, 1),
                    minuteProvince.Volume,
                    recentProvince.Count == 0 ? 0 : Math.Round(recentProvince.Failed * 100d / recentProvince.Count, 1),
                    recentProvince.Blocked,
                    current.ProvinceMultipliers.GetValueOrDefault(province.Name, 1) * (spike?.Multiplier ?? 1),
                    spike?.Label,
                    rules.IsRegionBlocked(province.Name),
                    cluster.State,
                    cluster.Primary?.Name,
                    Backlog(province.Name),
                    province.Cities.Select(city => new CityLive(
                        city.Name, city.Lat, city.Lng, Math.Round(recent.Cities.GetValueOrDefault(city.Name) / 10d, 1), cluster.CityCounts.GetValueOrDefault(city.Name))).ToList());
            }).ToList();

            var channels = minute.Channels
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new ChannelShare(pair.Key, pair.Value, minute.Count == 0 ? 0 : Math.Round(pair.Value * 100d / minute.Count, 1)))
                .ToList();

            var reasons = minute.Reasons
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new ChannelShare(pair.Key, pair.Value, minute.Failed + minute.Blocked == 0 ? 0 : Math.Round(pair.Value * 100d / (minute.Failed + minute.Blocked), 1)))
                .ToList();

            return new LiveSnapshot(
                current,
                current.Running ? Math.Round((DateTime.UtcNow - startedAtUtc).TotalSeconds) : 0,
                Math.Round(recent.Count / 10d, 1),
                recent.Count == 0 ? 100 : Math.Round(recent.Success * 100d / recent.Count, 2),
                minute.Volume,
                recent.P95(),
                totalTransactions,
                totalVolume,
                provinces,
                series,
                channels,
                reasons,
                feed.Take(25).ToList(),
                WriterSnapshot(current));
        }
    }

    private WriterStats WriterSnapshot(SimulationSettings current)
    {
        lock (writerHistory)
        {
            var now = DateTime.UtcNow;
            var writesPerSecond = 0d;
            var commandsPerSecond = 0d;
            if (writerHistory.Count > 1)
            {
                var first = writerHistory.Peek();
                var last = writerHistory.Last();
                var span = (last.At - first.At).TotalSeconds;
                if (span > 0)
                {
                    writesPerSecond = Math.Round((last.Written - first.Written) / span);
                    commandsPerSecond = Math.Round((last.Commands - first.Commands) / span);
                }
            }

            var mode = current.DbStorm ? "row-by-row (storm)" : current.DbLatencyMs > 0 ? "bulk + injected latency" : "bulk copy";
            return new WriterStats(TotalBacklog, Interlocked.Read(ref written), Interlocked.Read(ref dropped), writesPerSecond,
                Math.Round(lastBatchMs, 1), commandsPerSecond, mode, lastError);
        }
    }

    public int Backlog(string province) => retries.TryGetValue(province, out var queue) ? queue.Count : 0;

    private int TotalBacklog => writes.Reader.Count + retries.Values.Sum(queue => queue.Count);

    /// <summary>
    /// Drains the stream into per-region batches. A region whose primary is unreachable keeps its rows
    /// in a bounded retry buffer until failover (or the admin) brings a writable primary back.
    /// </summary>
    private async Task WriteLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, stoppingToken);
                var current = Settings();
                var limit = current.DbStorm ? 1_500 : 40_000;
                var drained = 0;
                while (drained < limit && writes.Reader.TryRead(out var transaction))
                {
                    retries.GetOrAdd(transaction.Province, _ => new ConcurrentQueue<SimTransaction>()).Enqueue(transaction);
                    drained++;
                }

                await Task.WhenAll(retries.Select(pair => WriteRegionAsync(pair.Key, pair.Value, current, stoppingToken)));

                lock (writerHistory)
                {
                    writerHistory.Enqueue((DateTime.UtcNow, Interlocked.Read(ref written), Interlocked.Read(ref commands)));
                    while (writerHistory.Count > 10)
                    {
                        writerHistory.Dequeue();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Writing simulated transactions failed.");
            }
        }
    }

    private async Task WriteRegionAsync(string province, ConcurrentQueue<SimTransaction> queue, SimulationSettings current, CancellationToken cancellationToken)
    {
        if (queue.IsEmpty || !dataPlane.IsWritable(province))
        {
            TrimRetries(queue);
            return;
        }

        var limit = current.DbStorm ? 400 : 20_000;
        var batch = new List<SimTransaction>(Math.Min(limit, queue.Count));
        while (batch.Count < limit && queue.TryPeek(out var item))
        {
            batch.Add(item);
            queue.TryDequeue(out _);
        }

        var cluster = dataPlane.Cluster(province);
        var firstId = dataPlane.AllocateTransactionIds(cluster, batch.Count);
        var watch = Stopwatch.StartNew();
        try
        {
            if (current.DbStorm)
            {
                await WriteRowByRowAsync(province, batch, firstId, current.DbLatencyMs, cancellationToken);
            }
            else
            {
                var table = CreateTable();
                for (var index = 0; index < batch.Count; index++)
                {
                    var item = batch[index];
                    table.Rows.Add(firstId + index, item.CustomerId, item.City, item.Channel, item.Type, item.Amount, item.Status,
                        item.FailureReason, item.LatencyMs, item.CreatedAtUtc);
                }

                await dataPlane.BulkInsertAsync(province, "Transactions", table, current.DbLatencyMs, cancellationToken);
                Interlocked.Increment(ref commands);
            }

            lastBatchMs = watch.Elapsed.TotalMilliseconds;
            lastError = null;
            Interlocked.Add(ref written, batch.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the rows for the next attempt; ids are re-allocated so they stay above the new primary's mark.
            foreach (var item in batch)
            {
                queue.Enqueue(item);
            }

            lastError = $"{province}: {exception.Message}";
            TrimRetries(queue);
        }
    }

    private void TrimRetries(ConcurrentQueue<SimTransaction> queue)
    {
        while (queue.Count > 150_000 && queue.TryDequeue(out _))
        {
            Interlocked.Increment(ref dropped);
        }
    }

    /// <summary>The "DB storm" anti-pattern: one INSERT plus one aggregate read per transaction.</summary>
    private async Task WriteRowByRowAsync(string province, List<SimTransaction> batch, long firstId, int latencyMs, CancellationToken cancellationToken)
    {
        await using var connection = await dataPlane.OpenPrimaryAsync(province, cancellationToken);
        if (latencyMs > 0)
        {
            await using var wait = new SqlCommand($"WAITFOR DELAY '00:00:{latencyMs / 1000:D2}.{latencyMs % 1000:D3}'", connection) { CommandTimeout = 30 };
            await wait.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < batch.Count; index++)
        {
            var item = batch[index];
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.Transactions (Id, CustomerId, City, Channel, Type, Amount, Status, FailureReason, LatencyMs, CreatedAtUtc) " +
                "VALUES (@id, @c, @city, @ch, @t, @a, @s, @r, @l, @at);" +
                "SELECT COUNT_BIG(*) FROM dbo.Transactions WITH (NOLOCK) WHERE City = @city AND CreatedAtUtc > DATEADD(second, -30, SYSUTCDATETIME());",
                connection);
            insert.Parameters.AddWithValue("@id", firstId + index);
            insert.Parameters.AddWithValue("@c", item.CustomerId);
            insert.Parameters.AddWithValue("@city", item.City);
            insert.Parameters.AddWithValue("@ch", item.Channel);
            insert.Parameters.AddWithValue("@t", item.Type);
            insert.Parameters.AddWithValue("@a", item.Amount);
            insert.Parameters.AddWithValue("@s", item.Status);
            insert.Parameters.AddWithValue("@r", (object?)item.FailureReason ?? DBNull.Value);
            insert.Parameters.AddWithValue("@l", item.LatencyMs);
            insert.Parameters.AddWithValue("@at", item.CreatedAtUtc);
            await insert.ExecuteScalarAsync(cancellationToken);
            Interlocked.Add(ref commands, 2);
        }
    }

    private static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("CustomerId", typeof(long));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("Channel", typeof(string));
        table.Columns.Add("Type", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("FailureReason", typeof(string));
        table.Columns.Add("LatencyMs", typeof(int));
        table.Columns.Add("CreatedAtUtc", typeof(DateTime));
        return table;
    }
    private int Poisson(double lambda)
    {
        if (lambda <= 0)
        {
            return 0;
        }

        if (lambda > 30)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return Math.Max(0, (int)Math.Round(lambda + normal * Math.Sqrt(lambda)));
        }

        var limit = Math.Exp(-lambda);
        var product = random.NextDouble();
        var count = 0;
        while (product > limit)
        {
            product *= random.NextDouble();
            count++;
        }

        return count;
    }

    private decimal LogNormal(double mu, double sigma)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return (decimal)Math.Min(5_000_000, Math.Exp(mu + sigma * normal));
    }

    private class Counter
    {
        public int Count;
        public int Success;
        public int Failed;
        public int Blocked;
        public decimal Volume;
    }

    private class Aggregate : Counter
    {
        public readonly Dictionary<string, Counter> Provinces = new();
        public readonly Dictionary<string, int> Cities = new();
        public readonly Dictionary<string, int> Channels = new();
        public readonly Dictionary<string, int> Reasons = new();
        protected readonly List<int> Latencies = [];

        public void Merge(Aggregate other)
        {
            Count += other.Count;
            Success += other.Success;
            Failed += other.Failed;
            Blocked += other.Blocked;
            Volume += other.Volume;
            foreach (var (name, counter) in other.Provinces)
            {
                var target = Provinces.TryGetValue(name, out var existing) ? existing : Provinces[name] = new Counter();
                target.Count += counter.Count;
                target.Success += counter.Success;
                target.Failed += counter.Failed;
                target.Blocked += counter.Blocked;
                target.Volume += counter.Volume;
            }

            foreach (var (name, count) in other.Cities) Cities[name] = Cities.GetValueOrDefault(name) + count;
            foreach (var (name, count) in other.Channels) Channels[name] = Channels.GetValueOrDefault(name) + count;
            foreach (var (name, count) in other.Reasons) Reasons[name] = Reasons.GetValueOrDefault(name) + count;
            if (Latencies.Count < 5_000)
            {
                Latencies.AddRange(other.Latencies);
            }
        }

        public double P95()
        {
            if (Latencies.Count == 0)
            {
                return 0;
            }

            var sorted = Latencies.OrderBy(value => value).ToList();
            return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(sorted.Count * 0.95) - 1)];
        }
    }

    private sealed class Second : Aggregate
    {
        public long Value = -1;

        public void Reset(long second)
        {
            Value = second;
            Count = Success = Failed = Blocked = 0;
            Volume = 0;
            Provinces.Clear();
            Cities.Clear();
            Channels.Clear();
            Reasons.Clear();
            Latencies.Clear();
        }

        public void Add(SimTransaction transaction)
        {
            Count++;
            var province = Provinces.TryGetValue(transaction.Province, out var existing) ? existing : Provinces[transaction.Province] = new Counter();
            province.Count++;
            switch (transaction.Status)
            {
                case "Success":
                    Success++;
                    province.Success++;
                    Volume += transaction.Amount;
                    province.Volume += transaction.Amount;
                    break;
                case "Blocked":
                    Blocked++;
                    province.Blocked++;
                    break;
                default:
                    Failed++;
                    province.Failed++;
                    break;
            }

            if (transaction.FailureReason is { } reason)
            {
                Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
            }

            Cities[transaction.City] = Cities.GetValueOrDefault(transaction.City) + 1;
            Channels[transaction.Channel] = Channels.GetValueOrDefault(transaction.Channel) + 1;
            if (Latencies.Count < 1_000)
            {
                Latencies.Add(transaction.LatencyMs);
            }
        }
    }
}

public sealed record ChannelShare(string Name, int Count, double Percent);

public sealed record LiveSnapshot(
    SimulationSettings Settings,
    double RunningSeconds,
    double Tps,
    double SuccessRate,
    decimal VolumeLastMinute,
    double P95LatencyMs,
    long TotalTransactions,
    decimal TotalVolume,
    IReadOnlyList<ProvinceLive> Provinces,
    IReadOnlyList<SimPoint> Series,
    IReadOnlyList<ChannelShare> Channels,
    IReadOnlyList<ChannelShare> FailureReasons,
    IReadOnlyList<FeedItem> Feed,
    WriterStats Writer);
