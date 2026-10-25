using System.Diagnostics;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace DigitalBanking.Admin.Api.Ops.Distributed;

public sealed record LookupTiming(string Method, string? Found, double TookMs, string Detail);

public sealed record GlobalLookupResult(string AccountNumber, IReadOnlyList<LookupTiming> Timings, IReadOnlyList<LookupTiming> ScatterGather);

public sealed record GlobalIndexStats(bool RedisConnected, long IndexedByNumber, long IndexedById, long DirectoryRows, long DirectoryWithoutShard);

public sealed record RebuildResult(long Indexed, long ShardsAssigned, double TookMs);

public sealed record ShardView(string Shard, double RingShare, long Accounts);

public sealed record ProvinceRingView(string Province, string Region, int Shards, int VirtualNodes, IReadOnlyList<ShardView> ShardList);

public sealed record RebalancePreview(int FromShards, int ToShards, int SampleKeys, double MovedWithRing, double MovedWithModulo, double IdealMoved);

public sealed record RingView(IReadOnlyList<ProvinceRingView> Provinces, RebalancePreview Rebalance);

/// <summary>
/// The partition-aware global index (Redis, fed from the outbox) and the hash ring inside each province:
/// lookups timed three ways (Redis, SQL directory, scatter-gather), index rebuilds, and the ring's balance.
/// </summary>
public sealed class GlobalIndexService(
    IConnectionMultiplexer redis, RegionalDatabaseOptions options, RegionalDataPlane dataPlane, ProvinceShardMap shards)
{
    public async Task PutAsync(string accountNumber, Guid accountId, string value)
    {
        if (!redis.IsConnected)
        {
            throw new ServiceUnavailableException("Redis is not reachable; the event will be retried.");
        }

        var database = redis.GetDatabase();
        await Task.WhenAll(
            database.HashSetAsync(GlobalAccountIndex.ByNumber, accountNumber, value),
            database.HashSetAsync(GlobalAccountIndex.ById, accountId.ToString(), value));
    }

    public async Task<string?> DirectoryValueAsync(string accountNumber, CancellationToken cancellationToken)
    {
        await using var connection = await OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT Region, AccountId, Shard FROM directory.Accounts WHERE AccountNumber = @number", connection);
        command.Parameters.AddWithValue("@number", accountNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var region = reader.GetString(0);
        return GlobalAccountIndex.Value(region, reader.IsDBNull(2) ? shards.ShardKey(region, reader.GetGuid(1)) : reader.GetString(2));
    }

    public async Task<GlobalIndexStats> StatsAsync(CancellationToken cancellationToken)
    {
        long byNumber = 0, byId = 0;
        if (redis.IsConnected)
        {
            var database = redis.GetDatabase();
            byNumber = await database.HashLengthAsync(GlobalAccountIndex.ByNumber);
            byId = await database.HashLengthAsync(GlobalAccountIndex.ById);
        }

        await using var connection = await OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand("""
            IF OBJECT_ID(N'directory.Accounts') IS NULL SELECT CAST(0 AS bigint), CAST(0 AS bigint)
            ELSE IF COL_LENGTH(N'directory.Accounts', N'Shard') IS NULL SELECT COUNT_BIG(*), COUNT_BIG(*) FROM directory.Accounts
            ELSE EXEC(N'SELECT COUNT_BIG(*), COUNT_BIG(CASE WHEN Shard IS NULL THEN 1 END) FROM directory.Accounts');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new GlobalIndexStats(redis.IsConnected, byNumber, byId, reader.GetInt64(0), reader.GetInt64(1));
    }

    // Rebuilds the Redis index from the SQL directory (the source of truth), assigning ring shards to rows that predate them.
    public async Task<RebuildResult> RebuildAsync(CancellationToken cancellationToken)
    {
        if (!redis.IsConnected)
        {
            throw new ServiceUnavailableException("Redis is not reachable.");
        }

        var watch = Stopwatch.StartNew();
        var rows = new List<(string Number, Guid Id, string Region, string? Shard)>();
        await using (var connection = await OpenCentralAsync(cancellationToken))
        {
            await using var command = new SqlCommand("SELECT AccountNumber, AccountId, Region, Shard FROM directory.Accounts", connection) { CommandTimeout = 300 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var assigned = 0L;
        await using (var connection = await OpenCentralAsync(cancellationToken))
        {
            foreach (var row in rows.Where(row => row.Shard is null))
            {
                await using var update = new SqlCommand("UPDATE directory.Accounts SET Shard = @shard WHERE AccountNumber = @number", connection);
                update.Parameters.AddWithValue("@shard", shards.ShardKey(row.Region, row.Id));
                update.Parameters.AddWithValue("@number", row.Number);
                assigned += await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var database = redis.GetDatabase();
        foreach (var chunk in rows.Chunk(500))
        {
            var byNumber = chunk.Select(row => new HashEntry(row.Number, GlobalAccountIndex.Value(row.Region, row.Shard ?? shards.ShardKey(row.Region, row.Id)))).ToArray();
            var byId = chunk.Select(row => new HashEntry(row.Id.ToString(), GlobalAccountIndex.Value(row.Region, row.Shard ?? shards.ShardKey(row.Region, row.Id)))).ToArray();
            await Task.WhenAll(database.HashSetAsync(GlobalAccountIndex.ByNumber, byNumber), database.HashSetAsync(GlobalAccountIndex.ById, byId));
        }

        return new RebuildResult(rows.Count, assigned, Math.Round(watch.Elapsed.TotalMilliseconds, 1));
    }

    // Where is this account? Answered three ways, so the difference is visible.
    public async Task<GlobalLookupResult> LookupAsync(string accountNumber, CancellationToken cancellationToken)
    {
        accountNumber = accountNumber.Trim();
        var timings = new List<LookupTiming>();

        var watch = Stopwatch.StartNew();
        string? fromRedis = null;
        if (redis.IsConnected)
        {
            var value = await redis.GetDatabase().HashGetAsync(GlobalAccountIndex.ByNumber, accountNumber);
            fromRedis = value.IsNullOrEmpty ? null : value.ToString();
        }

        timings.Add(new LookupTiming("Redis global index (HGET)", fromRedis, Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            redis.IsConnected ? $"HGET {GlobalAccountIndex.ByNumber} {accountNumber}" : "Redis not connected"));

        watch.Restart();
        var fromDirectory = await DirectoryValueAsync(accountNumber, cancellationToken);
        timings.Add(new LookupTiming("SQL directory (central DB)", fromDirectory, Math.Round(watch.Elapsed.TotalMilliseconds, 3), "SELECT ... FROM directory.Accounts WHERE AccountNumber = @n"));

        // Without an index: ask every province and wait for all of them (another one could also have it).
        watch.Restart();
        var scatter = await Task.WhenAll(dataPlane.Clusters.Select(async cluster =>
        {
            var province = cluster.Province.Name;
            var provinceWatch = Stopwatch.StartNew();
            try
            {
                var node = dataPlane.ReadNode(province);
                await using var connection = new SqlConnection(node.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(
                    "IF OBJECT_ID(N'account.Accounts') IS NULL SELECT CAST(NULL AS uniqueidentifier) ELSE SELECT TOP 1 Id FROM account.Accounts WHERE AccountNumber = @n", connection);
                command.Parameters.AddWithValue("@n", accountNumber);
                var id = await command.ExecuteScalarAsync(cancellationToken);
                var found = id is Guid accountId ? shards.ShardKey(cluster.Slug, accountId) : null;
                return new LookupTiming(province, found, Math.Round(provinceWatch.Elapsed.TotalMilliseconds, 3), node.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new LookupTiming(province, null, Math.Round(provinceWatch.Elapsed.TotalMilliseconds, 3), exception.Message);
            }
        }));
        timings.Add(new LookupTiming("Scatter-gather (all 4 provinces)", scatter.FirstOrDefault(item => item.Found is not null)?.Found,
            Math.Round(watch.Elapsed.TotalMilliseconds, 3), "Slowest province decides the latency"));

        return new GlobalLookupResult(accountNumber, timings, scatter);
    }

    public async Task<RingView> RingAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sample = new List<Guid>();
        await using (var connection = await OpenCentralAsync(cancellationToken))
        {
            await using var command = new SqlCommand("""
                IF OBJECT_ID(N'directory.Accounts') IS NOT NULL AND COL_LENGTH(N'directory.Accounts', N'Shard') IS NOT NULL
                BEGIN
                    EXEC(N'SELECT Shard, COUNT_BIG(*) FROM directory.Accounts WHERE Shard IS NOT NULL GROUP BY Shard');
                    SELECT TOP 20000 AccountId FROM directory.Accounts;
                END
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (reader.FieldCount == 2)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    counts[reader.GetString(0)] = reader.GetInt64(1);
                }

                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    sample.Add(reader.GetGuid(0));
                }
            }
        }

        var provinces = dataPlane.Clusters.Select(cluster =>
        {
            var ring = shards.Ring(cluster.Slug);
            var ownership = ring.Ownership();
            var list = Enumerable.Range(0, ring.Shards).Select(shard =>
            {
                var key = ProvinceShardMap.Format(cluster.Slug, shard);
                return new ShardView(key, Math.Round(ownership[shard], 4), counts.GetValueOrDefault(key));
            }).ToList();
            return new ProvinceRingView(cluster.Province.Name, cluster.Slug, ring.Shards, ring.VirtualNodes, list);
        }).ToList();

        // How many keys move when a province grows by one shard: ring ~1/(N+1), "hash mod N" ~N/(N+1).
        while (sample.Count < 20000)
        {
            sample.Add(Guid.NewGuid());
        }

        var from = shards.ShardsPerProvince;
        var before = new ConsistentHashRing("preview", from, shards.VirtualNodes);
        var after = new ConsistentHashRing("preview", from + 1, shards.VirtualNodes);
        var ringMoved = 0;
        var moduloMoved = 0;
        foreach (var id in sample)
        {
            var hash = MurmurHash3.Hash(id.ToString("N"));
            ringMoved += before.Locate(hash) != after.Locate(hash) ? 1 : 0;
            moduloMoved += hash % (uint)from != hash % (uint)(from + 1) ? 1 : 0;
        }

        return new RingView(provinces, new RebalancePreview(from, from + 1, sample.Count,
            Math.Round(ringMoved / (double)sample.Count, 4), Math.Round(moduloMoved / (double)sample.Count, 4), Math.Round(1d / (from + 1), 4)));
    }

    private volatile bool shardColumnReady;

    private async Task<SqlConnection> OpenCentralAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (!shardColumnReady)
        {
            // Same column the account service adds at startup, in case this service starts first.
            await using var command = new SqlCommand("""
                IF OBJECT_ID(N'directory.Accounts') IS NOT NULL AND COL_LENGTH(N'directory.Accounts', N'Shard') IS NULL
                    ALTER TABLE directory.Accounts ADD Shard nvarchar(40) NULL;
                SELECT CASE WHEN OBJECT_ID(N'directory.Accounts') IS NULL THEN 0 ELSE 1 END;
                """, connection);
            shardColumnReady = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        return connection;
    }
}
