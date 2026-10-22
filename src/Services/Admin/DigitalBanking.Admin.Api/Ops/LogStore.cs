using System.Data;
using System.Threading.Channels;
using DigitalBanking.BuildingBlocks.Observability;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops;

/// <summary>
/// Receives request logs from every service. Keeps a 15 minute per-second rollup in memory for the
/// live dashboard and persists every entry to SQL Server for the logs explorer.
/// </summary>
public sealed class LogStore
{
    private const int WindowSeconds = 900;
    private readonly Bucket[] buckets = Enumerable.Range(0, WindowSeconds).Select(_ => new Bucket()).ToArray();
    private readonly object sync = new();
    private readonly Channel<RequestLogEntry> pending = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(300_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private long ingested;

    public long Ingested => Interlocked.Read(ref ingested);
    public int PendingWrites => pending.Reader.CanCount ? pending.Reader.Count : 0;
    public ChannelReader<RequestLogEntry> PendingReader => pending.Reader;

    public void Ingest(IEnumerable<RequestLogEntry> entries)
    {
        lock (sync)
        {
            foreach (var entry in entries)
            {
                Interlocked.Increment(ref ingested);
                pending.Writer.TryWrite(entry);
                Record(entry);
            }
        }
    }

    private void Record(RequestLogEntry entry)
    {
        var second = ToSecond(entry.TimestampUtc);
        var bucket = buckets[second % WindowSeconds];
        if (bucket.Second != second)
        {
            bucket.Reset(second);
        }

        var service = bucket.Services.TryGetValue(entry.Service, out var serviceAgg) ? serviceAgg : bucket.Services[entry.Service] = new Agg();
        service.Add(entry);

        // Client and endpoint views come from the edge only, otherwise each call would count twice.
        if (entry.Service != "gateway")
        {
            return;
        }

        bucket.Edge.Add(entry);
        var client = bucket.Clients.TryGetValue(entry.ClientId, out var clientAgg) ? clientAgg : bucket.Clients[entry.ClientId] = new ClientAgg();
        client.Add(entry);
        client.Region = entry.Region;
        client.LastPath = entry.Path;

        var pathKey = $"{entry.Method} {entry.Path}";
        var path = bucket.Paths.TryGetValue(pathKey, out var pathAgg) ? pathAgg : bucket.Paths[pathKey] = new Agg { Label = entry.Target };
        path.Add(entry);
    }

    public IReadOnlyList<EdgePoint> EdgeSeries(int seconds)
    {
        lock (sync)
        {
            var now = ToSecond(DateTime.UtcNow) - 1;
            var points = new List<EdgePoint>(seconds);
            for (var second = now - seconds + 1; second <= now; second++)
            {
                var bucket = buckets[second % WindowSeconds];
                var agg = bucket.Second == second ? bucket.Edge : Agg.Empty;
                points.Add(new EdgePoint(FromSecond(second), agg.Count - agg.Errors4xx - agg.Errors5xx - agg.Blocked, agg.Errors4xx, agg.Errors5xx, agg.Blocked, agg.P95()));
            }

            return points;
        }
    }

    public IReadOnlyList<ServiceTraffic> ServiceTraffic(int windowSeconds, int seriesSeconds)
    {
        lock (sync)
        {
            var now = ToSecond(DateTime.UtcNow) - 1;
            var totals = new Dictionary<string, Agg>();
            var series = new Dictionary<string, int[]>();
            for (var second = now - Math.Max(windowSeconds, seriesSeconds) + 1; second <= now; second++)
            {
                var bucket = buckets[second % WindowSeconds];
                if (bucket.Second != second)
                {
                    continue;
                }

                foreach (var (name, agg) in bucket.Services)
                {
                    if (second > now - seriesSeconds)
                    {
                        var points = series.TryGetValue(name, out var existing) ? existing : series[name] = new int[seriesSeconds];
                        points[second - (now - seriesSeconds + 1)] = agg.Count;
                    }

                    if (second > now - windowSeconds)
                    {
                        (totals.TryGetValue(name, out var total) ? total : totals[name] = new Agg()).Merge(agg);
                    }
                }
            }

            return totals.Keys.Union(series.Keys).Select(name =>
            {
                var total = totals.GetValueOrDefault(name) ?? Agg.Empty;
                return new ServiceTraffic(
                    name,
                    Math.Round(total.Count / (double)windowSeconds, 1),
                    total.Count == 0 ? 0 : Math.Round((total.Errors5xx + total.Errors4xx) * 100d / total.Count, 1),
                    total.Count == 0 ? 0 : Math.Round(total.Errors5xx * 100d / total.Count, 1),
                    total.P95(),
                    total.Blocked,
                    series.GetValueOrDefault(name) ?? new int[seriesSeconds]);
            }).OrderByDescending(traffic => traffic.RequestsPerSecond).ToList();
        }
    }

    public IReadOnlyList<ClientTraffic> TopClients(int windowSeconds, int take)
    {
        lock (sync)
        {
            var now = ToSecond(DateTime.UtcNow) - 1;
            var totals = new Dictionary<string, ClientAgg>();
            for (var second = now - windowSeconds + 1; second <= now; second++)
            {
                var bucket = buckets[second % WindowSeconds];
                if (bucket.Second != second)
                {
                    continue;
                }

                foreach (var (clientId, agg) in bucket.Clients)
                {
                    var total = totals.TryGetValue(clientId, out var existing) ? existing : totals[clientId] = new ClientAgg();
                    total.Merge(agg);
                    total.Region = agg.Region;
                    total.LastPath = agg.LastPath;
                }
            }

            var all = totals.Sum(pair => pair.Value.Count);
            var rates = totals.Values.Select(agg => agg.Count / (double)windowSeconds).OrderBy(rate => rate).ToList();
            var median = rates.Count == 0 ? 0 : rates[rates.Count / 2];

            return totals
                .OrderByDescending(pair => pair.Value.Count)
                .Take(take)
                .Select(pair =>
                {
                    var rate = pair.Value.Count / (double)windowSeconds;
                    return new ClientTraffic(
                        pair.Key,
                        pair.Value.Region ?? "Unknown",
                        Math.Round(rate, 1),
                        all == 0 ? 0 : Math.Round(pair.Value.Count * 100d / all, 1),
                        pair.Value.Blocked,
                        pair.Value.Errors5xx + pair.Value.Errors4xx,
                        pair.Value.LastPath ?? "/",
                        rate >= Math.Max(15, median * 8));
                })
                .ToList();
        }
    }

    public IReadOnlyList<EndpointTraffic> TopEndpoints(int windowSeconds, int take)
    {
        lock (sync)
        {
            var now = ToSecond(DateTime.UtcNow) - 1;
            var totals = new Dictionary<string, Agg>();
            for (var second = now - windowSeconds + 1; second <= now; second++)
            {
                var bucket = buckets[second % WindowSeconds];
                if (bucket.Second != second)
                {
                    continue;
                }

                foreach (var (path, agg) in bucket.Paths)
                {
                    var total = totals.TryGetValue(path, out var existing) ? existing : totals[path] = new Agg { Label = agg.Label };
                    total.Merge(agg);
                }
            }

            return totals
                .OrderByDescending(pair => pair.Value.Count)
                .Take(take)
                .Select(pair => new EndpointTraffic(
                    pair.Key,
                    pair.Value.Label ?? "-",
                    Math.Round(pair.Value.Count / (double)windowSeconds, 1),
                    pair.Value.P95(),
                    pair.Value.Count == 0 ? 0 : Math.Round(pair.Value.Errors5xx * 100d / pair.Value.Count, 1),
                    pair.Value.Blocked))
                .ToList();
        }
    }

    private static long ToSecond(DateTime utc) => utc.Ticks / TimeSpan.TicksPerSecond;
    private static DateTime FromSecond(long second) => new(second * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private sealed class Bucket
    {
        public long Second = -1;
        public Agg Edge = new();
        public readonly Dictionary<string, Agg> Services = new();
        public readonly Dictionary<string, ClientAgg> Clients = new();
        public readonly Dictionary<string, Agg> Paths = new();

        public void Reset(long second)
        {
            Second = second;
            Edge = new Agg();
            Services.Clear();
            Clients.Clear();
            Paths.Clear();
        }
    }

    private class Agg
    {
        public static readonly Agg Empty = new();
        public string? Label;
        public int Count;
        public int Errors4xx;
        public int Errors5xx;
        public int Blocked;
        private readonly List<double> durations = [];

        public void Add(RequestLogEntry entry)
        {
            Count++;
            if (entry.Blocked)
            {
                Blocked++;
            }
            else if (entry.StatusCode >= 500)
            {
                Errors5xx++;
            }
            else if (entry.StatusCode >= 400)
            {
                Errors4xx++;
            }

            if (durations.Count < 400)
            {
                durations.Add(entry.DurationMs);
            }
        }

        public void Merge(Agg other)
        {
            Count += other.Count;
            Errors4xx += other.Errors4xx;
            Errors5xx += other.Errors5xx;
            Blocked += other.Blocked;
            if (durations.Count < 4_000)
            {
                durations.AddRange(other.durations);
            }
        }

        public double P95()
        {
            if (durations.Count == 0)
            {
                return 0;
            }

            var sorted = durations.OrderBy(value => value).ToList();
            return Math.Round(sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(sorted.Count * 0.95) - 1)], 1);
        }
    }

