using DigitalBanking.BuildingBlocks.Regions;

namespace DigitalBanking.BuildingBlocks.Distributed;

// Partition-aware global secondary index in Redis: account number (and account id) -> "province:shard".
// Written by the admin service from AccountCreatedEvent (transactional outbox, so an account that committed is
// always indexed eventually); read by the account service before the SQL directory, in well under a millisecond.
// The SQL table directory.Accounts stays the source of truth: a miss in Redis falls back to it and repairs the entry.
public static class GlobalAccountIndex
{
    public const string ByNumber = "global_index:accounts";
    public const string ById = "global_index:account-ids";

    public static string Value(string region, string shardKey) => shardKey.Contains(':') ? shardKey : $"{BankRegions.Normalize(region)}:{shardKey}";

    public static string RegionOf(string value) => BankRegions.Normalize(value.Split(':')[0]);
}
