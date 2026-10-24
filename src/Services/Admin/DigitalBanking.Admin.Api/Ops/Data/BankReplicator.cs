using System.Collections.Concurrent;
using System.Data;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops.Data;

public sealed record BankTable(string Schema, string Name, int ObjectId, IReadOnlyList<string> Columns, IReadOnlyList<string> Keys, string? IdentityColumn)
{
    public string FullName => $"[{Schema}].[{Name}]";
    public string KeyJoin(string left, string right) => string.Join(" AND ", Keys.Select(key => $"{left}.[{key}] = {right}.[{key}]"));
    public string ColumnList(string? alias = null) => string.Join(", ", Columns.Select(column => alias is null ? $"[{column}]" : $"{alias}.[{column}]"));
}

public sealed record ShipResult(long RowsApplied, bool Rebuilt, long SourceVersion);

public sealed record DivergedRow(string Table, string Operation, string? RowJson);

/// <summary>
/// Real primary-to-standby replication of the banking tables (account, txn, customer, notify, audit schemas)
/// using SQL Server Change Tracking, plus the primary lease and the fencing triggers that make a database
/// refuse banking writes unless it currently holds the lease. Together these give one writable primary per
/// province at any time (no split brain), even when the coordinator and a node lose sight of each other.
/// </summary>
public sealed class BankReplicator
{
    // A primary keeps accepting writes this long after its last lease renewal. Failover waits this long
    // after losing a primary, so the old primary has fenced itself before the standby is promoted.
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(10);

    // dbo holds the admin's synthetic load (replicated by id watermark); ops holds this coordinator's own tables.
    private const string BankTableFilter = "s.name NOT IN (N'dbo', N'ops', N'sys') AND t.is_ms_shipped = 0";

