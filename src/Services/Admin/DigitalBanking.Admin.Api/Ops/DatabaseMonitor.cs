using DigitalBanking.Admin.Api.Ops.Data;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record DatabaseSessions(string Database, int Sessions);

public sealed record DatabaseSnapshot(
    bool Available,
    string? Error,
    double BatchRequestsPerSecond,
    double TransactionsPerSecond,
    int UserSessions,
    int ActiveRequests,
    int WaitingTasks,
    double SizeMb,
    long CustomerRows,
    long TransactionRows,
    long LogRows,
    IReadOnlyList<DatabaseSessions> SessionsByDatabase,
    double[] BatchSeries,
    DateTime SampledAtUtc);

/// <summary>Samples SQL Server's own counters every two seconds so DB pressure is measured, not guessed.</summary>
public sealed class DatabaseMonitor(IConfiguration configuration, RegionalDataPlane dataPlane, ILogger<DatabaseMonitor> logger) : BackgroundService
{
    private const string Sql = """
        SELECT
            (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Batch Requests/sec'),
            (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Transactions/sec' AND instance_name = '_Total'),
            (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1),
            (SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.is_user_process = 1),
            (SELECT COUNT(*) FROM sys.dm_os_waiting_tasks w JOIN sys.dm_exec_sessions s ON s.session_id = w.session_id WHERE s.is_user_process = 1),
            (SELECT CAST(SUM(CAST(size AS bigint)) * 8 / 1024.0 AS float) FROM sys.database_files),
            CAST(0 AS bigint),
            CAST(0 AS bigint),
            (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('RequestLogs') AND index_id IN (0, 1)),
            (SELECT CAST(ISNULL(SUM(execution_count), 0) AS bigint) FROM sys.dm_exec_query_stats);
        SELECT DB_NAME(database_id), COUNT(*) FROM sys.dm_exec_sessions
        WHERE is_user_process = 1 AND database_id > 4 GROUP BY database_id ORDER BY COUNT(*) DESC;
        """;

    private readonly object sync = new();
    private readonly Queue<double> batchSeries = new();
    private DatabaseSnapshot latest = new(false, "Not sampled yet", 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], DateTime.UtcNow);
    private long? lastBatch;
    private long? lastTransactions;
    private DateTime lastSample;

    public DatabaseSnapshot Latest
    {
        get
        {
            lock (sync)
            {
                return latest;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Admin")!;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 10 };
                await using var reader = await command.ExecuteReaderAsync(stoppingToken);
                await reader.ReadAsync(stoppingToken);

                var now = DateTime.UtcNow;
                var batch = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                var transactions = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var seconds = (now - lastSample).TotalSeconds;
                double Rate(long? current, long? previous) =>
                    current is null || previous is null || seconds <= 0 || current < previous ? 0 : Math.Round((current.Value - previous.Value) / seconds, 1);

                // LocalDB hides the perf counters; statement executions from the plan cache are the fallback.
                var countersAvailable = batch is not null;
                batch ??= reader.GetInt64(9);
                var batchRate = Rate(batch, lastBatch);
                var transactionRate = Rate(transactions, lastTransactions);
                lastBatch = batch;
                lastTransactions = transactions;
                lastSample = now;

                var sessions = reader.GetInt32(2);
                var active = reader.GetInt32(3);
                var waiting = reader.GetInt32(4);
                var size = reader.IsDBNull(5) ? 0 : Math.Round(reader.GetDouble(5), 1);
                // Business rows live in the regional primaries; the control DB only holds logs and state.
                var primaries = dataPlane.Clusters.Select(cluster => cluster.Primary).Where(node => node is not null).ToList();
                var customers = primaries.Sum(node => node!.CustomerRows);
                var simRows = primaries.Sum(node => node!.TransactionRows);
                var logRows = reader.GetInt64(8);

                await reader.NextResultAsync(stoppingToken);
                var byDatabase = new List<DatabaseSessions>();
                while (await reader.ReadAsync(stoppingToken))
                {
                    byDatabase.Add(new DatabaseSessions(reader.IsDBNull(0) ? "(unknown)" : reader.GetString(0), reader.GetInt32(1)));
                }

                lock (sync)
                {
                    batchSeries.Enqueue(batchRate);
                    while (batchSeries.Count > 60)
                    {
                        batchSeries.Dequeue();
                    }

                    latest = new DatabaseSnapshot(true, countersAvailable ? null : "Statement executions/s (LocalDB has no perf counters)",
                        batchRate, transactionRate, sessions, active, waiting, size, customers, simRows, logRows, byDatabase,
                        batchSeries.ToArray(), now);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Database sampling failed.");
                lock (sync)
                {
                    latest = latest with { Available = false, Error = exception.Message, SampledAtUtc = DateTime.UtcNow };
                }
            }

            await Task.Delay(2_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
