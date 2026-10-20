using System.Buffers.Binary;
using PeerSync.Protocol.Directories;

namespace PeerSync.Engine.Hashing;

internal sealed partial class DirectorySnapshotBuilder
{
    internal const int MaximumCheckpointBytes = 50328;
    internal byte[] ExportCheckpoint()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        int length = 16 + levels.Count * 2 + (records.Count + levels.Sum(level => level.Count)) * 36;
        if (length > MaximumCheckpointBytes || levels.Count > 10 || records.Count >= 128 || levels.Any(level => level.Count >= 128))
            throw new InvalidDataException("Cannot checkpoint a failed directory snapshot addition.");
        byte[] bytes = new byte[length]; "PSDB"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = (byte)levels.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)records.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)accepted);
        int offset = 16;
        foreach (var level in levels) { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)level.Count); offset += 2; }
        foreach (var link in records) Write(link);
        foreach (var level in levels) foreach (var link in level) Write(link);
        // The same strict decoder validates count/carry consistency after a
        // partial async failure; only successful additions are resumable.
        _ = RestoreCheckpoint(static (_, _) => throw new InvalidOperationException("Checkpoint validation performs no writes."), bytes);
        return bytes;
        void Write(DirectorySnapshotLink link)
        {
            link.Hash.CopyTo(bytes, offset); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 32), (uint)link.EncodedLength); offset += 36;
        }
    }
    internal static DirectorySnapshotBuilder RestoreCheckpoint(Func<DirectorySnapshotNode, CancellationToken, Task<DirectorySnapshotLink>> write, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (bytes.Length is < 16 or > MaximumCheckpointBytes || !bytes[..4].SequenceEqual("PSDB"u8) || bytes[4] != 1 || bytes[5] > 10)
            throw new InvalidDataException("Invalid directory builder checkpoint header.");
        int levelCount = bytes[5], recordCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        if (count > long.MaxValue || recordCount >= 128 || bytes.Length < 16 + 2 * levelCount || (ulong)recordCount != count % 128)
            throw new InvalidDataException("Invalid directory builder checkpoint count.");
        int[] counts = new int[levelCount]; int total = recordCount; ulong remaining = count / 128; int offset = 16;
        for (int level = 0; level < levelCount; level++)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]); offset += 2;
            if (size >= 128 || (ulong)size != remaining % 128 || level == levelCount - 1 && size == 0)
                throw new InvalidDataException("Invalid directory builder checkpoint carry.");
            counts[level] = size; total += size; remaining /= 128;
        }
        if (remaining != 0 || bytes.Length != offset + total * 36) throw new InvalidDataException("Directory builder checkpoint is incomplete.");
        var builder = new DirectorySnapshotBuilder(write) { accepted = (long)count };
        for (int record = 0; record < recordCount; record++) builder.records.Add(Read(bytes, ref offset, record: true));
        foreach (int size in counts)
        {
            var links = new List<DirectorySnapshotLink>(128);
            for (int item = 0; item < size; item++) links.Add(Read(bytes, ref offset, record: false));
            builder.levels.Add(links);
        }
        return builder;
    }
    private static DirectorySnapshotLink Read(ReadOnlySpan<byte> bytes, ref int offset, bool record)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 32)..]);
        if (record ? length is < 48 or > 624 || (length - 12) % 36 != 0 : length != DirectorySnapshotNode.MaximumLinksNodeBytes)
            throw new InvalidDataException("Invalid directory builder checkpoint link.");
        var link = new DirectorySnapshotLink(bytes.Slice(offset, 32), (int)length); offset += 36; return link;
    }
}
