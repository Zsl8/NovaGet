using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NovaGet.TestServer;

/// <summary>
/// Deterministic pseudo-random content: any byte range can be produced without storing the file,
/// so tests can serve hundreds of megabytes and still verify SHA-256 hashes.
/// </summary>
public static class ContentGenerator
{
    public static void Fill(int seed, long offset, Span<byte> destination)
    {
        var position = offset;
        var index = 0;
        Span<byte> bytes = stackalloc byte[8];
        while (index < destination.Length)
        {
            var block = position >> 3;
            var value = SplitMix64(((ulong)(uint)seed << 40) ^ (ulong)block);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            var within = (int)(position & 7);
            var take = Math.Min(8 - within, destination.Length - index);
            bytes.Slice(within, take).CopyTo(destination[index..]);
            index += take;
            position += take;
        }
    }

    public static byte[] Generate(int seed, long length)
    {
        var data = new byte[length];
        Fill(seed, 0, data);
        return data;
    }

    public static string Sha256Hex(int seed, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Fill(seed, offset, buffer.AsSpan(0, count));
            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
