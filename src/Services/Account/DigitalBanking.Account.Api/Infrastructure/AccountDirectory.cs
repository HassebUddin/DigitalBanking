using System.Collections.Concurrent;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Account.Api.Infrastructure;

// Global index of every account in the bank (central database): account number and id -> province.
// Lookups go straight to the right province database instead of asking all four, and the primary key on
// the account number keeps numbers unique bank-wide (a number is reserved here before the account is saved).
public sealed class AccountDirectory(RegionalDatabaseOptions options)
{
    // Accounts never move between provinces, so a found entry can be cached for good.
    private readonly ConcurrentDictionary<string, string> regionByNumber = new();
    private readonly ConcurrentDictionary<Guid, string> regionById = new();

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            IF SCHEMA_ID(N'directory') IS NULL EXEC(N'CREATE SCHEMA directory');
            IF OBJECT_ID(N'directory.Accounts') IS NULL
            BEGIN
                CREATE TABLE directory.Accounts (
                    AccountNumber nvarchar(20) NOT NULL CONSTRAINT PK_DirectoryAccounts PRIMARY KEY,
                    AccountId uniqueidentifier NOT NULL,
                    Region nvarchar(32) NOT NULL,
                    CreatedAtUtc datetime2 NOT NULL);
                CREATE UNIQUE INDEX IX_DirectoryAccounts_AccountId ON directory.Accounts (AccountId);
            END
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // False when the number is already taken anywhere in the bank.
    public async Task<bool> TryReserveAsync(string accountNumber, Guid accountId, string region, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "INSERT directory.Accounts (AccountNumber, AccountId, Region, CreatedAtUtc) VALUES (@number, @id, @region, SYSUTCDATETIME())", connection);
        command.Parameters.AddWithValue("@number", accountNumber);
        command.Parameters.AddWithValue("@id", accountId);
        command.Parameters.AddWithValue("@region", BankRegions.Normalize(region));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2627 or 2601)
        {
            return false;
        }

        regionByNumber[accountNumber] = regionById[accountId] = BankRegions.Normalize(region);
        return true;
    }

    public Task<string?> RegionOfAsync(Guid accountId, CancellationToken cancellationToken) =>
        LookupAsync(regionById, accountId, "AccountId", cancellationToken);

    public Task<string?> RegionOfAsync(string accountNumber, CancellationToken cancellationToken) =>
        LookupAsync(regionByNumber, accountNumber, "AccountNumber", cancellationToken);

    // Indexes accounts that existed before the directory (or were saved while it was unreachable).
    public async Task<int> BackfillAsync(string region, IEnumerable<(string AccountNumber, Guid AccountId)> accounts, CancellationToken cancellationToken)
    {
        var added = 0;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var (accountNumber, accountId) in accounts)
        {
            await using var command = new SqlCommand("""
                IF NOT EXISTS (SELECT 1 FROM directory.Accounts WHERE AccountNumber = @number OR AccountId = @id)
                    INSERT directory.Accounts (AccountNumber, AccountId, Region, CreatedAtUtc) VALUES (@number, @id, @region, SYSUTCDATETIME());
                """, connection);
            command.Parameters.AddWithValue("@number", accountNumber);
            command.Parameters.AddWithValue("@id", accountId);
            command.Parameters.AddWithValue("@region", BankRegions.Normalize(region));
            added += Math.Max(0, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        return added;
    }

    private async Task<string?> LookupAsync<TKey>(ConcurrentDictionary<TKey, string> cache, TKey key, string column, CancellationToken cancellationToken)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new SqlCommand($"SELECT Region FROM directory.Accounts WHERE {column} = @key", connection);
            command.Parameters.AddWithValue("@key", key);
            if (await command.ExecuteScalarAsync(cancellationToken) is string region)
            {
                return cache[key] = BankRegions.Normalize(region);
            }
        }
        catch (SqlException)
        {
            // Directory unreachable: the caller falls back to asking every province.
        }

        return null;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
