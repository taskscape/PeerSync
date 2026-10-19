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
        int minimum = parameters.Min;
        int average = parameters.Average;
        int maximum = parameters.Max;
        ulong smallMask = parameters.SmallMask, largeMask = parameters.LargeMask;
        if (minimum <= 0 || average < minimum || maximum < average)
            throw new ArgumentException("Invalid chunk parameters.", nameof(parameters));
        int length = Math.Min(buffer.Length, maximum);
        if (length <= minimum) return length;
        int normal = Math.Min(average, length);
        // A fixed 256-element span lets the JIT prove byte-indexed gear accesses
        // are in range. Slice iteration also removes repeated input bounds checks.
        ReadOnlySpan<ulong> gear = Gear.AsSpan(0, 256);
        ulong fingerprint = 0;
        int position = minimum;
        foreach (byte value in buffer[minimum..normal])
        {
            fingerprint = unchecked((fingerprint << 1) + gear[value]);
            position++;
            if ((fingerprint & smallMask) == 0) return position;
        }
        foreach (byte value in buffer[normal..length])
        {
            fingerprint = unchecked((fingerprint << 1) + gear[value]);
            position++;
            if ((fingerprint & largeMask) == 0) return position;
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
