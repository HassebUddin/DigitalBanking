using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using DigitalBanking.Admin.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
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

    // Network partition drill: the coordinator cannot reach this node, while the services still can.
    public bool Partitioned { get; set; }

    // When the coordinator last extended this node's primary lease (it stays writable until + LeaseTtl).
    public DateTime? LeaseRenewedUtc { get; set; }

    public bool HoldsLease => Role == "primary" && LeaseRenewedUtc is { } renewed && DateTime.UtcNow < renewed + BankReplicator.LeaseTtl;

    // For a primary that lost its role: the last change-tracking version of it that reached the new primary.
    // Its changes after this version are the writes the new primary never received.
    public long? ShippedUpToVersion { get; set; }
}

public sealed class RegionCluster
{
    // "node:epoch" last written to ops.RegionPrimaries (what the services route by). If that write failed,
    // e.g. the central database was restarting during a failover, the lease loop writes it again.
    public string? PublishedPrimary { get; set; }

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

    // Leader term: raised on every promotion and stamped into the primary's lease, so a node (or a stale
    // coordinator) from an older term can never take the lease back.
    public long Epoch { get; set; } = 1;

    // While true the primary's lease is not renewed (planned switchover: the old primary must fence itself).
    public volatile bool LeaseSuspended;
    public bool WaitingForLease { get; set; }

    // Real banking tables (account, txn, customer, notify, audit), shipped by change tracking.
    public long BankRowsShipped { get; set; }
    public DateTime? BankLastShippedUtc { get; set; }
    public long BankVersion { get; set; }
    public int BankRebuilds { get; set; }
    public long DivergedRows { get; set; }
    public ConcurrentDictionary<string, long> CityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int[]> BranchesByCity { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Roles and leases. Never held for long: lease renewal must not wait behind a slow replication pass.
    public SemaphoreSlim Gate { get; } = new(1, 1);

    // Data shipping (simulated tables and banking tables) and fencing setup.
    public SemaphoreSlim ShipGate { get; } = new(1, 1);

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
    long CustomerRows, long TransactionRows, double SizeMb, string? LastError, DateTime LastCheckUtc,
    bool Partitioned, bool HoldsLease, DateTime? LeaseRenewedUtc);

public sealed record ClusterView(
    string Province, string Code, string State, bool AutoFailover, bool ReplicationPaused, long CustomerLag, long TransactionLag,
    DateTime? LastReplicatedUtc, double ReplicatedPerSecond, int Failovers, DateTime? LastFailoverUtc, long LostRows, long Customers,
    IReadOnlyList<NodeView> Nodes,
    long Epoch, bool WaitingForLease, long BankRowsShipped, DateTime? BankLastShippedUtc, long BankVersion, int BankRebuilds, long DivergedRows);

/// <summary>
/// Four regional SQL Server clusters, each a primary and a replica in separate zones. Handles schema,
/// log-shipping style replication, health checks, automatic failover, planned switchover and resync.
/// </summary>
public sealed class RegionalDataPlane(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    OpsEventLog events,
    BankReplicator bank,
    RegionalDatabaseOptions regionalOptions,
    ILogger<RegionalDataPlane> logger) : BackgroundService
{
    private static readonly string[] BranchAreas =
        ["Main", "Saddar", "Cantt", "City", "Model Town", "Satellite Town", "University Road", "GT Road", "Industrial Area", "Airport Road", "Civil Lines", "Bazaar"];

    private readonly Dictionary<string, RegionCluster> clusters = new(StringComparer.OrdinalIgnoreCase);

    // The synthetic load (dbo.Customers / dbo.Transactions, up to tens of millions of rows) stays on the node it
    // was written to: copying it to the standby would double the disk for data that is not banking data.
    // The real banking tables are always replicated. Set RegionalDatabases:ReplicateSimulatedData=true to copy it too.
    private readonly bool replicateSimulatedData = configuration.GetValue("RegionalDatabases:ReplicateSimulatedData", false);

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

        foreach (var node in clusters.Values.SelectMany(cluster => cluster.Nodes))
        {
            // Bring every node back online on boot so a node left offline by a drill can be rebuilt.
            // A zone whose server is down (e.g. its Docker container is stopped) starts as "down" instead.
            try
            {
                await using var master = new SqlConnection(regionalOptions.MasterConnectionStringFor(node.Database));
                await master.OpenAsync(cancellationToken);
                await ExecuteAsync(master,
                    $"IF DB_ID(N'{node.Database}') IS NULL CREATE DATABASE [{node.Database}]; " +
                    $"ELSE IF DATABASEPROPERTYEX(N'{node.Database}', 'Status') <> 'ONLINE' ALTER DATABASE [{node.Database}] SET ONLINE;",
                    cancellationToken, 120);
            }
            catch (SqlException exception)
            {
                logger.LogWarning("Server of {Node} is not reachable at startup: {Message}", node.Name, exception.Message);
            }
        }

        Dictionary<string, ClusterState> states;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
            states = await dbContext.ClusterStates.AsNoTracking().ToDictionaryAsync(state => state.Province, cancellationToken);
        }

