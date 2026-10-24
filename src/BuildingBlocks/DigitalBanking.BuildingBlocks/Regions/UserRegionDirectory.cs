using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.BuildingBlocks.Regions;

public interface IUserRegionDirectory
{
    Task<string> GetRegionAsync(Guid userId, CancellationToken cancellationToken);
}

// Answers "which province does this user bank in?" from the users table in the central database.
// Staff, admin and unknown users get the head office region.
public sealed class UserRegionDirectory(RegionalDatabaseOptions options) : IUserRegionDirectory
{
    private readonly ConcurrentDictionary<Guid, string> cache = new();

    public async Task<string> GetRegionAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BankRegions.Default;
        }

        if (cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        // A failed lookup must never fall back to the head office: that silently put Sindh/KP customers'
        // data into Punjab. Retry, and if the central database stays unreachable, fail the request instead.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(options.CentralConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("SELECT Region FROM [identity].[Users] WHERE Id = @id", connection);
                command.Parameters.AddWithValue("@id", userId);
                var region = await command.ExecuteScalarAsync(cancellationToken) as string;

                // Unknown user (e.g. a system actor): head office. A user's province never changes, so remember it.
                return region is null ? BankRegions.Default : cache[userId] = BankRegions.Normalize(region);
            }
            catch (Exception exception) when (exception is SqlException or InvalidOperationException && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
            catch (Exception exception) when (exception is SqlException or InvalidOperationException)
            {
                throw new Exceptions.ServiceUnavailableException("Could not look up the customer's province right now. Please retry in a few seconds.");
            }
        }
    }
}
