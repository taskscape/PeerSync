using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

// Kind 8 is a local native-root journal, never a wire message or completion proof.
internal sealed class DirectoryCaseRootState
{
    internal required byte[] Root { get; init; }
    internal required VersionView Source { get; init; }
    internal required byte[] SourceId { get; init; }
    internal required string Temporary { get; init; }
    internal required byte[] TemporaryId { get; init; }
    internal int Collision { get; init; } = 1;
    internal bool Prepared { get; set; }
    internal uint TemporaryAttributes { get; set; }
    internal bool InstalledInThisCall { get; set; }
    internal string Destination => ConflictNames.CaseSourcePath(Source.Path, SourceId, Collision);
    internal static bool IsRoot(ApplyIntent intent) => intent.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(intent.Payload) == 8;
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(8); Bytes(Root); Bytes(Source.SourceDevice); Bytes(Source.Record.Body); Bytes(Source.Record.Signature); Bytes(SourceId);
        writer.Write(Collision); Bytes(Utf8.GetBytes(Temporary)); Bytes(TemporaryId); writer.Write(Prepared); writer.Write(TemporaryAttributes); writer.Flush();
        if (stream.Length > ApplyIntent.MaxPayloadBytes) throw new InvalidDataException("Directory case root state exceeds its bound.");
        return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    }
    internal static DirectoryCaseRootState Decode(ApplyIntent intent, FolderKeys keys)
    {
        using var stream = new MemoryStream(intent.Payload.ToArray(), writable: false); using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadInt32() != 8) throw new InvalidDataException("Unsupported directory case root kind.");
        byte[] root = Bytes(24), device = Bytes(32), body = Bytes(SignedRecord.MaxBodyBytes), signature = Bytes(64), id = Bytes(32);
        int collision = reader.ReadInt32(); if (collision is < 1 or > 1000) throw new InvalidDataException("Invalid directory case root collision.");
        var source = VersionView.Open(new(body, signature), keys, device);
        string temporary = Utf8.GetString(Bytes(4096 * 4)); byte[] temporaryId = Bytes(16); byte prepared = reader.ReadByte(); uint attributes = reader.ReadUInt32();
        if (prepared > 1) throw new InvalidDataException("Invalid directory case root preparation flag.");
        var state = new DirectoryCaseRootState { Root = root, Source = source, SourceId = id, Collision = collision,
            Temporary = temporary, TemporaryId = temporaryId, Prepared = prepared == 1, TemporaryAttributes = attributes };
        if (stream.Position != stream.Length || root.Length != 24 || id.Length != 32 || state.TemporaryId.Length != 16
            || source.Type != 1 || source.Deleted || !CaseConflictPolicy.SourceId(source).AsSpan().SequenceEqual(id)
            || intent.PathKey != PathRules.Key(state.Destination) || intent.ExpectedLocalSequence is not null
            || ApplyState.Parent(state.Temporary) != ApplyState.Parent(state.Destination)
            || !ApplyState.Name(state.Temporary).EndsWith(".!psp", StringComparison.Ordinal)
            || !Guid.TryParseExact(ApplyState.Name(state.Temporary)[..^5], "N", out _)
            || state.Prepared && ((state.TemporaryAttributes & (uint)FileAttributes.Directory) == 0 || (state.TemporaryAttributes & (uint)FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Invalid directory case root ownership.");
        _ = DirectorySnapshotBlocks.SnapshotOf(source); return state;
        byte[] Bytes(int maximum)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximum || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid directory case root field.");
            return reader.ReadBytes(length);
        }
    }
    public override string ToString() => "Directory case root state (redacted)";
    private static readonly UTF8Encoding Utf8 = new(false, true);
}
