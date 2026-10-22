using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops;

public sealed class LogQuery
{
    public int Minutes { get; set; } = 15;
    public string? Service { get; set; }
    public string? Status { get; set; }
    public string? Region { get; set; }
    public string? Source { get; set; }
    public string? ClientId { get; set; }
    public string? Instance { get; set; }
    public string? Q { get; set; }
    public long? BeforeId { get; set; }
    public int Take { get; set; } = 100;
}

public sealed record LogRow(
    long Id, DateTime TimestampUtc, string Service, string? Target, string? Instance, string Method, string Path, string? Query,
    int StatusCode, double DurationMs, string ClientId, string? IpAddress, string Region, string? UserId,
    string? UserAgent, bool Blocked, string? BlockReason, string? TraceId, string Source);

public sealed record FacetValue(string Value, long Count);
public sealed record HistogramBar(DateTime FromUtc, long Ok, long ClientErrors, long ServerErrors, long Blocked);

public sealed record LogSearchResult(
    IReadOnlyList<LogRow> Items,
    long Total,
    IReadOnlyDictionary<string, IReadOnlyList<FacetValue>> Facets,
    IReadOnlyList<HistogramBar> Histogram,
    int BucketSeconds,
    double TookMs);

/// <summary>Datadog-style search over the persisted request logs.</summary>
public sealed class LogQueryService(IConfiguration configuration)
{
    private const string StatusClass =
        "CASE WHEN Blocked = 1 THEN 'blocked' WHEN StatusCode >= 500 THEN '5xx' WHEN StatusCode >= 400 THEN '4xx' WHEN StatusCode >= 300 THEN '3xx' ELSE '2xx' END";

