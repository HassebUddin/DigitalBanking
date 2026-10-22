using System.Diagnostics;
using System.Text.RegularExpressions;
using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops.Data;

public sealed record TableInfo(string Name, long Rows, double SizeMb);

public sealed record DatabaseInfo(
    string Key, string Database, string Kind, string Region, string Role, string Status, string? Error, IReadOnlyList<TableInfo> Tables);

public sealed record ColumnInfo(string Name, string Type, bool Nullable, int? MaxLength, bool IsKey);

public sealed record IndexInfo(string Name, string Columns, bool Unique, bool Clustered);

public sealed record TablePage(
    string Database, string Table, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<object?[]> Rows,
    long TotalRows, int Page, int Size, double TookMs, string ServedBy);

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, double TookMs);

/// <summary>Read-only browser over the control database and every regional node, for admins without SQL tools.</summary>
public sealed partial class DataExplorer(IConfiguration configuration, RegionalDataPlane dataPlane)
{
    private const int QueryRowLimit = 1_000;

    private IEnumerable<(string Key, string Database, string Kind, string Region, string Role, string Status, string ConnectionString)> Catalog()
    {
        var control = new SqlConnectionStringBuilder(configuration.GetConnectionString("Admin"));
        yield return ("control", control.InitialCatalog, "control", "All regions", "control plane", "online", control.ConnectionString);
        foreach (var node in dataPlane.DatabaseCatalog())
        {
            yield return (node.Key, node.Label, "regional", node.Region, node.Role, node.Status, node.ConnectionString);
        }
    }

    public async Task<IReadOnlyList<DatabaseInfo>> DatabasesAsync(CancellationToken cancellationToken)
    {
        var tasks = Catalog().Select(async entry =>
        {
            if (entry.Status == "down")
            {
                return new DatabaseInfo(entry.Key, entry.Database, entry.Kind, entry.Region, entry.Role, entry.Status, "Node is offline", []);
            }

            try
            {
                await using var connection = new SqlConnection(entry.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("""
                    SELECT t.name,
                           ISNULL(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END), 0),
                           CAST(ISNULL(SUM(p.reserved_page_count), 0) * 8 / 1024.0 AS float)
                    FROM sys.tables t
                    LEFT JOIN sys.dm_db_partition_stats p ON p.object_id = t.object_id
                    WHERE t.is_ms_shipped = 0
                    GROUP BY t.name
                    ORDER BY t.name
                    """, connection) { CommandTimeout = 10 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var tables = new List<TableInfo>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    tables.Add(new TableInfo(reader.GetString(0), reader.GetInt64(1), Math.Round(reader.GetDouble(2), 2)));
                }

                return new DatabaseInfo(entry.Key, entry.Database, entry.Kind, entry.Region, entry.Role, entry.Status, null, tables);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new DatabaseInfo(entry.Key, entry.Database, entry.Kind, entry.Region, entry.Role, entry.Status, exception.Message, []);
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<TablePage> TableAsync(
        string key, string table, int page, int size, string? sort, bool descending, string? column, string? value, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var entry = Resolve(key);
        size = Math.Clamp(size, 10, 200);
        page = Math.Max(0, page);

        await using var connection = await OpenAsync(entry, cancellationToken);
        var described = await ColumnsAsync(connection, table, cancellationToken);
        if (described.Count == 0)
        {
            throw new NotFoundException($"Table '{table}' does not exist in {entry.Database}.");
        }

        // The table name comes back from sys.tables, so only real tables can reach the SQL below.
        var tableName = described[0].Table;
        var columns = described.Select(item => item.Column).ToList();

        var indexes = await IndexesAsync(connection, table, cancellationToken);
        var sortColumn = columns.FirstOrDefault(item => string.Equals(item.Name, sort, StringComparison.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(item => item.IsKey) ?? columns[0];
        if (string.IsNullOrWhiteSpace(sort))
        {
            descending = true;
        }

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        var where = string.Empty;
        if (!string.IsNullOrWhiteSpace(column) && !string.IsNullOrWhiteSpace(value))
        {
            var filterColumn = columns.FirstOrDefault(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase))
                ?? throw new ValidationException($"Unknown column '{column}'.");
            if (IsNumeric(filterColumn.Type) && decimal.TryParse(value, out var number))
            {
                where = $"WHERE [{filterColumn.Name}] = @value";
                command.Parameters.AddWithValue("@value", number);
            }
            else
            {
                where = $"WHERE CAST([{filterColumn.Name}] AS nvarchar(400)) LIKE @value";
                command.Parameters.AddWithValue("@value", value.Trim() + "%");
            }
        }

        var quoted = $"[dbo].[{tableName.Replace("]", "]]")}]";
        command.CommandText =
            $"SELECT * FROM {quoted} {where} ORDER BY [{sortColumn.Name}] {(descending ? "DESC" : "ASC")} OFFSET {page * size} ROWS FETCH NEXT {size} ROWS ONLY;" +
            (where.Length == 0
                ? $" SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(N'{quoted}') AND index_id IN (0, 1);"
                : $" SELECT COUNT_BIG(*) FROM (SELECT TOP (100000) 1 AS x FROM {quoted} {where}) counted;");

        var rows = new List<object?[]>(size);
        long total;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadRow(reader));
            }

            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            total = Convert.ToInt64(reader.GetValue(0));
        }

        return new TablePage(entry.Database, tableName, columns, indexes, rows, total, page, size,
            Math.Round(watch.Elapsed.TotalMilliseconds, 1), entry.Key);
    }

