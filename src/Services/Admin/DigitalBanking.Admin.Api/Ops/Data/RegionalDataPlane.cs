using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using DigitalBanking.Admin.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Admin.Api.Ops.Data;

public sealed class RegionUnavailableException(string message) : Exception(message);

public sealed class DbNode
{
    public required string Name { get; init; }
    public required string Database { get; init; }
    public required string Zone { get; init; }
    public required string ConnectionString { get; init; }
    public string Role { get; set; } = "replica";
    public string Status { get; set; } = "online";
    public bool NeedsResync { get; set; }
    public long CustomerWatermark { get; set; }
    public long TransactionWatermark { get; set; }
    public int Failures { get; set; }
    public double LatencyMs { get; set; }
    public string? LastError { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public long CustomerRows { get; set; }
    public long TransactionRows { get; set; }
    public double SizeMb { get; set; }
    public Queue<double> LatencySeries { get; } = new();
}

public sealed class RegionCluster
{
    public required Province Province { get; init; }
    public required string Slug { get; init; }
    public required DbNode[] Nodes { get; init; }
    public bool AutoFailover { get; set; } = true;
    public bool ReplicationPaused { get; set; }
    public volatile bool WritesFrozen;
    public long NextCustomerId;
    public long NextTransactionId;
    public long CustomerLag { get; set; }
    public long TransactionLag { get; set; }
    public double ReplicatedPerSecond { get; set; }
    public DateTime? LastReplicatedUtc { get; set; }
    public int Failovers { get; set; }
    public DateTime? LastFailoverUtc { get; set; }
    public long LostRows { get; set; }
    public ConcurrentDictionary<string, long> CityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int[]> BranchesByCity { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DbNode? Primary => Nodes.FirstOrDefault(node => node.Role == "primary");
    public DbNode? Replica => Nodes.FirstOrDefault(node => node.Role == "replica");
    public DbNode? WritablePrimary => !WritesFrozen && Primary is { Status: "online" } primary ? primary : null;

    public string State =>
        WritesFrozen ? "switching"
        : Primary is not { Status: "online" } ? "down"
        : Replica is not { Status: "online", NeedsResync: false } ? "degraded"
        : "healthy";
}

public sealed record NodeView(
    string Name, string Database, string Zone, string Role, string Status, bool NeedsResync, double LatencyMs, double[] LatencySeries,
    long CustomerRows, long TransactionRows, double SizeMb, string? LastError, DateTime LastCheckUtc);

public sealed record ClusterView(
    string Province, string Code, string State, bool AutoFailover, bool ReplicationPaused, long CustomerLag, long TransactionLag,
    DateTime? LastReplicatedUtc, double ReplicatedPerSecond, int Failovers, DateTime? LastFailoverUtc, long LostRows, long Customers,
    IReadOnlyList<NodeView> Nodes);

/// <summary>
/// Four regional SQL Server clusters, each a primary and a replica in separate zones. Handles schema,
/// log-shipping style replication, health checks, automatic failover, planned switchover and resync.
/// </summary>
public sealed class RegionalDataPlane(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    OpsEventLog events,
    ILogger<RegionalDataPlane> logger) : BackgroundService
{
    private static readonly string[] BranchAreas =
        ["Main", "Saddar", "Cantt", "City", "Model Town", "Satellite Town", "University Road", "GT Road", "Industrial Area", "Airport Road", "Civil Lines", "Bazaar"];

    private readonly Dictionary<string, RegionCluster> clusters = new(StringComparer.OrdinalIgnoreCase);
    private string server = "(localdb)\\MSSQLLocalDB";

    public IReadOnlyList<RegionCluster> Clusters => clusters.Values.ToList();

    public RegionCluster Cluster(string province)
    {
        var name = PakistanGeo.FindProvince(province)?.Name ?? province;
        return clusters.TryGetValue(name, out var cluster) ? cluster : throw new ValidationException($"No regional cluster named '{province}'.");
    }

    public DbNode Node(string name)
    {
        return clusters.Values.SelectMany(cluster => cluster.Nodes).FirstOrDefault(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException($"Database node '{name}' was not found.");
    }

    public RegionCluster ClusterOf(DbNode node) => clusters.Values.First(cluster => cluster.Nodes.Contains(node));

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        server = configuration["RegionalDatabases:Server"] ?? server;
        foreach (var province in PakistanGeo.Provinces)
        {
            var slug = province.Code switch { "PB" => "punjab", "SD" => "sindh", "KP" => "kp", _ => "balochistan" };
            var dbName = province.Code == "KP" ? "KP" : province.Name;
            clusters[province.Name] = new RegionCluster
            {
                Province = province,
                Slug = slug,
                Nodes =
                [
                    CreateNode($"{slug}-a", $"DigitalBanking_{dbName}_A", "Zone A"),
                    CreateNode($"{slug}-b", $"DigitalBanking_{dbName}_B", "Zone B")
                ]
            };
        }

        await using (var master = new SqlConnection(BuildConnectionString("master", 15)))
        {
            await master.OpenAsync(cancellationToken);
            foreach (var node in clusters.Values.SelectMany(cluster => cluster.Nodes))
            {
                // Bring every node back online on boot so a node left offline by a drill can be rebuilt.
                await ExecuteAsync(master,
                    $"IF DB_ID(N'{node.Database}') IS NULL CREATE DATABASE [{node.Database}]; " +
                    $"ELSE IF DATABASEPROPERTYEX(N'{node.Database}', 'Status') <> 'ONLINE' ALTER DATABASE [{node.Database}] SET ONLINE;",
                    cancellationToken, 120);
            }
        }

        Dictionary<string, ClusterState> states;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
            states = await dbContext.ClusterStates.AsNoTracking().ToDictionaryAsync(state => state.Province, cancellationToken);
        }

        foreach (var cluster in clusters.Values)
        {
            foreach (var node in cluster.Nodes)
            {
                // A node that cannot be reached at boot starts as "down" instead of stopping the console.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await using var connection = new SqlConnection(BuildConnectionString(node.Database, 30));
                        await connection.OpenAsync(cancellationToken);
                        await ExecuteAsync(connection, SchemaSql, cancellationToken, 60);
                        await SeedBranchesAsync(connection, cluster, cancellationToken);
                        break;
                    }
                    catch (SqlException exception) when (attempt < 3)
                    {
                        logger.LogWarning("Opening {Node} failed (attempt {Attempt}): {Message}", node.Name, attempt, exception.Message);
                        await Task.Delay(2_000, cancellationToken);
                    }
                    catch (SqlException exception)
                    {
                        node.Status = "down";
                        node.LastError = exception.Message;
                        events.Critical("database", node.Name, $"{node.Name} could not be opened at startup: {Shorten(exception.Message)}");
                        break;
                    }
                }
            }