    /// <summary>Lease and replication tables, change tracking and fencing triggers. Idempotent; re-run to pick up new tables.</summary>
    public async Task PrepareAsync(SqlConnection connection, string nodeName, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops');
            IF OBJECT_ID(N'ops.Lease') IS NULL
                CREATE TABLE ops.Lease (
                    Id int NOT NULL CONSTRAINT PK_Lease PRIMARY KEY CONSTRAINT CK_Lease_Single CHECK (Id = 1),
                    Node nvarchar(64) NOT NULL, Role nvarchar(16) NOT NULL, Epoch bigint NOT NULL,
                    LeaseUntilUtc datetime2 NULL, UpdatedAtUtc datetime2 NOT NULL);
            IF NOT EXISTS (SELECT 1 FROM ops.Lease) INSERT ops.Lease VALUES (1, @node, N'replica', 0, NULL, SYSUTCDATETIME());
            IF OBJECT_ID(N'ops.ReplicationState') IS NULL
                CREATE TABLE ops.ReplicationState (
                    Id int NOT NULL CONSTRAINT PK_ReplicationState PRIMARY KEY CONSTRAINT CK_ReplicationState_Single CHECK (Id = 1),
                    SourceDatabase nvarchar(128) NOT NULL, LastVersion bigint NOT NULL, UpdatedAtUtc datetime2 NOT NULL);
            """, cancellationToken, ("@node", nodeName));

        // Snapshot isolation lets a ship pass read every table and the change-tracking version at one point in time.
        await ExecuteAsync(connection, """
            IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
                ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
            IF (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()) = 0
                ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
            """, cancellationToken);

        var pending = new List<(string Schema, string Table, bool Tracked, bool Fenced)>();
        await using (var command = new SqlCommand($"""
            SELECT s.name, t.name,
                   CASE WHEN ct.object_id IS NULL THEN 0 ELSE 1 END,
                   CASE WHEN tr.object_id IS NULL THEN 0 ELSE 1 END
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.change_tracking_tables ct ON ct.object_id = t.object_id
            LEFT JOIN sys.triggers tr ON tr.parent_id = t.object_id AND tr.name = N'fence_' + t.name
            WHERE {BankTableFilter} AND EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = t.object_id AND i.is_primary_key = 1)
            """, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1, reader.GetInt32(3) == 1));
            }
        }

        foreach (var (schema, table, tracked, fenced) in pending)
        {
            if (!tracked)
            {
                await ExecuteAsync(connection, $"ALTER TABLE [{schema}].[{table}] ENABLE CHANGE_TRACKING", cancellationToken);
            }

            if (!fenced)
            {
                // The coordinator's own replication session sets SESSION_CONTEXT 'replication' and passes through.
                await ExecuteAsync(connection, $"""
                    CREATE TRIGGER [{schema}].[fence_{table}] ON [{schema}].[{table}] AFTER INSERT, UPDATE, DELETE AS
                    BEGIN
                        SET NOCOUNT ON;
                        IF CAST(SESSION_CONTEXT(N'replication') AS int) = 1 RETURN;
                        IF NOT EXISTS (SELECT 1 FROM ops.Lease WHERE Role = N'primary' AND LeaseUntilUtc > SYSUTCDATETIME())
                            THROW {RegionErrors.Fenced}, N'Fenced: this database is not the primary of its region (no valid lease).', 1;
                    END
                    """, cancellationToken);
            }
        }
    }

    /// <summary>Grants or extends the primary lease. Refused (false) if the node already saw a newer epoch.</summary>
    public async Task<bool> RenewPrimaryAsync(SqlConnection connection, string nodeName, long epoch, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(connection, """
            UPDATE ops.Lease
            SET Node = @node, Role = N'primary', Epoch = @epoch,
                LeaseUntilUtc = DATEADD(millisecond, @ttl, SYSUTCDATETIME()), UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = 1 AND Epoch <= @epoch
            """, cancellationToken, ("@node", nodeName), ("@epoch", epoch), ("@ttl", (int)LeaseTtl.TotalMilliseconds)) == 1;
    }

    /// <summary>
    /// A node becoming primary stops following anyone: its replication watermark is dropped, so if it is ever
    /// demoted again it is rebuilt from the new primary instead of resuming from a stale position.
    /// </summary>
    public async Task ForgetReplicationStateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "IF OBJECT_ID(N'ops.ReplicationState') IS NOT NULL DELETE FROM ops.ReplicationState", cancellationToken);
    }

    /// <summary>Takes the lease away at once: from now on the node refuses banking writes.</summary>
    public async Task DemoteAsync(SqlConnection connection, string nodeName, long epoch, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            UPDATE ops.Lease SET Node = @node, Role = N'replica', Epoch = @epoch, LeaseUntilUtc = NULL, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = 1 AND (Role <> N'replica' OR Epoch <> @epoch OR LeaseUntilUtc IS NOT NULL)
            """, cancellationToken, ("@node", nodeName), ("@epoch", epoch));
    }

    /// <summary>
    /// Ships every banking change the standby has not applied yet, in one transaction (all or nothing), and
    /// records the source version it reached. A standby that followed a different primary, or fell behind the
    /// change-tracking retention, is rebuilt from a full copy instead.
    /// </summary>
    public async Task<ShipResult> ShipAsync(string sourceConnectionString, string sourceDatabase, string targetConnectionString, CancellationToken cancellationToken)
    {
        try
        {
            return await ShipOnceAsync(sourceConnectionString, sourceDatabase, targetConnectionString, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // The standby holds rows the primary's history does not explain (it diverged): start it over from a full copy.
            await using (var target = new SqlConnection(targetConnectionString))
            {
                await target.OpenAsync(cancellationToken);
                await ForgetReplicationStateAsync(target, cancellationToken);
            }

            return await ShipOnceAsync(sourceConnectionString, sourceDatabase, targetConnectionString, cancellationToken);
        }
    }

    private async Task<ShipResult> ShipOnceAsync(string sourceConnectionString, string sourceDatabase, string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var source = new SqlConnection(sourceConnectionString);
        await using var target = new SqlConnection(targetConnectionString);
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);

        // Every read of this pass (the change-tracking version and the rows of all tables) sees one point in time,
        // so an account's balance never arrives without the ledger legs committed with it (or vice versa).
        // Read-only: closing the connection ends it.
        var version = await ScalarLongAsync(source,
            "SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRANSACTION; SELECT ISNULL(CHANGE_TRACKING_CURRENT_VERSION(), 0);", cancellationToken);

        var state = await ReplicationStateAsync(target, cancellationToken);
        var lastVersion = state?.LastVersion ?? -1;
        var sameSource = state is not null && string.Equals(state.Value.SourceDatabase, sourceDatabase, StringComparison.OrdinalIgnoreCase);
        if (sameSource && version == lastVersion)
        {
            // Nothing committed since the last pass: the common case, kept to three tiny queries.
            return new ShipResult(0, false, version);
        }

        var ordered = await PlanAsync(source, target, sourceDatabase, targetConnectionString, cancellationToken);
        var needsRebuild = !sameSource || await TooOldAsync(source, ordered, lastVersion, cancellationToken);
        if (!needsRebuild)
        {
            ordered = await ChangedTablesAsync(source, ordered, lastVersion, cancellationToken);
        }

        await using var transaction = (SqlTransaction)await target.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(target, "EXEC sp_set_session_context @key = N'replication', @value = 1", cancellationToken, transaction);

        long applied = 0;
        if (needsRebuild)
        {
            foreach (var table in Enumerable.Reverse(ordered))
            {
                await ExecuteAsync(target, $"DELETE FROM {table.FullName}", cancellationToken, transaction);
            }

            foreach (var table in ordered)
            {
                await using var read = new SqlCommand($"SELECT {table.ColumnList()} FROM {table.FullName}", source) { CommandTimeout = 300 };
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                applied += await BulkCopyAsync(target, transaction, reader, table.FullName, table.Columns, cancellationToken);
            }
        }
        else
        {
            // Read every changed table first (one query each, inside the source snapshot), then apply.
            var changes = new List<(BankTable Table, List<object[]> Upserts, List<object[]> Deletes)>();
            foreach (var table in ordered)
            {
                var (upserts, deletes) = await ReadChangesAsync(source, table, lastVersion, cancellationToken);
                changes.Add((table, upserts, deletes));
            }

            for (var index = 0; index < changes.Count; index++)
            {
                applied += await ApplyUpsertsAsync(target, transaction, changes[index].Table, index, changes[index].Upserts, cancellationToken);
            }

            // Children before parents, so foreign keys hold while rows disappear.
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                applied += await ApplyDeletesAsync(target, transaction, changes[index].Table, changes[index].Deletes, cancellationToken);
            }
        }

        await ExecuteAsync(target, """
            MERGE ops.ReplicationState AS state
            USING (SELECT 1 AS Id) AS one ON state.Id = one.Id
            WHEN MATCHED THEN UPDATE SET SourceDatabase = @source, LastVersion = @version, UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Id, SourceDatabase, LastVersion, UpdatedAtUtc) VALUES (1, @source, @version, SYSUTCDATETIME());
            EXEC sp_set_session_context @key = N'replication', @value = NULL;
            """, cancellationToken, transaction, ("@source", sourceDatabase), ("@version", version));
        await transaction.CommitAsync(cancellationToken);
        return new ShipResult(applied, needsRebuild, version);
    }

    /// <summary>
    /// Banking changes a former primary accepted after the last version shipped from it: writes the new primary
    /// never received. They are returned (as JSON rows) so they can be kept for reconciliation before a rebuild.
    /// </summary>
    public async Task<List<DivergedRow>> UnshippedChangesAsync(string formerPrimaryConnectionString, long sinceVersion, CancellationToken cancellationToken)
    {
        var rows = new List<DivergedRow>();
        await using var connection = new SqlConnection(formerPrimaryConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in await TablesAsync(connection, cancellationToken))
        {
            await using var command = new SqlCommand($"""
                SELECT ct.SYS_CHANGE_OPERATION,
                       (SELECT {table.ColumnList("x")} FROM {table.FullName} x WHERE {table.KeyJoin("x", "ct")} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
                FROM CHANGETABLE(CHANGES {table.FullName}, @since) AS ct
                """, connection) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@since", sinceVersion);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new DivergedRow(table.FullName, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }
            catch (SqlException)
            {
                // Table not tracked on this node yet: nothing could have been tracked there either.
            }
        }

        return rows;
    }

    public async Task<(string SourceDatabase, long LastVersion)?> ReplicationStateAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReplicationStateAsync(connection, cancellationToken);
    }

    private static async Task<(string SourceDatabase, long LastVersion)?> ReplicationStateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "IF OBJECT_ID(N'ops.ReplicationState') IS NOT NULL SELECT SourceDatabase, LastVersion FROM ops.ReplicationState WHERE Id = 1", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    // Changed rows as they are now (upserts, values in table.Columns order) and keys of rows that no longer exist (deletes).
    private static async Task<(List<object[]> Upserts, List<object[]> Deletes)> ReadChangesAsync(SqlConnection source, BankTable table, long since, CancellationToken cancellationToken)
    {
        var upserts = new List<object[]>();
        var deletes = new List<object[]>();
        var keys = string.Join(", ", table.Keys.Select(key => $"ct.[{key}]"));
        await using var read = new SqlCommand(
            $"SELECT CASE WHEN t.[{table.Keys[0]}] IS NULL THEN 1 ELSE 0 END, {keys}, {table.ColumnList("t")} " +
            $"FROM CHANGETABLE(CHANGES {table.FullName}, @since) AS ct LEFT JOIN {table.FullName} AS t ON {table.KeyJoin("t", "ct")}",
            source) { CommandTimeout = 300 };
        read.Parameters.AddWithValue("@since", since);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt32(0) == 1)
            {
                deletes.Add(Enumerable.Range(1, table.Keys.Count).Select(reader.GetValue).ToArray());
            }
            else
            {
                upserts.Add(Enumerable.Range(1 + table.Keys.Count, table.Columns.Count).Select(reader.GetValue).ToArray());
            }
        }

        return (upserts, deletes);
    }

    // Small batches go as one parameterised statement batch; large ones through a bulk-copied staging table.
    private static async Task<long> ApplyUpsertsAsync(SqlConnection target, SqlTransaction transaction, BankTable table, int index, List<object[]> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var settable = table.Columns.Where(column => !table.Keys.Contains(column, StringComparer.OrdinalIgnoreCase) && column != table.IdentityColumn).ToList();
        var identityOn = table.IdentityColumn is null ? string.Empty : $"SET IDENTITY_INSERT {table.FullName} ON;";
        var identityOff = table.IdentityColumn is null ? string.Empty : $"SET IDENTITY_INSERT {table.FullName} OFF;";
        var rowsPerBatch = Math.Max(1, 2000 / table.Columns.Count);
        if (rows.Count <= rowsPerBatch * 4)
        {
            foreach (var chunk in rows.Chunk(rowsPerBatch))
            {
                await using var command = new SqlCommand { Connection = target, Transaction = transaction, CommandTimeout = 300 };
                var sql = new System.Text.StringBuilder(identityOn);
                for (var row = 0; row < chunk.Length; row++)
                {
                    string Param(string column) => $"@r{row}_{table.Columns.ToList().IndexOf(column)}";
                    for (var column = 0; column < table.Columns.Count; column++)
                    {
                        AddValue(command, $"@r{row}_{column}", chunk[row][column]);
                    }

                    var where = string.Join(" AND ", table.Keys.Select(key => $"[{key}] = {Param(key)}"));
                    if (settable.Count > 0)
                    {
                        sql.Append($"UPDATE {table.FullName} SET {string.Join(", ", settable.Select(column => $"[{column}] = {Param(column)}"))} WHERE {where};");
                        sql.Append($"IF @@ROWCOUNT = 0 ");
                    }
                    else
                    {
                        sql.Append($"IF NOT EXISTS (SELECT 1 FROM {table.FullName} WHERE {where}) ");
                    }

                    sql.Append($"INSERT INTO {table.FullName} ({table.ColumnList()}) VALUES ({string.Join(", ", table.Columns.Select(Param))});");
                }

                command.CommandText = sql.Append(identityOff).ToString();
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return rows.Count;
        }

        var stage = $"#up{index}";
        await ExecuteAsync(target, $"SELECT TOP (0) {table.ColumnList()} INTO {stage} FROM {table.FullName}", cancellationToken, transaction);
        var data = new DataTable();
        await using (var schema = new SqlCommand($"SELECT TOP (0) {table.ColumnList()} FROM {stage}", target, transaction))
        await using (var reader = await schema.ExecuteReaderAsync(cancellationToken))
        {
            data.Load(reader);
        }

        foreach (var row in rows)
        {
            data.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
        }

        using (var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, transaction) { DestinationTableName = stage, BulkCopyTimeout = 300 })
        {
            await bulk.WriteToServerAsync(data, cancellationToken);
        }

        var update = settable.Count == 0 ? string.Empty
            : $"UPDATE t SET {string.Join(", ", settable.Select(column => $"t.[{column}] = u.[{column}]"))} FROM {table.FullName} t JOIN {stage} u ON {table.KeyJoin("t", "u")};";
        var insert = $"INSERT INTO {table.FullName} ({table.ColumnList()}) SELECT {table.ColumnList("u")} FROM {stage} u WHERE NOT EXISTS (SELECT 1 FROM {table.FullName} t WHERE {table.KeyJoin("t", "u")});";
        await ExecuteAsync(target, $"{update} {identityOn} {insert} {identityOff} DROP TABLE {stage};", cancellationToken, transaction);
        return rows.Count;
    }

    // datetime2 keeps full precision (AddWithValue would send DateTime as the older, coarser datetime type).
    private static void AddValue(SqlCommand command, string name, object? value)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (value is DateTime)
        {
            parameter.SqlDbType = SqlDbType.DateTime2;
        }
    }

    private static async Task<long> ApplyDeletesAsync(SqlConnection target, SqlTransaction transaction, BankTable table, List<object[]> keys, CancellationToken cancellationToken)
    {
        long deleted = 0;
        foreach (var chunk in keys.Chunk(Math.Max(1, 2000 / table.Keys.Count)))
        {
            await using var command = new SqlCommand { Connection = target, Transaction = transaction, CommandTimeout = 300 };
            var rows = new List<string>();
            for (var row = 0; row < chunk.Length; row++)
            {
                rows.Add("(" + string.Join(" AND ", table.Keys.Select((key, k) => $"[{key}] = @d{row}_{k}")) + ")");
                for (var k = 0; k < table.Keys.Count; k++)
                {
                    AddValue(command, $"@d{row}_{k}", chunk[row][k]);
                }
            }

            command.CommandText = $"DELETE FROM {table.FullName} WHERE {string.Join(" OR ", rows)}";
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return deleted;
    }

    private static async Task<long> BulkCopyAsync(SqlConnection target, SqlTransaction transaction, IDataReader reader, string destination, IEnumerable<string> columns, CancellationToken cancellationToken)
    {
        long rows = 0;
        using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 300,
            NotifyAfter = 1
        };
        bulk.SqlRowsCopied += (_, args) => rows = args.RowsCopied;
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        await bulk.WriteToServerAsync((System.Data.Common.DbDataReader)reader, cancellationToken);
        return rows;
    }

    /// <summary>Banking tables with a primary key: copyable columns (no computed or rowversion), keys and identity column.</summary>
    private static async Task<List<BankTable>> TablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<(string, string), (int ObjectId, List<string> Columns, List<string> Keys, string? Identity)>();
        await using var command = new SqlCommand($"""
            SELECT s.name, t.name, t.object_id, c.name, c.is_identity,
                   CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.indexes pk ON pk.object_id = t.object_id AND pk.is_primary_key = 1
            LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = pk.index_id AND ic.column_id = c.column_id
            WHERE {BankTableFilter} AND c.is_computed = 0 AND TYPE_NAME(c.system_type_id) NOT IN (N'timestamp')
            ORDER BY s.name, t.name, c.column_id
            """, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!columns.TryGetValue(key, out var table))
            {
                table = (reader.GetInt32(2), [], [], null);
            }

            var column = reader.GetString(3);
            table.Columns.Add(column);
            if (reader.GetInt32(5) == 1) table.Keys.Add(column);
            if (reader.GetBoolean(4)) table.Identity = column;
            columns[key] = table;
        }

        return columns.Select(pair => new BankTable(pair.Key.Item1, pair.Key.Item2, pair.Value.ObjectId, pair.Value.Columns, pair.Value.Keys, pair.Value.Identity)).ToList();
    }

    // Parents before children (by foreign keys inside the database), so inserts satisfy constraints.
    private static async Task<List<BankTable>> ParentsFirstAsync(SqlConnection connection, List<BankTable> tables, CancellationToken cancellationToken)
    {
        var byId = tables.ToDictionary(table => table.ObjectId);
        var parents = tables.ToDictionary(table => table.ObjectId, _ => new HashSet<int>());
        await using (var command = new SqlCommand("SELECT parent_object_id, referenced_object_id FROM sys.foreign_keys", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var (child, parent) = (reader.GetInt32(0), reader.GetInt32(1));
                if (child != parent && parents.ContainsKey(child) && byId.ContainsKey(parent))
                {
                    parents[child].Add(parent);
                }
            }
        }

        var ordered = new List<BankTable>();
        var done = new HashSet<int>();
        while (ordered.Count < tables.Count)
        {
            var ready = tables.Where(table => !done.Contains(table.ObjectId) && parents[table.ObjectId].All(done.Contains)).ToList();
            if (ready.Count == 0)
            {
                ready = tables.Where(table => !done.Contains(table.ObjectId)).ToList();
            }

            foreach (var table in ready)
            {
                done.Add(table.ObjectId);
                ordered.Add(table);
            }
        }

        return ordered;
    }

    // Table list and insert order per source/target pair, re-read every 10 s (services may add tables).
    private readonly ConcurrentDictionary<string, (DateTime ReadAtUtc, List<BankTable> Ordered)> plans = new();

    private async Task<List<BankTable>> PlanAsync(SqlConnection source, SqlConnection target, string sourceDatabase, string targetConnectionString, CancellationToken cancellationToken)
    {
        var key = $"{sourceDatabase}|{targetConnectionString}";
        if (plans.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.ReadAtUtc < TimeSpan.FromSeconds(10))
        {
            return cached.Ordered;
        }

        var targetTables = (await TablesAsync(target, cancellationToken)).ToDictionary(table => table.FullName, StringComparer.OrdinalIgnoreCase);
        var tables = (await TablesAsync(source, cancellationToken))
            .Where(table => targetTables.ContainsKey(table.FullName))
            .Select(table => table with { Columns = table.Columns.Intersect(targetTables[table.FullName].Columns, StringComparer.OrdinalIgnoreCase).ToList() })
            .ToList();
        var ordered = await ParentsFirstAsync(source, tables, cancellationToken);
        plans[key] = (DateTime.UtcNow, ordered);
        return ordered;
    }

    // True when some table cannot be shipped incrementally from lastVersion (untracked, or history cleaned up).
    private static async Task<bool> TooOldAsync(SqlConnection source, List<BankTable> tables, long lastVersion, CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return false;
        }

        var ids = string.Join(", ", tables.Select(table => $"({table.ObjectId})"));
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM (VALUES {ids}) AS t(id) WHERE ISNULL(CHANGE_TRACKING_MIN_VALID_VERSION(t.id), 9223372036854775807) > @last", source);
        command.Parameters.AddWithValue("@last", lastVersion);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    // Only the tables with changes since lastVersion, in the same (parents-first) order. One query.
    private static async Task<List<BankTable>> ChangedTablesAsync(SqlConnection source, List<BankTable> tables, long lastVersion, CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return tables;
        }

        var probe = string.Join(" UNION ALL ", tables.Select(table =>
            $"SELECT {table.ObjectId} WHERE EXISTS (SELECT 1 FROM CHANGETABLE(CHANGES {table.FullName}, @since) AS ct)"));
        await using var command = new SqlCommand(probe, source) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@since", lastVersion);
        var changed = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changed.Add(reader.GetInt32(0));
        }

        return tables.Where(table => changed.Contains(table.ObjectId)).ToList();
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Task<int> ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        ExecuteAsync(connection, sql, cancellationToken, null, parameters);

    private static async Task<int> ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, SqlTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
