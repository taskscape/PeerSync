using System.Buffers.Binary;
using PeerSync.Core.Sync;

namespace PeerSync.Protocol.Directories;

/// <summary>Exact original bytes only. Consumers must authenticate the envelope
/// and validate its directory membership before applying or retaining contents.</summary>
public static class DirectorySnapshotEnvelope
{
    public static byte[] Encode(SignedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.BodyLength == 0) throw new ArgumentException("Directory snapshot envelope body is empty.", nameof(record));
        byte[] bytes = new byte[record.BodyLength + 68]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)record.BodyLength);
        record.Body.CopyTo(bytes.AsSpan(4)); record.Signature.CopyTo(bytes.AsSpan(record.BodyLength + 4)); return bytes;
    }
    public static SignedRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 69 || bytes.Length > DirectorySnapshotNode.MaximumEnvelopeBytes) throw new InvalidDataException("Invalid directory snapshot envelope length.");
        uint bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (bodyLength is < 1 or > SignedRecord.MaxBodyBytes || bytes.Length != bodyLength + 68) throw new InvalidDataException("Invalid directory snapshot envelope body length.");
        return new(bytes.Slice(4, (int)bodyLength), bytes[^64..]);
    }
}