    public async Task<QueryResult> QueryAsync(string key, string sql, CancellationToken cancellationToken)
    {
        var text = (sql ?? string.Empty).Trim().TrimEnd(';').Trim();
        if (!ReadOnlyStart().IsMatch(text) || text.Contains(';') || Forbidden().IsMatch(text))
        {
            throw new ValidationException("Only a single read-only SELECT (or WITH … SELECT) statement is allowed here.");
        }

        var watch = Stopwatch.StartNew();
        var entry = Resolve(key);
        await using var connection = await OpenAsync(entry, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new SqlCommand(text, connection, transaction) { CommandTimeout = 15 };
        var rows = new List<object?[]>();
        var truncated = false;
        List<string> columns;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= QueryRowLimit)
                {
                    truncated = true;
                    break;
                }

                rows.Add(ReadRow(reader));
            }
        }

        // Nothing should have changed, but never commit anything run from the console.
        await transaction.RollbackAsync(cancellationToken);
        return new QueryResult(columns, rows, truncated, Math.Round(watch.Elapsed.TotalMilliseconds, 1));
    }

    private (string Key, string Database, string Kind, string Region, string Role, string Status, string ConnectionString) Resolve(string key)
    {
        return Catalog().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found
            ? found
            : throw new NotFoundException($"Database '{key}' is not in the catalog.");
    }

    private static async Task<SqlConnection> OpenAsync(
        (string Key, string Database, string Kind, string Region, string Role, string Status, string ConnectionString) entry, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(entry.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync();
            throw new BusinessRuleException($"{entry.Key} is not reachable: {exception.Message}");
        }
    }

    private static async Task<List<(string Table, ColumnInfo Column)>> ColumnsAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT t.name, c.name, ty.name, c.is_nullable, c.max_length,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.name = @table AND SCHEMA_NAME(t.schema_id) = 'dbo'
            ORDER BY c.column_id
            """, connection);
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<(string, ColumnInfo)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(2);
            var bytes = reader.GetInt16(4);
            // max_length is in bytes (-1 for MAX); unicode types use two bytes per character.
            int? length = !type.Contains("char") ? null : bytes < 0 ? -1 : type.StartsWith('n') ? bytes / 2 : bytes;
            columns.Add((reader.GetString(0), new ColumnInfo(reader.GetString(1), type, reader.GetBoolean(3), length, reader.GetInt32(5) == 1)));
        }

        return columns;
    }

    private static async Task<List<IndexInfo>> IndexesAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT i.name, STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal), i.is_unique, CASE WHEN i.type = 1 THEN 1 ELSE 0 END
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table)) AND i.name IS NOT NULL
            GROUP BY i.name, i.is_unique, i.type
            ORDER BY i.type, i.name
            """, connection);
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var indexes = new List<IndexInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new IndexInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3) == 1));
        }

        return indexes;
    }

    private static object?[] ReadRow(SqlDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index) switch
            {
                byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32))) + (bytes.Length > 32 ? "…" : string.Empty),
                DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc),
                Guid guid => guid.ToString(),
                TimeSpan span => span.ToString(),
                var other => other
            };
        }

        return values;
    }

    private static bool IsNumeric(string type) => type is "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "float" or "real" or "money" or "bit";

    [GeneratedRegex(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadOnlyStart();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|BACKUP|RESTORE|SHUTDOWN|DBCC|INTO|OPENROWSET|OPENQUERY|OPENDATASOURCE|WAITFOR|KILL|RECONFIGURE)\b|\bxp_|\bsp_", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
