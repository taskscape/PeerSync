using System.Buffers.Binary;

namespace PeerSync.Core.Sync;

public enum VvOrder { Equal, Dominates, DominatedBy, Concurrent }

/// <summary>Missing entries are zero; canonical vectors contain only positive counters.</summary>
public static class VersionVector
{
    public static VvOrder Compare(IReadOnlyDictionary<ulong, ulong> a, IReadOnlyDictionary<ulong, ulong> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        bool aAhead = false, bAhead = false;
        foreach (var (device, counter) in a)
        {
            ulong other = b.GetValueOrDefault(device);
            aAhead |= counter > other;
            bAhead |= counter < other;
        }
        foreach (var (device, counter) in b)
            if (counter != 0 && !a.ContainsKey(device)) bAhead = true;
        return (aAhead, bAhead) switch
        {
            (false, false) => VvOrder.Equal,
            (true, false) => VvOrder.Dominates,
            (false, true) => VvOrder.DominatedBy,
            _ => VvOrder.Concurrent,
        };
    }

    public static Dictionary<ulong, ulong> Merge(IReadOnlyDictionary<ulong, ulong> a, IReadOnlyDictionary<ulong, ulong> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var merged = Normalize(a);
        foreach (var (device, counter) in b)
            if (counter > merged.GetValueOrDefault(device)) merged[device] = counter;
        return merged;
    }

    public static Dictionary<ulong, ulong> Increment(IReadOnlyDictionary<ulong, ulong> vector, ulong self)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ulong next = checked(vector.GetValueOrDefault(self) + 1);
        var result = Normalize(vector);
        result[self] = next;
        return result;
    }

    /// <summary>Canonical storage encoding, independent of dictionary enumeration order.</summary>
    public static byte[] Serialize(IReadOnlyDictionary<ulong, ulong> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var entries = vector.Where(e => e.Value != 0).OrderBy(e => e.Key).ToArray();
        int length = VarintSize((ulong)entries.Length);
        foreach (var (_, counter) in entries) length = checked(length + 8 + VarintSize(counter));
        byte[] bytes = new byte[length];
        int position = 0;
        WriteVarint(bytes, ref position, (ulong)entries.Length);
        foreach (var (device, counter) in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(position), device);
            position += 8;
            WriteVarint(bytes, ref position, counter);
        }
        return bytes;
    }

    public static Dictionary<ulong, ulong> Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (!TryDeserialize(bytes, out var vector)) throw new FormatException("Invalid version vector.");
        return vector!;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out Dictionary<ulong, ulong>? vector)
    {
        vector = null;
        int position = 0;
        if (!ReadVarint(bytes, ref position, out ulong count) || count > (ulong)((bytes.Length - position) / 9)) return false;
        var result = new Dictionary<ulong, ulong>((int)count);
        ulong previous = 0;
        for (ulong index = 0; index < count; index++)
        {
            if (bytes.Length - position < 8) return false;
            ulong device = BinaryPrimitives.ReadUInt64LittleEndian(bytes[position..]);
            position += 8;
            if (index != 0 && device <= previous) return false;
            if (!ReadVarint(bytes, ref position, out ulong counter) || counter == 0) return false;
            result.Add(device, counter);
            previous = device;
        }
        if (position != bytes.Length) return false;
        vector = result;
        return true;
    }

    private static Dictionary<ulong, ulong> Normalize(IReadOnlyDictionary<ulong, ulong> vector) =>
        vector.Where(e => e.Value != 0).ToDictionary(e => e.Key, e => e.Value);

    private static int VarintSize(ulong value)
    {
        int count = 1;
        while ((value >>= 7) != 0) count++;
        return count;
    }

    private static void WriteVarint(Span<byte> output, ref int position, ulong value)
    {
        while (value >= 128)
        {
            output[position++] = (byte)((value & 127) | 128);
            value >>= 7;
        }
        output[position++] = (byte)value;
    }

    private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int position, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (position == bytes.Length) return false;
            byte current = bytes[position++];
            if (shift == 63 && current > 1) return false;
            value |= (ulong)(current & 127) << shift;
            if ((current & 128) == 0) return shift == 0 || current != 0;
        }
        return false;
    }
}
