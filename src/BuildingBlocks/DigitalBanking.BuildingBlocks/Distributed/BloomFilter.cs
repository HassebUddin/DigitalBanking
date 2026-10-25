namespace DigitalBanking.BuildingBlocks.Distributed;

// A Bloom filter: a bit array that answers "definitely not present" or "probably present" for a set of keys,
// in a few bytes per key. Used as the reduced relation of a semi-join: each province summarises its CNICs in one,
// and a global search only queries the provinces whose filter says "probably".
//
// k bit positions per key come from two MurmurHash3 values (Kirsch-Mitzenmacher double hashing: h1 + i*h2).
public sealed class BloomFilter
{
    private readonly ulong[] bits;

    public BloomFilter(long expectedItems, double falsePositiveRate, long maxBits = 1L << 27)
    {
        expectedItems = Math.Max(1, expectedItems);
        // Optimal size m = -n ln p / (ln 2)^2 and hash count k = (m / n) ln 2.
        var size = (long)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
        BitCount = Math.Clamp(size, 1024, maxBits);
        HashCount = Math.Clamp((int)Math.Round(BitCount / (double)expectedItems * Math.Log(2)), 1, 16);
        bits = new ulong[(BitCount + 63) / 64];
    }

    public long BitCount { get; }
    public int HashCount { get; }
    public long Count { get; private set; }
    public long SizeBytes => bits.LongLength * 8;

    public void Add(string key)
    {
        var (first, second) = Hashes(key);
        for (var index = 0; index < HashCount; index++)
        {
            var bit = Position(first, second, index);
            bits[bit >> 6] |= 1UL << (int)(bit & 63);
        }

        Count++;
    }

    public bool MightContain(string key)
    {
        var (first, second) = Hashes(key);
        for (var index = 0; index < HashCount; index++)
        {
            var bit = Position(first, second, index);
            if ((bits[bit >> 6] & (1UL << (int)(bit & 63))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    // Expected false-positive rate at the current fill: (1 - e^(-kn/m))^k.
    public double EstimatedFalsePositiveRate => Math.Pow(1 - Math.Exp(-HashCount * (double)Count / BitCount), HashCount);

    private long Position(uint first, uint second, int index) => (long)((first + (ulong)index * second) % (ulong)BitCount);

    private static (uint, uint) Hashes(string key) => (MurmurHash3.Hash(key, 0), MurmurHash3.Hash(key, 0x9747b28c) | 1);
}
