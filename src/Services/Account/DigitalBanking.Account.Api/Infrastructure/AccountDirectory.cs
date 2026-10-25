using System.Collections.Concurrent;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace DigitalBanking.Account.Api.Infrastructure;

// Global index of every account in the bank (central database): account number and id -> province and shard.
// Lookups go straight to the right province database instead of asking all four, and the primary key on
// the account number keeps numbers unique bank-wide (a number is reserved here before the account is saved).
// Reads try, in order: this process's memory, the Redis global index (GlobalAccountIndex), then this table.
public sealed class AccountDirectory(RegionalDatabaseOptions options, ProvinceShardMap shards, IConnectionMultiplexer redis, ILogger<AccountDirectory> logger)
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
            -- The account's hash shard inside its province ("punjab:s2"), from the consistent-hash ring.
            IF COL_LENGTH(N'directory.Accounts', N'Shard') IS NULL
                ALTER TABLE directory.Accounts ADD Shard nvarchar(40) NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string ShardOf(string region, Guid accountId) => shards.ShardKey(region, accountId);

    // False when the number is already taken anywhere in the bank.
    public async Task<bool> TryReserveAsync(string accountNumber, Guid accountId, string region, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "INSERT directory.Accounts (AccountNumber, AccountId, Region, Shard, CreatedAtUtc) VALUES (@number, @id, @region, @shard, SYSUTCDATETIME())", connection);
        command.Parameters.AddWithValue("@number", accountNumber);
        command.Parameters.AddWithValue("@id", accountId);
        command.Parameters.AddWithValue("@region", BankRegions.Normalize(region));
        command.Parameters.AddWithValue("@shard", ShardOf(region, accountId));
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
        LookupAsync(regionById, accountId, GlobalAccountIndex.ById, "AccountId", cancellationToken);

    public Task<string?> RegionOfAsync(string accountNumber, CancellationToken cancellationToken) =>
        LookupAsync(regionByNumber, accountNumber, GlobalAccountIndex.ByNumber, "AccountNumber", cancellationToken);

    // Indexes accounts that existed before the directory (or were saved while it was unreachable).
    public async Task<int> BackfillAsync(string region, IEnumerable<(string AccountNumber, Guid AccountId)> accounts, CancellationToken cancellationToken)
    {
        var added = 0;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var (accountNumber, accountId) in accounts)
        {
            await using var command = new SqlCommand("""
                IF NOT EXISTS (SELECT 1 FROM directory.Accounts WHERE AccountNumber = @number OR AccountId = @id)
                    INSERT directory.Accounts (AccountNumber, AccountId, Region, Shard, CreatedAtUtc) VALUES (@number, @id, @region, @shard, SYSUTCDATETIME());
                """, connection);
            command.Parameters.AddWithValue("@number", accountNumber);
            command.Parameters.AddWithValue("@id", accountId);
            command.Parameters.AddWithValue("@region", BankRegions.Normalize(region));
            command.Parameters.AddWithValue("@shard", ShardOf(region, accountId));
            added += Math.Max(0, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        return added;
    }

    private async Task<string?> LookupAsync<TKey>(ConcurrentDictionary<TKey, string> cache, TKey key, string redisHash, string column, CancellationToken cancellationToken)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var field = key.ToString()!;
        if (await RedisGetAsync(redisHash, field) is { } indexed)
        {
            return cache[key] = GlobalAccountIndex.RegionOf(indexed);
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new SqlCommand($"SELECT Region, AccountNumber, AccountId, Shard FROM directory.Accounts WHERE {column} = @key", connection);
            command.Parameters.AddWithValue("@key", key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var region = BankRegions.Normalize(reader.GetString(0));
                var accountId = reader.GetGuid(2);
                var value = GlobalAccountIndex.Value(region, reader.IsDBNull(3) ? ShardOf(region, accountId) : reader.GetString(3));
                // Read repair: the next lookup (from any service instance) is answered by Redis.
                await RedisSetAsync(reader.GetString(1), accountId, value);
                return cache[key] = region;
            }
        }
        catch (SqlException)
        {
            // Directory unreachable: the caller falls back to asking every province.
        }

        return null;
    }

    // Redis is an accelerator, never a dependency: when it is down a lookup simply goes to SQL.
    private async Task<string?> RedisGetAsync(string hash, string field)
    {
        if (!redis.IsConnected)
        {
            return null;
        }

        try
        {
            var value = await redis.GetDatabase().HashGetAsync(hash, field);
            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (RedisException exception)
        {
            logger.LogDebug("Global index read failed: {Message}", exception.Message);
            return null;
        }
    }

    private async Task RedisSetAsync(string accountNumber, Guid accountId, string value)
    {
        if (!redis.IsConnected)
        {
            return;
        }

        try
        {
            var database = redis.GetDatabase();
            await Task.WhenAll(
                database.HashSetAsync(GlobalAccountIndex.ByNumber, accountNumber, value),
                database.HashSetAsync(GlobalAccountIndex.ById, accountId.ToString(), value));
        }
        catch (RedisException exception)
        {
            logger.LogDebug("Global index repair failed: {Message}", exception.Message);
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.CentralConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