    public async Task<LogSearchResult> SearchAsync(LogQuery query, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var minutes = Math.Clamp(query.Minutes, 1, 1440);
        var fromUtc = DateTime.UtcNow.AddMinutes(-minutes);
        var take = Math.Clamp(query.Take, 10, 500);

        await using var connection = new SqlConnection(configuration.GetConnectionString("Admin"));
        await connection.OpenAsync(cancellationToken);

        var items = new List<LogRow>(take);
        await using (var command = Build(connection, query, fromUtc, null, $"SELECT TOP ({take}) Id, TimestampUtc, Service, Target, Instance, Method, Path, Query, StatusCode, DurationMs, ClientId, IpAddress, Region, UserId, UserAgent, Blocked, BlockReason, TraceId, Source FROM RequestLogs WHERE {{where}} ORDER BY Id DESC", includePaging: true))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new LogRow(
                    reader.GetInt64(0), reader.GetDateTime(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                    reader.GetString(5), reader.GetString(6), NullableString(reader, 7), reader.GetInt32(8), reader.GetDouble(9),
                    reader.GetString(10), NullableString(reader, 11), reader.GetString(12), NullableString(reader, 13), NullableString(reader, 14),
                    reader.GetBoolean(15), NullableString(reader, 16), NullableString(reader, 17), reader.GetString(18)));
            }
        }

        long total;
        await using (var command = Build(connection, query, fromUtc, null, "SELECT COUNT_BIG(*) FROM RequestLogs WHERE {where}", includePaging: false))
        {
            total = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        var facets = new Dictionary<string, IReadOnlyList<FacetValue>>
        {
            ["service"] = await FacetAsync(connection, query, fromUtc, "service", "Service", cancellationToken),
            ["status"] = await FacetAsync(connection, query, fromUtc, "status", StatusClass, cancellationToken),
            ["region"] = await FacetAsync(connection, query, fromUtc, "region", "Region", cancellationToken),
            ["source"] = await FacetAsync(connection, query, fromUtc, "source", "Source", cancellationToken),
            ["instance"] = await FacetAsync(connection, query, fromUtc, "instance", "Instance", cancellationToken)
        };

        var bucketSeconds = Math.Max(5, minutes * 60 / 60);
        var bars = new Dictionary<long, long[]>();
        var histogramSql =
            $"SELECT DATEDIFF(second, @from, TimestampUtc) / {bucketSeconds} AS Slot, {StatusClass} AS Class, COUNT_BIG(*) " +
            "FROM RequestLogs WHERE {where} GROUP BY DATEDIFF(second, @from, TimestampUtc) / " + bucketSeconds + ", " + StatusClass;
        await using (var command = Build(connection, query, fromUtc, null, histogramSql, includePaging: false))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var slot = reader.GetInt32(0);
                var counts = bars.TryGetValue(slot, out var existing) ? existing : bars[slot] = new long[4];
                var index = reader.GetString(1) switch { "blocked" => 3, "5xx" => 2, "4xx" => 1, _ => 0 };
                counts[index] += reader.GetInt64(2);
            }
        }

        var slots = minutes * 60 / bucketSeconds;
        var histogram = Enumerable.Range(0, slots + 1).Select(slot =>
        {
            var counts = bars.GetValueOrDefault(slot) ?? new long[4];
            return new HistogramBar(fromUtc.AddSeconds(slot * bucketSeconds), counts[0], counts[1], counts[2], counts[3]);
        }).ToList();

        return new LogSearchResult(items, total, facets, histogram, bucketSeconds, Math.Round(watch.Elapsed.TotalMilliseconds, 1));
    }

    private static async Task<IReadOnlyList<FacetValue>> FacetAsync(
        SqlConnection connection, LogQuery query, DateTime fromUtc, string facet, string expression, CancellationToken cancellationToken)
    {
        var sql = $"SELECT TOP (20) {expression} AS Value, COUNT_BIG(*) AS Total FROM RequestLogs WHERE {{where}} GROUP BY {expression} ORDER BY Total DESC";
        await using var command = Build(connection, query, fromUtc, facet, sql, includePaging: false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<FacetValue>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new FacetValue(reader.IsDBNull(0) ? "(none)" : reader.GetString(0), reader.GetInt64(1)));
        }

        return values;
    }

    /// <summary>Builds the shared WHERE clause; <paramref name="skipFacet"/> leaves out the facet being counted.</summary>
    private static SqlCommand Build(SqlConnection connection, LogQuery query, DateTime fromUtc, string? skipFacet, string template, bool includePaging)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        var where = new StringBuilder("TimestampUtc >= @from");
        command.Parameters.AddWithValue("@from", fromUtc);

        if (!string.IsNullOrWhiteSpace(query.Service) && skipFacet != "service")
        {
            where.Append(" AND Service = @service");
            command.Parameters.AddWithValue("@service", query.Service);
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && skipFacet != "status")
        {
            where.Append(query.Status switch
            {
                "blocked" => " AND Blocked = 1",
                "5xx" => " AND Blocked = 0 AND StatusCode >= 500",
                "4xx" => " AND Blocked = 0 AND StatusCode BETWEEN 400 AND 499",
                "3xx" => " AND Blocked = 0 AND StatusCode BETWEEN 300 AND 399",
                _ => " AND Blocked = 0 AND StatusCode < 300"
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Region) && skipFacet != "region")
        {
            where.Append(" AND Region = @region");
            command.Parameters.AddWithValue("@region", query.Region);
        }

        if (!string.IsNullOrWhiteSpace(query.Source) && skipFacet != "source")
        {
            where.Append(" AND Source = @source");
            command.Parameters.AddWithValue("@source", query.Source);
        }

if (!string.IsNullOrWhiteSpace(query.Instance) && skipFacet != "instance")
        {
            where.Append(" AND Instance = @instance");
            command.Parameters.AddWithValue("@instance", query.Instance);
        }

        if (!string.IsNullOrWhiteSpace(query.ClientId))
        {
            where.Append(" AND ClientId = @client");
            command.Parameters.AddWithValue("@client", query.ClientId);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            where.Append(" AND (Path LIKE @q OR ClientId LIKE @q OR TraceId LIKE @q OR BlockReason LIKE @q OR UserId LIKE @q)");
            command.Parameters.AddWithValue("@q", $"%{query.Q.Trim()}%");
        }

        if (includePaging && query.BeforeId is { } beforeId)
        {
            where.Append(" AND Id < @before");
            command.Parameters.AddWithValue("@before", beforeId);
        }

        command.CommandText = template.Replace("{where}", where.ToString());
        return command;
    }

    private static string? NullableString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