    private sealed class ClientAgg : Agg
    {
        public string? Region;
        public string? LastPath;
    }
}

public sealed record EdgePoint(DateTime AtUtc, int Ok, int ClientErrors, int ServerErrors, int Blocked, double P95Ms);
public sealed record ServiceTraffic(string Service, double RequestsPerSecond, double ErrorRate, double ServerErrorRate, double P95Ms, int Blocked, int[] Series);
public sealed record ClientTraffic(string ClientId, string Region, double RequestsPerSecond, double SharePercent, int Blocked, int Errors, string LastPath, bool Suspicious);
public sealed record EndpointTraffic(string Endpoint, string Target, double RequestsPerSecond, double P95Ms, double ServerErrorRate, int Blocked);

/// <summary>Drains the log queue into SQL Server with SqlBulkCopy and enforces retention.</summary>
public sealed class LogWriter(LogStore store, IConfiguration configuration, ILogger<LogWriter> logger) : BackgroundService
{
    private DateTime lastRetention = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Admin")!;
        var table = CreateTable();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1_000, stoppingToken);
                table.Clear();
                while (table.Rows.Count < 20_000 && store.PendingReader.TryRead(out var entry))
                {
                    table.Rows.Add(null, entry.TimestampUtc, entry.Service, entry.Target, entry.Instance, entry.Method, entry.Path, entry.Query,
                        entry.StatusCode, entry.DurationMs, entry.ClientId, entry.IpAddress, entry.Region, entry.UserId,
                        entry.UserAgent, entry.Blocked, entry.BlockReason, entry.TraceId, entry.Source);
                }

                if (table.Rows.Count > 0)
                {
                    using var bulk = new SqlBulkCopy(connectionString) { DestinationTableName = "RequestLogs", BulkCopyTimeout = 60 };
                    foreach (DataColumn column in table.Columns)
                    {
                        if (column.ColumnName != "Id")
                        {
                            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }
                    }

                    await bulk.WriteToServerAsync(table, stoppingToken);
                }

                if (DateTime.UtcNow - lastRetention > TimeSpan.FromMinutes(5))
                {
                    lastRetention = DateTime.UtcNow;
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(stoppingToken);
                    await using var command = new SqlCommand(
                        "DELETE TOP (50000) FROM RequestLogs WHERE TimestampUtc < DATEADD(hour, -24, SYSUTCDATETIME());",
                        connection) { CommandTimeout = 120 };
                    await command.ExecuteNonQueryAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Writing request logs failed.");
            }
        }
    }

    private static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("TimestampUtc", typeof(DateTime));
        table.Columns.Add("Service", typeof(string));
        table.Columns.Add("Target", typeof(string));
        table.Columns.Add("Instance", typeof(string));
        table.Columns.Add("Method", typeof(string));
        table.Columns.Add("Path", typeof(string));
        table.Columns.Add("Query", typeof(string));
        table.Columns.Add("StatusCode", typeof(int));
        table.Columns.Add("DurationMs", typeof(double));
        table.Columns.Add("ClientId", typeof(string));
        table.Columns.Add("IpAddress", typeof(string));
        table.Columns.Add("Region", typeof(string));
        table.Columns.Add("UserId", typeof(string));
        table.Columns.Add("UserAgent", typeof(string));
        table.Columns.Add("Blocked", typeof(bool));
        table.Columns.Add("BlockReason", typeof(string));
        table.Columns.Add("TraceId", typeof(string));
        table.Columns.Add("Source", typeof(string));
        return table;
    }
}
