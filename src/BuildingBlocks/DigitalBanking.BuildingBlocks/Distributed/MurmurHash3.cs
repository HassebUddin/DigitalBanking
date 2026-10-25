using System.Buffers.Binary;
using System.Text;

namespace DigitalBanking.BuildingBlocks.Distributed;

// MurmurHash3 (x86, 32-bit): fast, non-cryptographic, with a good avalanche, so nearby keys (sequential account
// ids, CNICs that differ in one digit) land far apart. Used by the shard ring and the Bloom filters.
public static class MurmurHash3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash(string text, uint seed = 0) => Hash(Encoding.UTF8.GetBytes(text), seed);

    public static uint Hash(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var hash = seed;
        var blocks = data.Length / 4;
        for (var index = 0; index < blocks; index++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(index * 4, 4));
            k *= C1;
            k = uint.RotateLeft(k, 15);
            k *= C2;
            hash ^= k;
            hash = uint.RotateLeft(hash, 13);
            hash = hash * 5 + 0xe6546b64;
        }

        var tail = data[(blocks * 4)..];
        uint last = 0;
        switch (tail.Length)
        {
            case 3:
                last ^= (uint)tail[2] << 16;
                goto case 2;
            case 2:
                last ^= (uint)tail[1] << 8;
                goto case 1;
            case 1:
                last ^= tail[0];
                last *= C1;
                last = uint.RotateLeft(last, 15);
                last *= C2;
                hash ^= last;
                break;
        }

        hash ^= (uint)data.Length;
        hash ^= hash >> 16;
        hash *= 0x85ebca6b;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35;
        hash ^= hash >> 16;
        return hash;
    }
}