        var topology = await LoadTopologyAsync(cancellationToken);

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
                        await bank.PrepareAsync(connection, node.Name, cancellationToken);
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

            // The services follow ops.RegionPrimaries. Before it existed they always wrote to node A, so that is
            // where the banking data is: start there rather than on a standby that never received it.
            var primaryName = cluster.Nodes[0].Name;
            if (topology.TryGetValue(cluster.Slug, out var known))
            {
                primaryName = known.Node;
                cluster.Epoch = known.Epoch;
            }

            foreach (var node in cluster.Nodes)
            {
                node.Role = node.Name == primaryName ? "primary" : "replica";
                // A previous coordinator may have granted a lease moments ago; treat it as live for one TTL.
                node.LeaseRenewedUtc = node.Role == "primary" ? DateTime.UtcNow : null;
            }

            if (cluster.Primary is null)
            {
                cluster.Nodes[0].Role = "primary";
            }

            cluster.AutoFailover = states.TryGetValue(cluster.Province.Name, out var state) ? state.AutoFailover : true;
            await SaveStateAsync(cluster, cancellationToken);

            foreach (var node in cluster.Nodes.Where(node => node.Status == "online"))
            {
                var (customerMax, transactionMax) = await MaxIdsAsync(node, cancellationToken);
                cluster.NextCustomerId = Math.Max(cluster.NextCustomerId, customerMax);
                cluster.NextTransactionId = Math.Max(cluster.NextTransactionId, transactionMax);
            }

