using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DigitalBanking.BuildingBlocks.Regions;

// Synchronous commit for money movements. After a change commits on a province's primary, the caller waits
// until the standby has applied it (the coordinator ships committed changes every ~100 ms and records the
// primary's change-tracking version it reached in the standby's ops.ReplicationState). Only then is the
// movement acknowledged, so a failover can never lose a movement the bank already confirmed (RPO = 0).
//
// When it cannot be confirmed:
// - the standby itself is down: there is nothing to fail over to, so the movement is acknowledged
//   (like SQL Server Always On, a missing secondary degrades to asynchronous instead of stopping the bank);
// - the primary vanished right after the commit, or the standby does not catch up in time: nobody can tell
//   whether the change survives a failover, so with strict = true the caller gets "unavailable" and retries.
//   Movements are idempotent, so the retry applies it exactly once on whichever node is primary by then.
public sealed class StandbyAcknowledger(RegionalDatabaseOptions options, ILogger<StandbyAcknowledger> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<bool> WaitAsync(string region, bool strict, CancellationToken cancellationToken)
    {
        if (!options.Topology.SynchronousCommit)
        {
            // Asynchronous commit (switched by the admin): faster, but a failover can lose the last ~100 ms.
            return false;
        }

        var primary = options.Topology.PrimaryDatabase(region);
        var standby = BankRegions.StandbyOf(primary);
        long committed;
        try
        {
            await using var connection = new SqlConnection(options.ConnectionStringFor(primary));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT ISNULL(CHANGE_TRACKING_CURRENT_VERSION(), 0)", connection);
            committed = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqlException exception)
        {
            return Unconfirmed(strict, $"primary {primary} became unreachable after the commit ({exception.Message})");
        }

        SqlConnection standbyConnection;
        try
        {
            standbyConnection = new SqlConnection(options.ConnectionStringFor(standby));
            await standbyConnection.OpenAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Standby {Standby} is down; movement acknowledged without it: {Message}", standby, exception.Message);
            return false;
        }

        await using (standbyConnection)
        {
            await using var poll = new SqlCommand(
                "SELECT LastVersion FROM ops.ReplicationState WHERE Id = 1 AND SourceDatabase = @primary", standbyConnection);
            poll.Parameters.AddWithValue("@primary", primary);
            var deadline = DateTime.UtcNow + Timeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (await poll.ExecuteScalarAsync(cancellationToken) is long applied && applied >= committed)
                    {
                        return true;
                    }

                    await Task.Delay(40, cancellationToken);
                }
            }
            catch (SqlException exception)
            {
                logger.LogWarning("Standby {Standby} went down while waiting; movement acknowledged without it: {Message}", standby, exception.Message);
                return false;
            }
        }

        return Unconfirmed(strict, $"standby {standby} did not confirm within {Timeout.TotalSeconds:0}s");
    }

    private bool Unconfirmed(bool strict, string reason)
    {
        if (strict)
        {
            throw new ServiceUnavailableException($"The change could not be confirmed on the standby yet ({reason}). Please retry.");
        }

        logger.LogWarning("Not confirmed on the standby: {Reason}.", reason);
        return false;
    }
}
