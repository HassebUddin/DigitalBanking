using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace DigitalBanking.BuildingBlocks.Regions;

// Several services share one database (each in its own schema), so EnsureCreated is not enough:
// it does nothing once another service has created the database.
public static class DatabaseBootstrap
{
    // Creates the database if needed, then this context's tables if its schema has none yet.
    public static async Task EnsureTablesAsync(DbContext dbContext, CancellationToken cancellationToken = default)
    {
        var creator = dbContext.GetService<IRelationalDatabaseCreator>();
        for (var attempt = 0; attempt < 5 && !await creator.ExistsAsync(cancellationToken); attempt++)
        {
            try
            {
                await creator.CreateAsync(cancellationToken);
            }
            catch (SqlException)
            {
                // Another service created it at the same moment; check again.
                await Task.Delay(500, cancellationToken);
            }
        }

        // Master -> slave replication reads under snapshot isolation; a database created here (rather than by
        // the admin's data plane) must allow it too.
        await dbContext.Database.ExecuteSqlRawAsync(
            "IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()) = 0 ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;",
            cancellationToken);

        var (schema, table) = Tables(dbContext).First();
        if (!await TableExistsAsync(dbContext, schema, table, cancellationToken))
        {
            await creator.CreateTablesAsync(cancellationToken);
        }
    }

    // Copies rows from the old one-database-per-service setup (e.g. DigitalBanking_Account) into this
    // context's tables, once: only tables that are still empty are filled.
    public static async Task ImportLegacyAsync(DbContext dbContext, string legacyDatabase, CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.databases WHERE name = @p0", cancellationToken, legacyDatabase) == 0)
            {
                return;
            }

            // A one-time migration of existing data, not a live write: it passes the fencing triggers like replication
            // does, so it cannot be skipped just because the coordinator had not granted a lease yet.
            await ScalarAsync(connection, "EXEC sp_set_session_context @key = N'replication', @value = 1; SELECT 0", cancellationToken);

            foreach (var (schema, table) in TablesInInsertOrder(dbContext))
            {
                var target = $"[{schema}].[{table}]";
                if (await ScalarAsync(connection, $"SELECT COUNT(*) FROM {target}", cancellationToken) > 0)
                {
                    continue;
                }

                var legacyColumns = await ColumnsAsync(connection,
                    $"SELECT c.name FROM [{legacyDatabase}].sys.columns c JOIN [{legacyDatabase}].sys.tables t ON t.object_id = c.object_id " +
                    $"WHERE t.name = @p0 AND SCHEMA_NAME(t.schema_id) = 'dbo' AND c.is_computed = 0 AND TYPE_NAME(c.system_type_id) <> 'timestamp'",
                    cancellationToken, table);
                if (legacyColumns.Count == 0)
                {
                    continue;
                }

                var targetColumns = await ColumnsAsync(connection,
                    "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @p0 AND TABLE_NAME = @p1",
                    cancellationToken, schema, table);
                var columns = string.Join(", ", targetColumns.Intersect(legacyColumns, StringComparer.OrdinalIgnoreCase).Select(column => $"[{column}]"));
                if (columns.Length == 0)
                {
                    continue;
                }

                var hasIdentity = await ScalarAsync(connection, $"SELECT CAST(OBJECTPROPERTY(OBJECT_ID(N'{target}'), 'TableHasIdentity') AS int)", cancellationToken) == 1;
                var insert = $"INSERT INTO {target} ({columns}) SELECT {columns} FROM [{legacyDatabase}].[dbo].[{table}];";
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 300;
                command.CommandText = hasIdentity ? $"SET IDENTITY_INSERT {target} ON; {insert} SET IDENTITY_INSERT {target} OFF;" : insert;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await ScalarAsync(connection, "EXEC sp_set_session_context @key = N'replication', @value = NULL; SELECT 0", CancellationToken.None);
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static IEnumerable<(string Schema, string Table)> Tables(DbContext dbContext)
    {
        return dbContext.Model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .Select(entity => (entity.GetSchema() ?? "dbo", entity.GetTableName()!))
            .Distinct();
    }

    // Parents before children, so foreign keys are satisfied while copying.
    private static IEnumerable<(string Schema, string Table)> TablesInInsertOrder(DbContext dbContext)
    {
        var pending = dbContext.Model.GetEntityTypes().Where(entity => entity.GetTableName() is not null).ToList();
        var done = new HashSet<IEntityType>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(entity => entity.GetForeignKeys().All(key => key.PrincipalEntityType == entity || done.Contains(key.PrincipalEntityType))).ToList();
            if (ready.Count == 0)
            {
                ready = pending;
            }

            foreach (var entity in ready)
            {
                done.Add(entity);
                pending.Remove(entity);
                yield return (entity.GetSchema() ?? "dbo", entity.GetTableName()!);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(DbContext dbContext, string schema, string table, CancellationToken cancellationToken)
    {
        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = {table}")
            .SingleAsync(cancellationToken);
        return count > 0;
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<List<string>> ColumnsAsync(DbConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static void AddParameters(DbCommand command, object[] parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{index}";
            parameter.Value = parameters[index];
            command.Parameters.Add(parameter);
        }
    }
}
