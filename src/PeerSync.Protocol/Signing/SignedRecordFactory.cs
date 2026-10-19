using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Protocol.Records;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Protocol.Signing;

public static class SignedRecordFactory
{
    public static Envelope Create(FileRecordBody body, FolderKeys keys, ulong localSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.CanWrite) throw new InvalidOperationException("A read-write key is required to create records.");
        // Clone before sorting: the caller's parsed view remains untouched.
        var canonical = body.Clone();
        var entries = canonical.Vv.OrderBy(e => e.Device).ToArray();
        canonical.Vv.Clear();
        canonical.Vv.AddRange(entries);
        if (!IsValid(canonical) || canonical.CalculateSize() > Envelope.MaxBodyBytes)
            throw new ArgumentException("Invalid file record.", nameof(body));
        byte[] bytes = canonical.ToByteArray();
        return new Envelope(bytes, keys.Sign(SigningInput(keys.FolderId(), bytes)), localSequence);
    }

    public static bool TryOpen(Envelope record, FolderKeys keys, [NotNullWhen(true)] out FileRecordBody? body)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(keys);
        body = null;
        if (!keys.Verify(SigningInput(keys.FolderId(), record.Body), record.Signature)) return false;
        try
        {
            var parsed = FileRecordBody.Parser.ParseFrom(record.Body);
            if (!IsValid(parsed)) return false;
            body = parsed;
            return true;
        }
        catch (InvalidProtocolBufferException) { return false; }
    }

    public static Records.SignedRecord ToWire(Envelope record) => new()
    {
        Body = ByteString.CopyFrom(record.Body),
        Signature = ByteString.CopyFrom(record.Signature),
        LocalSeq = record.LocalSequence,
    };

    public static Envelope FromWire(Records.SignedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(record.Body.Span, record.Signature.Span, record.LocalSeq);
    }

    // Broader type/hash/size/request limits are enforced by P2 message validation.
    private static bool IsValid(FileRecordBody body)
    {
        if (!PathRules.IsValid(body.Path)) return false;
        bool snapshot = body.DirectorySnapshotHash.Length != 0 || body.DirectorySnapshotLength != 0;
        if (snapshot && (body.Type != 1 || body.Deleted || body.DirectorySnapshotHash.Length != 32
            || body.DirectorySnapshotLength < Directories.DirectorySnapshotNode.HeaderBytes
            || body.DirectorySnapshotLength > Directories.DirectorySnapshotNode.MaximumLinksNodeBytes
            || (body.DirectorySnapshotLength - Directories.DirectorySnapshotNode.HeaderBytes) % Directories.DirectorySnapshotNode.LinkBytes != 0)) return false;
        ulong previous = 0;
        for (int i = 0; i < body.Vv.Count; i++)
        {
            var entry = body.Vv[i];
            if (entry.Counter == 0 || (i != 0 && entry.Device <= previous)) return false;
            previous = entry.Device;
        }
        return true;
    }

    private static byte[] SigningInput(ReadOnlySpan<byte> folderId, ReadOnlySpan<byte> body) =>
        [.. "psp1 record"u8, .. folderId, .. body];
}
