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

        try
        {
            await using var connection = new SqlConnection(options.CentralConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT Region FROM [identity].[Users] WHERE Id = @id", connection);
            command.Parameters.AddWithValue("@id", userId);
            if (await command.ExecuteScalarAsync(cancellationToken) is string region)
            {
                // A user's province never changes, so it is safe to remember it.
                return cache[userId] = BankRegions.Normalize(region);
            }
        }
        catch (SqlException)
        {
        }

        return BankRegions.Default;
    }
}