            var primaryName = states.TryGetValue(cluster.Province.Name, out var state) ? state.PrimaryNode : cluster.Nodes[0].Name;
            foreach (var node in cluster.Nodes)
            {
                node.Role = node.Name == primaryName ? "primary" : "replica";
            }

            if (cluster.Primary is null)
            {
                cluster.Nodes[0].Role = "primary";
            }

            cluster.AutoFailover = state?.AutoFailover ?? true;
            if (cluster.Primary is { Status: "down" } && cluster.Replica is { Status: "online" } standby)
            {
                cluster.Primary!.Role = "replica";
                standby.Role = "primary";
            }

            foreach (var node in cluster.Nodes.Where(node => node.Status == "online"))
            {
                var (customerMax, transactionMax) = await MaxIdsAsync(node, cancellationToken);
                cluster.NextCustomerId = Math.Max(cluster.NextCustomerId, customerMax);
                cluster.NextTransactionId = Math.Max(cluster.NextTransactionId, transactionMax);
            }

            await RefreshCityCountsAsync(cluster, cancellationToken);
        }
    }

    private DbNode CreateNode(string name, string database, string zone) => new()
    {
        Name = name,
        Database = database,
        Zone = zone,
        ConnectionString = BuildConnectionString(database, 3)
    };

    private string BuildConnectionString(string database, int connectTimeout) =>
        $"Data Source={server};Initial Catalog={database};Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout={connectTimeout};Max Pool Size=200;";

    private const string SchemaSql = """
        IF OBJECT_ID(N'dbo.Branches', N'U') IS NULL
            CREATE TABLE dbo.Branches (
                Id int NOT NULL CONSTRAINT PK_Branches PRIMARY KEY,
                Code varchar(12) NOT NULL,
                Name nvarchar(80) NOT NULL,
                City nvarchar(40) NOT NULL,
                Lat float NOT NULL,
                Lng float NOT NULL,
                OpenedOn date NOT NULL);
        IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Customers (
                Id bigint NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                CustomerNo varchar(20) NOT NULL,
                FullName nvarchar(80) NOT NULL,
                Cnic varchar(15) NOT NULL,
                Phone varchar(12) NOT NULL,
                Gender char(1) NOT NULL,
                City nvarchar(40) NOT NULL,
                BranchId int NOT NULL,
                AccountType nvarchar(24) NOT NULL,
                Balance decimal(18, 2) NOT NULL,
                JobId int NOT NULL,
                CreatedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_Customers_City ON dbo.Customers (City);
            CREATE INDEX IX_Customers_Cnic ON dbo.Customers (Cnic);
            CREATE INDEX IX_Customers_Phone ON dbo.Customers (Phone);
            CREATE INDEX IX_Customers_FullName ON dbo.Customers (FullName);
        END
        IF OBJECT_ID(N'dbo.Transactions', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Transactions (
                Id bigint NOT NULL CONSTRAINT PK_Transactions PRIMARY KEY,
                CustomerId bigint NOT NULL,
                City nvarchar(40) NOT NULL,
                Channel nvarchar(24) NOT NULL,
                Type nvarchar(24) NOT NULL,
                Amount decimal(18, 2) NOT NULL,
                Status varchar(12) NOT NULL,
                FailureReason nvarchar(80) NULL,
                LatencyMs int NOT NULL,
                CreatedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_Transactions_CreatedAtUtc ON dbo.Transactions (CreatedAtUtc);
            CREATE INDEX IX_Transactions_CustomerId ON dbo.Transactions (CustomerId);
        END
        """;

    private static async Task SeedBranchesAsync(SqlConnection connection, RegionCluster cluster, CancellationToken cancellationToken)
    {
        // Deterministic so both nodes of a cluster hold identical branch rows.
        var random = new Random(cluster.Province.Code[0] * 131 + cluster.Province.Code[1]);
        var rows = new List<(int Id, string Code, string Name, string City, double Lat, double Lng, DateTime Opened)>();
        for (var cityIndex = 0; cityIndex < cluster.Province.Cities.Count; cityIndex++)
        {
            var city = cluster.Province.Cities[cityIndex];
            var count = Math.Clamp((int)Math.Round(Math.Sqrt(city.Weight) * 3), 2, BranchAreas.Length);
            var ids = new int[count];
            for (var index = 0; index < count; index++)
            {
                var id = (cityIndex + 1) * 100 + index + 1;
                ids[index] = id;
                rows.Add((id, $"{cluster.Province.Code}-{id:D4}", $"{city.Name} {BranchAreas[index]}", city.Name,
                    city.Lat + (random.NextDouble() - 0.5) * 0.08, city.Lng + (random.NextDouble() - 0.5) * 0.08,
                    new DateTime(1995 + random.Next(0, 28), random.Next(1, 13), random.Next(1, 28))));
            }

            cluster.BranchesByCity[city.Name] = ids;
        }

        await using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.Branches", connection);
        if ((int)(await check.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.Branches (Id, Code, Name, City, Lat, Lng, OpenedOn) VALUES (@id, @code, @name, @city, @lat, @lng, @opened)", connection);
            insert.Parameters.AddWithValue("@id", row.Id);
            insert.Parameters.AddWithValue("@code", row.Code);
            insert.Parameters.AddWithValue("@name", row.Name);
            insert.Parameters.AddWithValue("@city", row.City);
            insert.Parameters.AddWithValue("@lat", row.Lat);
            insert.Parameters.AddWithValue("@lng", row.Lng);
            insert.Parameters.AddWithValue("@opened", row.Opened);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // ---- Write and read routing -------------------------------------------------------------

    public long CustomerCount(string province) => Interlocked.Read(ref Cluster(province).NextCustomerId);

    public long AllocateCustomerIds(RegionCluster cluster, int count) => Interlocked.Add(ref cluster.NextCustomerId, count) - count + 1;

    public long AllocateTransactionIds(RegionCluster cluster, int count) => Interlocked.Add(ref cluster.NextTransactionId, count) - count + 1;

    public long PickCustomerId(string province, Random random)
    {
        var max = CustomerCount(province);
        return max <= 0 ? 0 : random.NextInt64(1, max + 1);
    }

    public bool IsWritable(string province) => Cluster(province).WritablePrimary is not null;

    public async Task<SqlConnection> OpenPrimaryAsync(string province, CancellationToken cancellationToken)
    {
        var cluster = Cluster(province);
        var primary = cluster.WritablePrimary
            ?? throw new RegionUnavailableException(cluster.WritesFrozen
                ? $"{province} is switching primaries"
                : $"{province} has no writable primary");
        var connection = new SqlConnection(primary.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException exception)
        {
            SqlConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw new RegionUnavailableException($"{primary.Name} refused the connection: {exception.Message}");
        }
    }

    /// <summary>Reads go to the replica when it is in sync, offloading the primary (read scaling).</summary>
    public DbNode ReadNode(string province)
    {
        var cluster = Cluster(province);
        if (cluster.Replica is { Status: "online", NeedsResync: false } replica && cluster.CustomerLag < 5_000)
        {
            return replica;
        }

        return cluster.Primary is { Status: "online" } primary
            ? primary
            : throw new RegionUnavailableException($"{province} has no readable node");
    }

    public async Task BulkInsertAsync(string province, string table, DataTable rows, int injectedLatencyMs, CancellationToken cancellationToken)
    {
        await using var connection = await OpenPrimaryAsync(province, cancellationToken);
        if (injectedLatencyMs > 0)
        {
            await ExecuteAsync(connection, $"WAITFOR DELAY '00:00:{injectedLatencyMs / 1000:D2}.{injectedLatencyMs % 1000:D3}'", cancellationToken, 30);
        }

        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = $"dbo.{table}", BulkCopyTimeout = 120 };
        foreach (DataColumn column in rows.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(rows, cancellationToken);
    }

    public void TrackCities(RegionCluster cluster, IReadOnlyDictionary<string, long> added)
    {
        foreach (var (city, count) in added)
        {
            cluster.CityCounts.AddOrUpdate(city, count, (_, existing) => existing + count);
        }
    }

    // ---- Admin actions -------------------------------------------------------------------------

    public async Task TakeOfflineAsync(string nodeName, CancellationToken cancellationToken)
    {
        var node = Node(nodeName);
        var role = node.Role;
        events.Critical("database", node.Name, $"{node.Name} ({role}) taken offline by admin. Simulated zone outage.");
        await using var master = new SqlConnection(BuildConnectionString("master", 15));
        await master.OpenAsync(cancellationToken);
        await ExecuteAsync(master, $"ALTER DATABASE [{node.Database}] SET OFFLINE WITH ROLLBACK IMMEDIATE", cancellationToken, 60);
        SqlConnection.ClearAllPools();
    }

    public async Task BringOnlineAsync(string nodeName, CancellationToken cancellationToken)
    {
        var node = Node(nodeName);
        await using var master = new SqlConnection(BuildConnectionString("master", 15));
        await master.OpenAsync(cancellationToken);
        await ExecuteAsync(master, $"ALTER DATABASE [{node.Database}] SET ONLINE", cancellationToken, 120);
        if (node.Status == "maintenance")
        {
            node.Status = "online";
        }

        events.Info("database", node.Name, $"{node.Name} brought back online. Waiting for health checks.");
    }

    public async Task SwitchoverAsync(string province, string reason, CancellationToken cancellationToken)
    {
        var cluster = Cluster(province);
        await cluster.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cluster.Primary is not { Status: "online" } primary || cluster.Replica is not { Status: "online", NeedsResync: false } replica)
            {
                throw new BusinessRuleException("A planned switchover needs both nodes online and in sync. Use force failover instead.");
            }

            var watch = Stopwatch.StartNew();
            cluster.WritesFrozen = true;
            await Task.Delay(400, cancellationToken);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await ReplicateAsync(cluster, primary, replica, cancellationToken);
                if (cluster.CustomerLag == 0 && cluster.TransactionLag == 0)
                {
                    break;
                }
            }

            await PromoteAsync(cluster, replica, planned: true, cancellationToken);
            events.Success("database", cluster.Province.Name,
                $"Planned switchover ({reason}): {replica.Name} is now primary. Writes paused {watch.ElapsedMilliseconds} ms, zero data loss.");
        }
        finally
        {
            cluster.WritesFrozen = false;
            cluster.Gate.Release();
        }
    }

    public async Task ForceFailoverAsync(string province, CancellationToken cancellationToken)
    {
        var cluster = Cluster(province);
        await cluster.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cluster.Replica is not { Status: "online", NeedsResync: false } replica)
            {
                throw new BusinessRuleException("The replica is not healthy enough to promote.");
            }

            await PromoteAsync(cluster, replica, planned: false, cancellationToken);
        }
        finally
        {
            cluster.Gate.Release();
        }
    }

    public async Task SetMaintenanceAsync(string nodeName, bool enabled, CancellationToken cancellationToken)
    {
        var node = Node(nodeName);
        var cluster = ClusterOf(node);
        if (enabled && node.Role == "primary")
        {
            await SwitchoverAsync(cluster.Province.Name, $"maintenance on {node.Name}", cancellationToken);
        }

        await cluster.Gate.WaitAsync(cancellationToken);
        try
        {
            node.Status = enabled ? "maintenance" : "online";
            if (enabled)
            {
                events.Warning("database", node.Name, $"{node.Name} entered maintenance. Replication to it is paused.");
            }
            else
            {
                events.Info("database", node.Name, $"{node.Name} left maintenance and is catching up.");
            }
        }
        finally
        {
            cluster.Gate.Release();
        }
    }

    public async Task ConfigureAsync(string province, bool? autoFailover, bool? replicationPaused, CancellationToken cancellationToken)
    {
        var cluster = Cluster(province);
        if (autoFailover is { } auto)
        {
            cluster.AutoFailover = auto;
            await SaveStateAsync(cluster, cancellationToken);
            events.Info("database", cluster.Province.Name, $"Automatic failover {(auto ? "enabled" : "disabled")}.");
        }

        if (replicationPaused is { } paused)
        {
            cluster.ReplicationPaused = paused;
            if (paused)
            {
                events.Warning("database", cluster.Province.Name, "Replication paused. The replica will fall behind (RPO grows).");
            }
            else
            {
                events.Info("database", cluster.Province.Name, "Replication resumed.");
            }
        }
    }

    /// <summary>Brings every node online, ends maintenance and resumes replication with auto-failover on.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using (var master = new SqlConnection(BuildConnectionString("master", 15)))
        {
            await master.OpenAsync(cancellationToken);
            foreach (var node in clusters.Values.SelectMany(cluster => cluster.Nodes))
            {
                await ExecuteAsync(master,
                    $"IF DATABASEPROPERTYEX(N'{node.Database}', 'Status') <> 'ONLINE' ALTER DATABASE [{node.Database}] SET ONLINE;", cancellationToken, 120);
            }
        }

        foreach (var cluster in clusters.Values)
        {
            cluster.ReplicationPaused = false;
            foreach (var node in cluster.Nodes.Where(node => node.Status == "maintenance"))
            {
                node.Status = "online";
            }

            if (!cluster.AutoFailover)
            {
                cluster.AutoFailover = true;
                await SaveStateAsync(cluster, cancellationToken);
            }
        }
    }

    // ---- Background loops ----------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var replication = Task.Run(() => ReplicationLoopAsync(stoppingToken), stoppingToken);
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(clusters.Values.Select(cluster => CheckClusterAsync(cluster, stoppingToken)));
                if (tick % 3 == 0)
                {
                    await Task.WhenAll(clusters.Values.SelectMany(cluster => cluster.Nodes).Where(node => node.Status != "down").Select(node => SampleAsync(node, stoppingToken)));
                }

                if (tick % 15 == 0)
                {
                    foreach (var cluster in clusters.Values)
                    {
                        await RefreshCityCountsAsync(cluster, stoppingToken);
                    }
                }

                if (tick % 600 == 0)
                {
                    await RetentionAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cluster health loop failed.");
            }

            tick++;
            await Task.Delay(1_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }

        await replication;
    }

    private async Task CheckClusterAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(cluster.Nodes.Select(node => PingAsync(node, cancellationToken)));
        await cluster.Gate.WaitAsync(cancellationToken);
        try
        {
            for (var index = 0; index < cluster.Nodes.Length; index++)
            {
                var node = cluster.Nodes[index];
                var (ok, latency, error) = results[index];
                node.LastCheckUtc = DateTime.UtcNow;
                node.LatencySeries.Enqueue(ok ? latency : -1);
                while (node.LatencySeries.Count > 60)
                {
                    node.LatencySeries.Dequeue();
                }

                if (ok)
                {
                    node.Failures = 0;
                    node.LatencyMs = latency;
                    node.LastError = null;
                    if (node.Status == "down")
                    {
                        node.Status = "online";
                        events.Success("database", node.Name, $"{node.Name} passed health checks again ({latency:0} ms).");
                        if (node.NeedsResync && node.Role == "replica")
                        {
                            await ResyncAsync(cluster, node, cancellationToken);
                        }
                    }

                    continue;
                }

                node.Failures++;
                node.LastError = error;
                if (node.Status == "online" && node.Failures >= 2)
                {
                    node.Status = "down";
                    events.Critical("database", node.Name, $"{node.Name} ({node.Role}) failed {node.Failures} health checks: {Shorten(error)}");
                    if (node.Role == "primary")
                    {
                        var candidate = cluster.Replica;
                        if (cluster.AutoFailover && candidate is { Status: "online", NeedsResync: false })
                        {
                            await PromoteAsync(cluster, candidate, planned: false, cancellationToken);
                        }
                        else
                        {
                            events.Critical("database", cluster.Province.Name,
                                cluster.AutoFailover
                                    ? $"{cluster.Province.Name} has no healthy replica to promote. Regional writes are failing."
                                    : $"{cluster.Province.Name} primary is down and automatic failover is off. Promote the replica manually.");
                        }
                    }
                }
            }
        }
        finally
        {
            cluster.Gate.Release();
        }
    }

    private async Task PromoteAsync(RegionCluster cluster, DbNode target, bool planned, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var old = cluster.Primary;
        var atRisk = cluster.CustomerLag + cluster.TransactionLag;
        target.Role = "primary";
        if (old is not null && old != target)
        {
            old.Role = "replica";
            if (!planned)
            {
                // Anything the old primary holds above these ids never reached the new primary.
                var (customerMax, transactionMax) = await MaxIdsAsync(target, cancellationToken);
                old.CustomerWatermark = customerMax;
                old.TransactionWatermark = transactionMax;
                old.NeedsResync = true;
            }
        }

        cluster.Failovers++;
        cluster.LastFailoverUtc = DateTime.UtcNow;
        await SaveStateAsync(cluster, cancellationToken);
        if (!planned)
        {
            events.Critical("database", cluster.Province.Name,
                $"Automatic failover: {target.Name} ({target.Zone}) promoted to primary in {watch.ElapsedMilliseconds} ms. " +
                $"{atRisk:N0} rows were not yet replicated (RPO).");
        }
    }

    private async Task ResyncAsync(RegionCluster cluster, DbNode node, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "DELETE FROM dbo.Customers WHERE Id > @c; DECLARE @a int = @@ROWCOUNT; DELETE FROM dbo.Transactions WHERE Id > @t; SELECT @a + @@ROWCOUNT;",
                connection) { CommandTimeout = 300 };
            command.Parameters.AddWithValue("@c", node.CustomerWatermark);
            command.Parameters.AddWithValue("@t", node.TransactionWatermark);
            var discarded = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            cluster.LostRows += discarded;
            node.NeedsResync = false;
            events.Warning("database", node.Name,
                $"{node.Name} rejoined as replica. Discarded {discarded:N0} rows that never reached the new primary, then resumed replication.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            node.LastError = exception.Message;
            logger.LogWarning(exception, "Resync of {Node} failed.", node.Name);
        }
    }

    private async Task ReplicationLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(clusters.Values.Select(async cluster =>
            {
                try
                {
                    if (cluster.WritesFrozen || cluster.Primary is not { Status: "online" } primary)
                    {
                        return;
                    }

                    var replica = cluster.Replica;
                    if (replica is not { Status: "online", NeedsResync: false })
                    {
                        return;
                    }

                    if (cluster.ReplicationPaused)
                    {
                        var (primaryCustomers, primaryTransactions) = await MaxIdsAsync(primary, stoppingToken);
                        var (replicaCustomers, replicaTransactions) = await MaxIdsAsync(replica, stoppingToken);
                        cluster.CustomerLag = Math.Max(0, primaryCustomers - replicaCustomers);
                        cluster.TransactionLag = Math.Max(0, primaryTransactions - replicaTransactions);
                        cluster.ReplicatedPerSecond = 0;
                        return;
                    }

                    if (await cluster.Gate.WaitAsync(0, stoppingToken))
                    {
                        try
                        {
                            await ReplicateAsync(cluster, primary, replica, stoppingToken);
                        }
                        finally
                        {
                            cluster.Gate.Release();
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Replication for {Province} failed: {Message}", cluster.Province.Name, exception.Message);
                }
            }));

            await Task.Delay(1_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    /// <summary>Ships every row above the replica's high-water mark, table by table.</summary>
    private async Task ReplicateAsync(RegionCluster cluster, DbNode primary, DbNode replica, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var source = new SqlConnection(primary.ConnectionString);
        await using var target = new SqlConnection(replica.ConnectionString);
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);

        long copied = 0;
        foreach (var table in new[] { "Customers", "Transactions" })
        {
            // Read both high-water marks before streaming: the source connection can only run one reader at a time.
            var primaryMax = await ScalarLongAsync(source, $"SELECT ISNULL(MAX(Id), 0) FROM dbo.{table}", cancellationToken);
            var replicaMax = await ScalarLongAsync(target, $"SELECT ISNULL(MAX(Id), 0) FROM dbo.{table}", cancellationToken);
            long rows = 0;
            if (primaryMax > replicaMax)
            {
                await using var read = new SqlCommand($"SELECT TOP (50000) * FROM dbo.{table} WHERE Id > @max ORDER BY Id", source) { CommandTimeout = 120 };
                read.Parameters.AddWithValue("@max", replicaMax);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                using var bulk = new SqlBulkCopy(target) { DestinationTableName = $"dbo.{table}", BulkCopyTimeout = 120, NotifyAfter = 1 };
                bulk.SqlRowsCopied += (_, args) => rows = args.RowsCopied;
                await bulk.WriteToServerAsync(reader, cancellationToken);
                copied += rows;
            }

            var lag = Math.Max(0, primaryMax - replicaMax - rows);
            if (table == "Customers") cluster.CustomerLag = lag; else cluster.TransactionLag = lag;
        }

        cluster.ReplicatedPerSecond = Math.Round(copied / Math.Max(1, watch.Elapsed.TotalSeconds + 1));
        if (copied > 0 || cluster.CustomerLag + cluster.TransactionLag == 0)
        {
            cluster.LastReplicatedUtc = DateTime.UtcNow;
        }
    }

    private static async Task<(bool Ok, double LatencyMs, string? Error)> PingAsync(DbNode node, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = new SqlConnection(node.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 2 };
            await command.ExecuteScalarAsync(cancellationToken);
            return (true, Math.Round(watch.Elapsed.TotalMilliseconds, 1), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqlConnection.ClearPool(connection);
            return (false, watch.Elapsed.TotalMilliseconds, exception.Message);
        }
    }

    private async Task SampleAsync(DbNode node, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
                SELECT
                    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('dbo.Customers') AND index_id IN (0, 1)),
                    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('dbo.Transactions') AND index_id IN (0, 1)),
                    (SELECT CAST(SUM(CAST(size AS bigint)) * 8 / 1024.0 AS float) FROM sys.database_files)
                """, connection) { CommandTimeout = 5 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                node.CustomerRows = reader.GetInt64(0);
                node.TransactionRows = reader.GetInt64(1);
                node.SizeMb = Math.Round(reader.GetDouble(2), 1);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug("Sampling {Node} failed: {Message}", node.Name, exception.Message);
        }
    }

    private async Task RefreshCityCountsAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        try
        {
            var node = ReadNode(cluster.Province.Name);
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT City, COUNT_BIG(*) FROM dbo.Customers GROUP BY City", connection) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var fresh = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken))
            {
                fresh[reader.GetString(0)] = reader.GetInt64(1);
            }

            foreach (var city in cluster.Province.Cities)
            {
                cluster.CityCounts[city.Name] = fresh.GetValueOrDefault(city.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug("City counts for {Province} failed: {Message}", cluster.Province.Name, exception.Message);
        }
    }

    private async Task RetentionAsync(CancellationToken cancellationToken)
    {
        foreach (var node in clusters.Values.SelectMany(cluster => cluster.Nodes).Where(node => node.Status == "online"))
        {
            try
            {
                await using var connection = new SqlConnection(node.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, "DELETE TOP (100000) FROM dbo.Transactions WHERE CreatedAtUtc < DATEADD(hour, -6, SYSUTCDATETIME())", cancellationToken, 120);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug("Retention on {Node} failed: {Message}", node.Name, exception.Message);
            }
        }
    }

    private async Task SaveStateAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var state = await dbContext.ClusterStates.FirstOrDefaultAsync(item => item.Province == cluster.Province.Name, cancellationToken);
        if (state is null)
        {
            state = new ClusterState { Province = cluster.Province.Name };
            dbContext.ClusterStates.Add(state);
        }

        state.PrimaryNode = cluster.Primary?.Name ?? cluster.Nodes[0].Name;
        state.AutoFailover = cluster.AutoFailover;
        state.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<(long Customers, long Transactions)> MaxIdsAsync(DbNode node, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(node.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT ISNULL((SELECT MAX(Id) FROM dbo.Customers), 0), ISNULL((SELECT MAX(Id) FROM dbo.Transactions), 0)", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, int timeoutSeconds)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Shorten(string? message) => message is null ? "unknown error" : message.Length > 140 ? message[..140] + "…" : message;

    public IReadOnlyList<ClusterView> Snapshot()
    {
        return clusters.Values.Select(cluster => new ClusterView(
            cluster.Province.Name, cluster.Province.Code, cluster.State, cluster.AutoFailover, cluster.ReplicationPaused,
            cluster.CustomerLag, cluster.TransactionLag, cluster.LastReplicatedUtc, cluster.ReplicatedPerSecond, cluster.Failovers,
            cluster.LastFailoverUtc, cluster.LostRows, Interlocked.Read(ref cluster.NextCustomerId),
            cluster.Nodes.Select(node => new NodeView(
                node.Name, node.Database, node.Zone, node.Role, node.Status, node.NeedsResync, node.LatencyMs, node.LatencySeries.ToArray(),
                node.CustomerRows, node.TransactionRows, node.SizeMb, node.LastError, node.LastCheckUtc)).ToList())).ToList();
    }

    public IEnumerable<(string Key, string Label, string ConnectionString, string Region, string Role, string Status)> DatabaseCatalog()
    {
        foreach (var cluster in clusters.Values)
        {
            foreach (var node in cluster.Nodes)
            {
                yield return (node.Name, $"{node.Database}", node.ConnectionString, cluster.Province.Name, node.Role, node.Status);
            }
        }
    }
}
