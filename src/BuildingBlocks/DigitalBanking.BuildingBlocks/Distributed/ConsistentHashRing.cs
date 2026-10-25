using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Extensions.Configuration;

namespace DigitalBanking.BuildingBlocks.Distributed;

// Consistent hashing: every shard owns many points ("virtual nodes") on a 32-bit ring, and a key belongs to the
// first point clockwise from its own hash. Adding a shard takes over only the arcs in front of its new points, so
// about 1/(N+1) of the keys move, instead of almost all of them with "hash mod N".
public sealed class ConsistentHashRing
{
    private readonly uint[] points;
    private readonly int[] owners;

    public ConsistentHashRing(string name, int shards, int virtualNodes)
    {
        if (shards < 1) throw new ArgumentOutOfRangeException(nameof(shards));
        Name = name;
        Shards = shards;
        VirtualNodes = virtualNodes;
        var ring = new List<(uint Point, int Shard)>(shards * virtualNodes);
        for (var shard = 0; shard < shards; shard++)
        {
            for (var replica = 0; replica < virtualNodes; replica++)
            {
                ring.Add((MurmurHash3.Hash($"{name}/s{shard}#v{replica}"), shard));
            }
        }

        ring.Sort((left, right) => left.Point.CompareTo(right.Point));
        points = ring.Select(entry => entry.Point).ToArray();
        owners = ring.Select(entry => entry.Shard).ToArray();
    }

    public string Name { get; }
    public int Shards { get; }
    public int VirtualNodes { get; }

    public int Locate(string key) => Locate(MurmurHash3.Hash(key));

    public int Locate(uint hash)
    {
        var index = Array.BinarySearch(points, hash);
        if (index < 0)
        {
            index = ~index;
        }

        // Past the last point: wrap around to the first one.
        return owners[index == points.Length ? 0 : index];
    }

    // Share of the 2^32 ring each shard owns; with enough virtual nodes these stay close to 1/N.
    public double[] Ownership()
    {
        var share = new double[Shards];
        for (var index = 0; index < points.Length; index++)
        {
            var previous = index == 0 ? points[^1] : points[index - 1];
            share[owners[index]] += unchecked(points[index] - previous) / 4294967296d;
        }

        return share;
    }
}

// The shard of an account inside its province: "punjab:s2". The province is the range partition (data residency);
// the ring spreads a busy province's accounts over its shards, keyed by the account id.
public sealed class ProvinceShardMap
{
    private readonly Dictionary<string, ConsistentHashRing> rings;

    public ProvinceShardMap(IConfiguration configuration)
        : this(configuration.GetValue("Sharding:ShardsPerProvince", 4), configuration.GetValue("Sharding:VirtualNodes", 128))
    {
    }

    public ProvinceShardMap(int shardsPerProvince, int virtualNodes)
    {
        ShardsPerProvince = shardsPerProvince;
        VirtualNodes = virtualNodes;
        rings = BankRegions.All.ToDictionary(region => region, region => new ConsistentHashRing(region, shardsPerProvince, virtualNodes));
    }

    public int ShardsPerProvince { get; }
    public int VirtualNodes { get; }

    public ConsistentHashRing Ring(string region) => rings[BankRegions.Normalize(region)];

    public int ShardOf(string region, Guid accountId) => Ring(region).Locate(accountId.ToString("N"));

    public string ShardKey(string region, Guid accountId) => Format(region, ShardOf(region, accountId));

    public static string Format(string region, int shard) => $"{BankRegions.Normalize(region)}:s{shard}";
}
