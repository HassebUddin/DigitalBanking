using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.BuildingBlocks.Distributed;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops.Distributed;

public sealed record BloomProvinceView(
    string Province, string? Node, long Keys, long Bits, int Hashes, long SizeBytes, double EstimatedFalsePositiveRate,
    long ChangeVersion, DateTime? BuiltAtUtc, DateTime? RefreshedAtUtc, double BuildMs, bool Fresh, string? Error);

public sealed record BloomProbe(string Province, bool MightContain, bool Fresh);

/// <summary>
/// Semi-join (Bloom join) for bank-wide CNIC searches. Each province's CNICs are summarised in a Bloom filter, a
/// bit array about 1.2 bytes per customer; it is the "reduced relation" of the semi-join. A global search for one
/// CNIC tests it against the four filters first (no I/O) and queries only the provinces whose filter answers
/// "probably here" (usually exactly one), instead of scatter-gathering all four databases.
///
/// A Bloom filter has false positives (an extra province queried now and then) but never false negatives, so the
/// result is exactly what a full scatter-gather returns, as long as every filter is current. The filters follow
/// the databases through SQL Server change tracking (every few seconds, only the changed rows), and a filter
/// that is not current is simply not used: that province is searched as before.
/// </summary>
public sealed class CnicBloomIndex(RegionalDataPlane dataPlane, ILogger<CnicBloomIndex> logger) : BackgroundService
{
    private const double FalsePositiveRate = 0.01;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private sealed class ProvinceFilter
    {
        public required BloomFilter Filter { get; init; }
        public required string Node { get; init; }
        public required string Database { get; init; }
        public long Version { get; set; }
        public DateTime BuiltAtUtc { get; init; }
        public DateTime RefreshedAtUtc { get; set; }
        public double BuildMs { get; init; }
    }

    private readonly ConcurrentDictionary<string, ProvinceFilter> filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);

    // CNICs are compared as their 13 digits, so "35202-1234567-1" and "3520212345671" are the same key.
    public static string? NormalizeCnic(string? text)
    {
        var digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 13 ? digits : null;
    }

    public IReadOnlyList<BloomProbe> Probe(string cnic, IEnumerable<string> provinces) =>
        provinces.Select(province => filters.TryGetValue(province, out var filter) && IsFresh(province, filter)
            ? new BloomProbe(province, filter.Filter.MightContain(cnic), true)
            : new BloomProbe(province, true, false)).ToList();

    public IReadOnlyList<BloomProvinceView> Snapshot() => dataPlane.Clusters.Select(cluster =>
    {
        var province = cluster.Province.Name;
        errors.TryGetValue(province, out var error);
        return filters.TryGetValue(province, out var filter)
            ? new BloomProvinceView(province, filter.Node, filter.Filter.Count, filter.Filter.BitCount, filter.Filter.HashCount, filter.Filter.SizeBytes,
                Math.Round(filter.Filter.EstimatedFalsePositiveRate, 5), filter.Version, filter.BuiltAtUtc, filter.RefreshedAtUtc, filter.BuildMs,
                IsFresh(province, filter), error)
            : new BloomProvinceView(province, null, 0, 0, 0, 0, 0, 0, null, null, 0, false, error ?? "Not built yet");
    }).ToList();

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        filters.Clear();
        await RefreshAllAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the data plane finish opening the province databases first.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAllAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    // A filter of another node (after a failover the read node changes) or past its freshness window is not trusted.
    private bool IsFresh(string province, ProvinceFilter filter)
    {
        try
        {
            return DateTime.UtcNow - filter.RefreshedAtUtc < FreshFor && dataPlane.ReadNode(province).Database == filter.Database;
        }
        catch (RegionUnavailableException)
        {
            return false;
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(dataPlane.Clusters.Select(cluster => RefreshAsync(cluster.Province.Name, cancellationToken)));
    }

    private async Task RefreshAsync(string province, CancellationToken cancellationToken)
    {
        try
        {
            var node = dataPlane.ReadNode(province);
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            if (await ScalarAsync(connection, "SELECT CASE WHEN OBJECT_ID(N'customer.Customers') IS NULL THEN 0 ELSE 1 END", cancellationToken) == 0)
            {
                errors[province] = "No customer table yet.";
                return;
            }

            if (filters.TryGetValue(province, out var filter) && filter.Database == node.Database
                && await ScalarAsync(connection, "SELECT ISNULL(CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'customer.Customers')), -1)", cancellationToken) is var minimum
                && minimum >= 0 && minimum <= filter.Version)
            {
                // Incremental: only customers inserted or updated since the last refresh (a changed CNIC is added;
                // the old one stays as a harmless false positive until the next full build).
                await using var command = new SqlCommand("""
                    SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRANSACTION;
                    SELECT ISNULL(CHANGE_TRACKING_CURRENT_VERSION(), 0);
                    SELECT c.NationalId FROM CHANGETABLE(CHANGES customer.Customers, @version) AS ct
                    JOIN customer.Customers c ON c.Id = ct.Id
                    WHERE ct.SYS_CHANGE_OPERATION IN (N'I', N'U');
                    COMMIT;
                    """, connection) { CommandTimeout = 60 };
                command.Parameters.AddWithValue("@version", filter.Version);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var version = reader.GetInt64(0);
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (NormalizeCnic(reader.GetString(0)) is { } cnic)
                    {
                        filter.Filter.Add(cnic);
                    }
                }

                filter.Version = version;
                filter.RefreshedAtUtc = DateTime.UtcNow;
                errors.TryRemove(province, out _);
                return;
            }

            filters[province] = await BuildAsync(node, connection, cancellationToken);
            errors.TryRemove(province, out _);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors[province] = exception.Message;
            logger.LogDebug("Bloom filter of {Province} not refreshed: {Message}", province, exception.Message);
        }
    }

    // Full build: every CNIC of the province, read under one snapshot so the change-tracking version matches the rows.
    private static async Task<ProvinceFilter> BuildAsync(DbNode node, SqlConnection connection, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var rows = await ScalarAsync(connection,
            "SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(N'customer.Customers') AND index_id IN (0, 1)",
            cancellationToken);
        // Room to grow before the false-positive rate drifts up (a full build follows a failover or a restart anyway).
        var filter = new BloomFilter((long)(rows * 1.5) + 10_000, FalsePositiveRate);
        await using var command = new SqlCommand("""
            SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRANSACTION;
            SELECT ISNULL(CHANGE_TRACKING_CURRENT_VERSION(), 0);
            SELECT NationalId FROM customer.Customers;
            COMMIT;
            """, connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var version = reader.GetInt64(0);
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (NormalizeCnic(reader.GetString(0)) is { } cnic)
            {
                filter.Add(cnic);
            }
        }

        var now = DateTime.UtcNow;
        return new ProvinceFilter
        {
            Filter = filter, Node = node.Name, Database = node.Database, Version = version, BuiltAtUtc = now, RefreshedAtUtc = now,
            BuildMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1)
        };
    }

    private static async Task<long> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
