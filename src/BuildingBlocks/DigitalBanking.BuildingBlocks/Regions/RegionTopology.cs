using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.BuildingBlocks.Regions;

// Which database is each province's primary right now.
// The admin's cluster coordinator writes ops.RegionPrimaries in the central database on every failover;
// every service polls it once a second, so traffic follows a promoted standby without a restart.
// Until the coordinator has written the table, node A is the primary.
public sealed class RegionTopology : IDisposable
{
    private readonly string centralConnectionString;
    private readonly ConcurrentDictionary<string, (string Database, long Epoch)> primaries = new();
    private readonly Timer timer;
    private int refreshing;

    public RegionTopology(string centralConnectionString)
    {
        this.centralConnectionString = centralConnectionString;
        timer = new Timer(_ => RefreshNow(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public string PrimaryDatabase(string region)
    {
        region = BankRegions.Normalize(region);
        return primaries.TryGetValue(region, out var primary) ? primary.Database : BankRegions.DatabaseName(region);
    }

    public long Epoch(string region) => primaries.TryGetValue(BankRegions.Normalize(region), out var primary) ? primary.Epoch : 0;

    // Admin-switchable: wait for the standby before acknowledging a money movement (RPO 0) or not (faster).
    public bool SynchronousCommit { get; private set; } = true;

    // Called on a failed or fenced write as well, so the next request already sees a new primary.
    public void RefreshNow()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1)
        {
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await using var connection = new SqlConnection(centralConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                IF OBJECT_ID(N'ops.RegionPrimaries') IS NOT NULL SELECT Region, PrimaryDatabase, Epoch FROM ops.RegionPrimaries;
                IF OBJECT_ID(N'ops.ReplicationSettings') IS NOT NULL SELECT SynchronousCommit FROM ops.ReplicationSettings WHERE Id = 1;
                """, connection) { CommandTimeout = 3 };
            await using var reader = await command.ExecuteReaderAsync();
            // Both tables are created together by the coordinator, so either both result sets come back or neither.
            if (reader.FieldCount == 3)
            {
                while (await reader.ReadAsync())
                {
                    primaries[BankRegions.Normalize(reader.GetString(0))] = (reader.GetString(1), reader.GetInt64(2));
                }

                if (await reader.NextResultAsync() && await reader.ReadAsync())
                {
                    SynchronousCommit = reader.GetBoolean(0);
                }
            }
        }
        catch (SqlException)
        {
            // Central database unreachable: keep routing to the last known primaries.
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    public void Dispose() => timer.Dispose();
}
