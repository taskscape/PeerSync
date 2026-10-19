using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

internal sealed class DirectoryState
{
    internal bool Prepared { get; set; }
    internal required byte[] Root { get; init; }
    internal required VersionView Version { get; init; }
    internal required IReadOnlyDictionary<ulong, ulong> Vector { get; init; }
    internal byte[] DirectoryId { get; set; } = [];

    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(3); writer.Write(Prepared); Bytes(Root); Bytes(Version.SourceDevice); Bytes(Version.Record.Body);
        Bytes(Version.Record.Signature); Bytes(VersionVector.Serialize(Vector)); Bytes(DirectoryId); writer.Flush();
        if (stream.Length > ApplyIntent.MaxPayloadBytes) throw new InvalidDataException("Directory intent exceeds limit.");
        return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    }

    internal static DirectoryState Decode(ApplyIntent intent, FolderKeys keys)
    {
        using var stream = new MemoryStream(intent.Payload.ToArray(), writable: false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 3) throw new InvalidDataException("Unsupported directory intent.");
        bool prepared = reader.ReadBoolean(); byte[] root = Bytes(24), source = Bytes(32), body = Bytes(SignedRecord.MaxBodyBytes), signature = Bytes(64);
        var vector = VersionVector.Deserialize(Bytes(ApplyIntent.MaxPayloadBytes)); byte[] id = Bytes(16);
        var version = VersionView.Open(new(body, signature), keys, source);
        if (stream.Position != stream.Length || root.Length != 24 || source.Length != 32 || id.Length is not (0 or 16)
            || version.Type != 1 || PathRules.Key(version.Path) != intent.PathKey
            || !version.Deleted && prepared && id.Length != 16
            || VersionVector.Compare(vector, version.Vector) is VvOrder.Concurrent or VvOrder.DominatedBy
            || keys.CanWrite && VersionVector.Compare(vector, version.Vector) != VvOrder.Equal)
            throw new InvalidDataException("Invalid directory recovery identity or version.");
        return new() { Prepared = prepared, Root = root, Version = version, Vector = vector, DirectoryId = id };
        byte[] Bytes(int maximum)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximum || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid directory field length.");
            return reader.ReadBytes(length);
        }
    }
    public override string ToString() => "Directory recovery state (redacted)";
}