            await RefreshCityCountsAsync(cluster, cancellationToken);
        }

        // Last, after the slow startup work, so the leases are fresh when the health loop takes over renewing them.
        foreach (var cluster in clusters.Values)
        {
            await RenewLeaseAsync(cluster, cancellationToken);
        }
    }

    private DbNode CreateNode(string name, string database, string zone) => new()
    {
        Name = name,
        Database = database,
        Zone = zone,
        ConnectionString = BuildConnectionString(database, 3)
    };

    // Same server and credentials the banking services use for this database (see RegionalDatabaseOptions).
    private string BuildConnectionString(string database, int connectTimeout) =>
        $"{regionalOptions.ConnectionStringFor(database)}Connect Timeout={connectTimeout};Max Pool Size=200;";

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

    // Branch ids of a city. Normally filled when the nodes are opened at startup; if the province's server was
    // still starting then, they are computed here with the same deterministic numbering as SeedBranchesAsync.
    public static int[] BranchIdsFor(RegionCluster cluster, string city)
    {
        if (cluster.BranchesByCity.TryGetValue(city, out var known))
        {
            return known;
        }

        var cityIndex = cluster.Province.Cities.ToList().FindIndex(item => string.Equals(item.Name, city, StringComparison.OrdinalIgnoreCase));
        var weight = cityIndex >= 0 ? cluster.Province.Cities[cityIndex].Weight : 1;
        var count = Math.Clamp((int)Math.Round(Math.Sqrt(weight) * 3), 2, BranchAreas.Length);
        var ids = Enumerable.Range(1, count).Select(index => (cityIndex + 1) * 100 + index).ToArray();
        cluster.BranchesByCity[city] = ids;
        return ids;
    }

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

    /// <summary>
    /// Reads of the simulated customers go to the replica when it holds a copy and is in sync (read scaling).
    /// When that data is not replicated (the default), only the primary has it, so reads go there.
    /// </summary>
    public DbNode ReadNode(string province)
    {
        var cluster = Cluster(province);
        if (replicateSimulatedData && cluster.Replica is { Status: "online", NeedsResync: false } replica && cluster.CustomerLag < 5_000)
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

    // Bulk-loaded customers are real bank customers: each chunk goes into the simulation's dbo.Customers (which the
    // traffic generator uses) and into the bank's customer.Customers (what the admin and staff see), in one transaction
    // on the province's current primary, so the two never disagree.
    public async Task BulkInsertCustomersAsync(string province, DataTable simulated, DataTable profiles, CancellationToken cancellationToken)
    {
        await using var connection = await OpenPrimaryAsync(province, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (rows, destination) in new[] { (simulated, "dbo.Customers"), (profiles, "[customer].[Customers]") })
        {
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction) { DestinationTableName = destination, BulkCopyTimeout = 120 };
            foreach (DataColumn column in rows.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(rows, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
        await using var master = new SqlConnection(regionalOptions.MasterConnectionStringFor(node.Database));
        await master.OpenAsync(cancellationToken);
        await ExecuteAsync(master, $"ALTER DATABASE [{node.Database}] SET OFFLINE WITH ROLLBACK IMMEDIATE", cancellationToken, 60);
        SqlConnection.ClearAllPools();
    }

    public async Task BringOnlineAsync(string nodeName, CancellationToken cancellationToken)
    {
        var node = Node(nodeName);
        await using var master = new SqlConnection(regionalOptions.MasterConnectionStringFor(node.Database));
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
        await cluster.ShipGate.WaitAsync(cancellationToken);
        try
        {
            if (cluster.Primary is not { Status: "online" } primary || cluster.Replica is not { Status: "online", NeedsResync: false } replica)
            {
                throw new BusinessRuleException("A planned switchover needs both nodes online and in sync. Use force failover instead.");
            }

            var watch = Stopwatch.StartNew();
            cluster.WritesFrozen = true;
            // Fence the old primary first: from here on it refuses banking writes, so the final ship is complete.
            cluster.LeaseSuspended = true;
            await DemoteAsync(primary, cluster.Epoch, cancellationToken);
            await Task.Delay(400, cancellationToken);
            for (var attempt = 0; replicateSimulatedData && attempt < 60; attempt++)
            {
                await ReplicateAsync(cluster, primary, replica, cancellationToken);
                if (cluster.CustomerLag == 0 && cluster.TransactionLag == 0)
                {
                    break;
                }
            }

            for (var attempt = 0; attempt < 10 && (await ShipBankAsync(cluster, primary, replica, cancellationToken)).RowsApplied > 0; attempt++)
            {
            }

            await PromoteAsync(cluster, replica, planned: true, cancellationToken);
            events.Success("database", cluster.Province.Name,
                $"Planned switchover ({reason}): {replica.Name} is now primary (epoch {cluster.Epoch}). Writes paused {watch.ElapsedMilliseconds} ms, zero data loss.");
        }
        finally
        {
            cluster.WritesFrozen = false;
            cluster.LeaseSuspended = false;
            cluster.ShipGate.Release();
            cluster.Gate.Release();
        }
    }

    public bool SynchronousCommit => regionalOptions.Topology.SynchronousCommit;

    // Synchronous commit: services acknowledge a money movement only once the standby holds it (RPO 0).
    // Asynchronous: faster, but a failover can lose what the standby had not received yet.
    public async Task SetSynchronousCommitAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(regionalOptions.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("UPDATE ops.ReplicationSettings SET SynchronousCommit = @on, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = 1", connection);
        command.Parameters.AddWithValue("@on", enabled);
        await command.ExecuteNonQueryAsync(cancellationToken);
        regionalOptions.Topology.RefreshNow();
        events.Info("database", "replication", enabled
            ? "Synchronous commit on: money movements wait for the standby (no data loss on failover)."
            : "Asynchronous commit: faster, but a failover can lose the last changes the standby had not received.");
    }

    // Network partition drill: the coordinator loses sight of the node while services can still reach it.
    // If it is the primary, its lease runs out, it fences itself, and only then is the standby promoted.
    public void SetPartition(string nodeName, bool enabled)
    {
        var node = Node(nodeName);
        node.Partitioned = enabled;
        if (enabled)
        {
            events.Critical("database", node.Name,
                $"Network partition: the coordinator can no longer reach {node.Name} ({node.Role}). Services still can.");
        }
        else
        {
            events.Info("database", node.Name, $"Partition healed: {node.Name} is reachable again.");
        }
    }

    public async Task ForceFailoverAsync(string province, CancellationToken cancellationToken)
    {
        var cluster = Cluster(province);
        await cluster.Gate.WaitAsync(cancellationToken);
        await cluster.ShipGate.WaitAsync(cancellationToken);
        try
        {
            if (cluster.Replica is not { Status: "online", NeedsResync: false } replica)
            {
                throw new BusinessRuleException("The replica is not healthy enough to promote.");
            }

            var lossless = false;
            if (cluster.Primary is { } old && old != replica)
            {
                cluster.LeaseSuspended = true;
                if (old.Status == "online" && !old.Partitioned && await DemoteAsync(old, cluster.Epoch, cancellationToken))
                {
                    // Fenced at once: ship what it has so the forced failover loses nothing.
                    await ShipBankAsync(cluster, old, replica, cancellationToken);
                    lossless = true;
                }
                else if (old.LeaseRenewedUtc is { } renewed && renewed + BankReplicator.LeaseTtl > DateTime.UtcNow)
                {
                    // Unreachable: it may still be writable until its lease runs out.
                    await Task.Delay(renewed + BankReplicator.LeaseTtl - DateTime.UtcNow, cancellationToken);
                }
            }

            await PromoteAsync(cluster, replica, planned: lossless, cancellationToken);
        }
        finally
        {
            cluster.LeaseSuspended = false;
            cluster.ShipGate.Release();
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
        foreach (var node in clusters.Values.SelectMany(cluster => cluster.Nodes))
        {
            try
            {
                await using var master = new SqlConnection(regionalOptions.MasterConnectionStringFor(node.Database));
                await master.OpenAsync(cancellationToken);
                await ExecuteAsync(master,
                    $"IF DATABASEPROPERTYEX(N'{node.Database}', 'Status') <> 'ONLINE' ALTER DATABASE [{node.Database}] SET ONLINE;", cancellationToken, 120);
            }
            catch (SqlException exception)
            {
                logger.LogWarning("Could not bring {Node} online: {Message}", node.Name, exception.Message);
            }
        }

        foreach (var cluster in clusters.Values)
        {
            cluster.ReplicationPaused = false;
            foreach (var node in cluster.Nodes.Where(node => node.Status == "maintenance"))
            {
                node.Status = "online";
            }

            foreach (var node in cluster.Nodes)
            {
                node.Partitioned = false;
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
        var health = Task.Run(() => HealthLoopAsync(stoppingToken), stoppingToken);
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

        await Task.WhenAll(replication, health);
    }

    // Health checks, lease renewal and failover: its own loop, so slow statistics never delay a lease renewal.
    private async Task HealthLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(clusters.Values.Select(cluster => CheckClusterAsync(cluster, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cluster health loop failed.");
            }

            await Task.Delay(1_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task CheckClusterAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        // Under the gate: a probe renews the primary's lease, and must never race a promotion that just
        // handed the lease (with a newer epoch) to the other node.
        await cluster.Gate.WaitAsync(cancellationToken);
        try
        {
            var results = await Task.WhenAll(cluster.Nodes.Select(node => ProbeAsync(cluster, node, cancellationToken)));
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
                    if (node.Role == "primary" && !(cluster.AutoFailover && cluster.Replica is { Status: "online", NeedsResync: false }))
                    {
                        events.Critical("database", cluster.Province.Name,
                            cluster.AutoFailover
                                ? $"{cluster.Province.Name} has no healthy replica to promote. Regional writes are failing."
                                : $"{cluster.Province.Name} primary is down and automatic failover is off. Promote the replica manually.");
                    }
                }
            }

            // Leader election: the standby takes over only once the lost primary's lease has expired, so the old
            // primary (which may still be reachable by services, e.g. in a partition) has already fenced itself.
            if (cluster.AutoFailover && cluster.Primary is { Status: "down" } lost && cluster.Replica is { Status: "online", NeedsResync: false } candidate)
            {
                var safeAt = (lost.LeaseRenewedUtc ?? DateTime.MinValue) + BankReplicator.LeaseTtl;
                if (DateTime.UtcNow >= safeAt)
                {
                    await PromoteAsync(cluster, candidate, planned: false, cancellationToken);
                }
                else if (!cluster.WaitingForLease)
                {
                    cluster.WaitingForLease = true;
                    events.Warning("database", cluster.Province.Name,
                        $"Primary {lost.Name} lost. Waiting {(safeAt - DateTime.UtcNow).TotalSeconds:0.0}s for its lease to expire (fencing) before promoting {candidate.Name}.");
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

        // New term. The new primary must hold the lease before any service is pointed at it.
        var epoch = cluster.Epoch + 1;
        await using (var connection = new SqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            if (!await bank.RenewPrimaryAsync(connection, target.Name, epoch, cancellationToken))
            {
                throw new BusinessRuleException($"{target.Name} has already seen a newer epoch; refusing to promote it with epoch {epoch}.");
            }

            if (old is not null && old != target
                && await bank.ReplicationStateAsync(target.ConnectionString, cancellationToken) is { } shipped
                && string.Equals(shipped.SourceDatabase, old.Database, StringComparison.OrdinalIgnoreCase))
            {
                old.ShippedUpToVersion = shipped.LastVersion;
            }

            await bank.ForgetReplicationStateAsync(connection, cancellationToken);
        }

        target.LeaseRenewedUtc = DateTime.UtcNow;
        cluster.Epoch = epoch;
        cluster.WaitingForLease = false;
        target.Role = "primary";
        if (old is not null && old != target)
        {
            old.Role = "replica";
            old.LeaseRenewedUtc = null;
            if (old.Status == "online" && !old.Partitioned)
            {
                await DemoteAsync(old, epoch, cancellationToken);
            }

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
                $"Failover: {target.Name} ({target.Zone}) promoted to primary, epoch {epoch}, in {watch.ElapsedMilliseconds} ms. " +
                $"Services now route {cluster.Province.Name} banking traffic to {target.Database}. {atRisk:N0} simulated rows were not yet replicated (RPO).");
        }
    }

    private async Task ResyncAsync(RegionCluster cluster, DbNode node, CancellationToken cancellationToken)
    {
        try
        {
            // Simulated rows are only trimmed when they are being replicated; otherwise each node keeps its own.
            var discarded = 0L;
            if (replicateSimulatedData)
            {
                await using var connection = new SqlConnection(node.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(
                    "DELETE FROM dbo.Customers WHERE Id > @c; DECLARE @a int = @@ROWCOUNT; DELETE FROM dbo.Transactions WHERE Id > @t; SELECT @a + @@ROWCOUNT;",
                    connection) { CommandTimeout = 300 };
                command.Parameters.AddWithValue("@c", node.CustomerWatermark);
                command.Parameters.AddWithValue("@t", node.TransactionWatermark);
                discarded = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            cluster.LostRows += discarded;
            await QuarantineDivergedAsync(cluster, node, cancellationToken);
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

    // One loop per province, so a slow province never delays another's shipping.
    private Task ReplicationLoopAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(clusters.Values.Select(cluster => Task.Run(() => ReplicateClusterLoopAsync(cluster, stoppingToken), stoppingToken)));

    // Banking changes ship every ~100 ms: services wait for the standby to confirm a money movement before
    // acknowledging it (synchronous commit), so this interval is part of their latency. Simulated rows ship every second.
    private async Task ReplicateClusterLoopAsync(RegionCluster cluster, CancellationToken stoppingToken)
    {
        for (var pass = 0; !stoppingToken.IsCancellationRequested; pass++)
        {
            var prepare = pass % 50 == 0;
            var simulated = pass % 10 == 0;
            await ReplicatePassAsync(cluster, prepare, simulated, stoppingToken);
            await Task.Delay(100, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task ReplicatePassAsync(RegionCluster cluster, bool prepare, bool simulated, CancellationToken stoppingToken)
    {
        try
        {
            // Fencing triggers and change tracking for tables the services created since the last pass.
            if (prepare && await cluster.ShipGate.WaitAsync(0, stoppingToken))
            {
                try
                {
                    await PrepareNodesAsync(cluster, stoppingToken);
                }
                finally
                {
                    cluster.ShipGate.Release();
                }
            }

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
                if (!simulated || !replicateSimulatedData)
                {
                    return;
                }

                var (primaryCustomers, primaryTransactions) = await MaxIdsAsync(primary, stoppingToken);
                var (replicaCustomers, replicaTransactions) = await MaxIdsAsync(replica, stoppingToken);
                cluster.CustomerLag = Math.Max(0, primaryCustomers - replicaCustomers);
                cluster.TransactionLag = Math.Max(0, primaryTransactions - replicaTransactions);
                cluster.ReplicatedPerSecond = 0;
                return;
            }

            if (await cluster.ShipGate.WaitAsync(0, stoppingToken))
            {
                try
                {
                    // Roles may have changed while waiting; only ship from the current primary.
                    if (cluster.Primary != primary || cluster.Replica != replica)
                    {
                        return;
                    }

                    await ShipBankAsync(cluster, primary, replica, stoppingToken);
                    if (simulated && replicateSimulatedData)
                    {
                        try
                        {
                            await ReplicateAsync(cluster, primary, replica, stoppingToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            logger.LogWarning("Simulated-data replication for {Province} failed: {Message}", cluster.Province.Name, exception.Message);
                        }
                    }
                }
                finally
                {
                    cluster.ShipGate.Release();
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Replication for {Province} failed: {Message}", cluster.Province.Name, exception.Message);
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

    // Health check, and lease keeping: the primary's lease is extended, the standby's is kept revoked.
    private async Task<(bool Ok, double LatencyMs, string? Error)> ProbeAsync(RegionCluster cluster, DbNode node, CancellationToken cancellationToken)
    {
        if (node.Partitioned)
        {
            return (false, 0, "Network partition (drill): the coordinator cannot reach this node.");
        }

        var watch = Stopwatch.StartNew();
        await using var connection = new SqlConnection(node.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 2 };
            await command.ExecuteScalarAsync(cancellationToken);
            var latency = Math.Round(watch.Elapsed.TotalMilliseconds, 1);

            if (node.Role == "primary")
            {
                if (!cluster.LeaseSuspended && node.Status != "down")
                {
                    if (await bank.RenewPrimaryAsync(connection, node.Name, cluster.Epoch, cancellationToken))
                    {
                        if (node.LeaseRenewedUtc is { } previous && DateTime.UtcNow - previous > BankReplicator.LeaseTtl / 2)
                        {
                            logger.LogWarning("Lease of {Node} renewed late: {Seconds:0.0}s since the previous renewal.", node.Name, (DateTime.UtcNow - previous).TotalSeconds);
                        }

                        node.LeaseRenewedUtc = DateTime.UtcNow;

                        // Keep the services' routing in step with the coordinator (a failed write is retried here).
                        if (cluster.PublishedPrimary != $"{node.Name}:{cluster.Epoch}")
                        {
                            try
                            {
                                await SaveStateAsync(cluster, cancellationToken);
                                logger.LogInformation("Services now route {Region} to {Node} (epoch {Epoch}).", cluster.Slug, node.Name, cluster.Epoch);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                logger.LogWarning("Could not publish the {Region} primary yet: {Message}", cluster.Slug, exception.Message);
                            }
                        }
                    }
                    else
                    {
                        events.Critical("database", node.Name, $"{node.Name} refused lease epoch {cluster.Epoch}: it has seen a newer epoch.");

                        // Our epoch is stale (e.g. a failover happened while the central database could not record it,
                        // and the coordinator restarted from the old value). Move past it; the next renewal succeeds
                        // and the new epoch is published to the services.
                        cluster.Epoch++;
                    }
                }
            }
            else
            {
                await bank.DemoteAsync(connection, node.Name, cluster.Epoch, cancellationToken);
            }

            return (true, latency, null);
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
                    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('customer.Customers') AND index_id IN (0, 1)),
                    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('dbo.Transactions'),
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
            await using var command = new SqlCommand("SELECT City, COUNT_BIG(*) FROM (SELECT LTRIM(RTRIM(LEFT(ISNULL(Address, ''), CHARINDEX(',', ISNULL(Address, '') + ',') - 1))) AS City FROM customer.Customers) c GROUP BY City", connection) { CommandTimeout = 60 };
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

        // What every banking service routes by (see BuildingBlocks RegionTopology).
        var primary = cluster.Primary ?? cluster.Nodes[0];
        await using var connection = new SqlConnection(regionalOptions.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            MERGE ops.RegionPrimaries AS target
            USING (SELECT @region AS Region) AS source ON target.Region = source.Region
            WHEN MATCHED THEN UPDATE SET PrimaryNode = @node, PrimaryDatabase = @database, Epoch = @epoch, UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Region, PrimaryNode, PrimaryDatabase, Epoch, UpdatedAtUtc) VALUES (@region, @node, @database, @epoch, SYSUTCDATETIME());
            """, connection);
        command.Parameters.AddWithValue("@region", cluster.Slug);
        command.Parameters.AddWithValue("@node", primary.Name);
        command.Parameters.AddWithValue("@database", primary.Database);
        command.Parameters.AddWithValue("@epoch", cluster.Epoch);
        await command.ExecuteNonQueryAsync(cancellationToken);
        cluster.PublishedPrimary = $"{primary.Name}:{cluster.Epoch}";
    }

    private async Task<Dictionary<string, (string Node, long Epoch)>> LoadTopologyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(regionalOptions.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops');
            IF OBJECT_ID(N'ops.RegionPrimaries') IS NULL
                CREATE TABLE ops.RegionPrimaries (
                    Region nvarchar(32) NOT NULL CONSTRAINT PK_RegionPrimaries PRIMARY KEY,
                    PrimaryNode nvarchar(64) NOT NULL, PrimaryDatabase nvarchar(128) NOT NULL,
                    Epoch bigint NOT NULL, UpdatedAtUtc datetime2 NOT NULL);
            IF OBJECT_ID(N'ops.ReplicationSettings') IS NULL
            BEGIN
                CREATE TABLE ops.ReplicationSettings (
                    Id int NOT NULL CONSTRAINT PK_ReplicationSettings PRIMARY KEY CONSTRAINT CK_ReplicationSettings_Single CHECK (Id = 1),
                    SynchronousCommit bit NOT NULL, UpdatedAtUtc datetime2 NOT NULL);
                INSERT ops.ReplicationSettings VALUES (1, 1, SYSUTCDATETIME());
            END
            IF OBJECT_ID(N'ops.DivergedRows') IS NULL
                CREATE TABLE ops.DivergedRows (
                    Id bigint IDENTITY CONSTRAINT PK_DivergedRows PRIMARY KEY,
                    Region nvarchar(32) NOT NULL, Node nvarchar(64) NOT NULL, TableName nvarchar(256) NOT NULL,
                    Operation nvarchar(8) NOT NULL, RowJson nvarchar(max) NULL, DetectedAtUtc datetime2 NOT NULL);
            """, cancellationToken, 60);

        var topology = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("SELECT Region, PrimaryNode, Epoch FROM ops.RegionPrimaries", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            topology[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));
        }

        return topology;
    }

    private async Task RenewLeaseAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        if (cluster.Primary is not { } primary)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(primary.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            if (await bank.RenewPrimaryAsync(connection, primary.Name, cluster.Epoch, cancellationToken))
            {
                primary.LeaseRenewedUtc = DateTime.UtcNow;
            }
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Could not grant the lease to {Node}: {Message}", primary.Name, exception.Message);
        }
    }

    // Revokes a node's lease at once (best effort: an unreachable node fences itself when its lease expires).
    private async Task<bool> DemoteAsync(DbNode node, long epoch, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await bank.DemoteAsync(connection, node.Name, epoch, cancellationToken);
            node.LeaseRenewedUtc = null;
            return true;
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Could not revoke the lease of {Node}: {Message}", node.Name, exception.Message);
            return false;
        }
    }

    private async Task PrepareNodesAsync(RegionCluster cluster, CancellationToken cancellationToken)
    {
        foreach (var node in cluster.Nodes.Where(node => node.Status == "online" && !node.Partitioned))
        {
            try
            {
                await using var connection = new SqlConnection(node.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await bank.PrepareAsync(connection, node.Name, cancellationToken);
            }
            catch (SqlException exception)
            {
                logger.LogWarning("Fencing setup on {Node} failed: {Message}", node.Name, exception.Message);
            }
        }
    }

    private async Task<ShipResult> ShipBankAsync(RegionCluster cluster, DbNode primary, DbNode replica, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bank.ShipAsync(primary.ConnectionString, primary.Database, replica.ConnectionString, cancellationToken);
            cluster.BankRowsShipped += result.RowsApplied;
            cluster.BankVersion = result.SourceVersion;
            cluster.BankLastShippedUtc = DateTime.UtcNow;
            if (result.Rebuilt)
            {
                cluster.BankRebuilds++;
                events.Info("database", replica.Name,
                    $"{replica.Name} rebuilt from {primary.Name}: {result.RowsApplied:N0} banking rows copied. Now following its changes.");
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Banking replication {Primary} -> {Replica} failed: {Message}", primary.Name, replica.Name, exception.Message);
            return new ShipResult(0, false, cluster.BankVersion);
        }
    }

    // A former primary rejoining: banking writes it accepted after the last version shipped from it never
    // reached the new primary. They are kept in ops.DivergedRows for reconciliation, then the node is rebuilt.
    private async Task QuarantineDivergedAsync(RegionCluster cluster, DbNode node, CancellationToken cancellationToken)
    {
        // Whatever happens below, the node follows the new primary from a full copy, never from its own stale watermark.
        await using (var rejoining = new SqlConnection(node.ConnectionString))
        {
            await rejoining.OpenAsync(cancellationToken);
            await bank.ForgetReplicationStateAsync(rejoining, cancellationToken);
        }

        if (cluster.Primary is not { } primary || primary == node || node.ShippedUpToVersion is not { } shippedUpTo)
        {
            return;
        }

        node.ShippedUpToVersion = null;
        var diverged = await bank.UnshippedChangesAsync(node.ConnectionString, shippedUpTo, cancellationToken);
        if (diverged.Count == 0)
        {
            events.Success("database", node.Name, $"{node.Name} rejoined: no banking write was lost (every change had reached {primary.Name}).");
            return;
        }

        await using var connection = new SqlConnection(regionalOptions.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var row in diverged)
        {
            await using var command = new SqlCommand(
                "INSERT ops.DivergedRows (Region, Node, TableName, Operation, RowJson, DetectedAtUtc) VALUES (@region, @node, @table, @op, @row, SYSUTCDATETIME())",
                connection);
            command.Parameters.AddWithValue("@region", cluster.Slug);
            command.Parameters.AddWithValue("@node", node.Name);
            command.Parameters.AddWithValue("@table", row.Table);
            command.Parameters.AddWithValue("@op", row.Operation);
            command.Parameters.AddWithValue("@row", (object?)row.RowJson ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        cluster.DivergedRows += diverged.Count;
        events.Critical("database", node.Name,
            $"{node.Name} rejoined with {diverged.Count:N0} banking changes that never reached {primary.Name}. " +
            "Kept in ops.DivergedRows for reconciliation; the node is rebuilt from the primary.");
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
                node.CustomerRows, node.TransactionRows, node.SizeMb, node.LastError, node.LastCheckUtc,
                node.Partitioned, node.HoldsLease, node.LeaseRenewedUtc)).ToList(),
            cluster.Epoch, cluster.WaitingForLease, cluster.BankRowsShipped, cluster.BankLastShippedUtc, cluster.BankVersion,
            cluster.BankRebuilds, cluster.DivergedRows)).ToList();
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
