using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PeerSync.Crypto.Chunking;

/// <summary>PSP/1 normalized FastCDC. The gear derivation and size classes are wire constants.</summary>
public static class FastCdc
{
    private static readonly ulong[] Gear = BuildGear();

    /// <summary>Call with at least Max bytes, except for the final window at EOF.</summary>
    public static int NextCut(ReadOnlySpan<byte> buffer, in ChunkParameters parameters)
    {
        if (parameters.Min <= 0 || parameters.Average < parameters.Min || parameters.Max < parameters.Average)
            throw new ArgumentException("Invalid chunk parameters.", nameof(parameters));
        int length = Math.Min(buffer.Length, parameters.Max);
        if (length <= parameters.Min) return length;
        int normal = Math.Min(parameters.Average, length);
        ulong fingerprint = 0;
        int i = parameters.Min;
        for (; i < normal; i++)
        {
            fingerprint = unchecked((fingerprint << 1) + Gear[buffer[i]]);
            if ((fingerprint & parameters.SmallMask) == 0) return i + 1;
        }
        for (; i < length; i++)
        {
            fingerprint = unchecked((fingerprint << 1) + Gear[buffer[i]]);
            if ((fingerprint & parameters.LargeMask) == 0) return i + 1;
        }
        return length;
    }

    private static ulong[] BuildGear()
    {
        var table = new ulong[256];
        Span<byte> input = stackalloc byte[16];
        Span<byte> digest = stackalloc byte[32];
        for (int i = 0; i < table.Length; i++)
        {
            input.Clear();
            "psp1 gear"u8.CopyTo(input);
            input[15] = (byte)i;
            SHA256.HashData(input, digest);
            table[i] = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        }
        return table;
    }
}
