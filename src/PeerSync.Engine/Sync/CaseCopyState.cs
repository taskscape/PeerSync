using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

// Local recovery only. The original signed source path remains unchanged.
internal sealed class CaseCopyState
{
    internal required byte[] Root { get; init; }
    internal required VersionView Source { get; init; }
    internal required byte[] SourceId { get; init; }
    internal required string Temporary { get; init; }
    internal required byte[] TemporaryId { get; init; }
    internal int Collision { get; init; } = 1;
    internal bool Prepared { get; set; }
    internal bool InstalledInThisCall { get; set; }
    internal uint TemporaryAttributes { get; set; }
    internal string Destination => ConflictNames.CaseSourcePath(Source.Path, SourceId, Collision);
    internal static bool IsCase(ApplyIntent intent) => intent.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(intent.Payload) == 7;
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(7); Bytes(Root); Bytes(Source.SourceDevice); Bytes(Source.Record.Body); Bytes(Source.Record.Signature); Bytes(SourceId);
        writer.Write(Collision); Bytes(Utf8.GetBytes(Temporary)); Bytes(TemporaryId); writer.Write(Prepared); writer.Write(TemporaryAttributes); writer.Flush();
        if (stream.Length > ApplyIntent.MaxPayloadBytes) throw new InvalidDataException("Case recovery exceeds its bound.");
        return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> value) { writer.Write(value.Length); writer.Write(value); }
    }
    internal static CaseCopyState Decode(ApplyIntent intent, FolderKeys keys)
    {
        using var stream = new MemoryStream(intent.Payload.ToArray(), writable: false); using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadInt32() != 7) throw new InvalidDataException("Unsupported case recovery kind.");
        byte[] root = Bytes(24), device = Bytes(32), body = Bytes(SignedRecord.MaxBodyBytes), signature = Bytes(64), id = Bytes(32);
        int collision = reader.ReadInt32();
        if (collision is < 1 or > 1000) throw new InvalidDataException("Invalid case collision bound.");
        var state = new CaseCopyState { Root = root, Source = VersionView.Open(new(body, signature), keys, device), SourceId = id, Collision = collision,
            Temporary = Utf8.GetString(Bytes(4096 * 4)), TemporaryId = Bytes(16), Prepared = reader.ReadBoolean(), TemporaryAttributes = reader.ReadUInt32() };
        if (stream.Position != stream.Length || root.Length != 24 || id.Length != 32 || state.TemporaryId.Length != 16
            || state.Source.Type != 0 || state.Source.Deleted || !CaseConflictPolicy.SourceId(state.Source).AsSpan().SequenceEqual(id)
            || PathRules.Key(state.Destination) != intent.PathKey || intent.ExpectedLocalSequence is not null
            || ApplyState.Parent(state.Temporary) != ApplyState.Parent(state.Destination)
            || !ApplyState.Name(state.Temporary).EndsWith(".!psp", StringComparison.Ordinal)
            || !Guid.TryParseExact(ApplyState.Name(state.Temporary)[..^5], "N", out _)) throw new InvalidDataException("Invalid case recovery ownership.");
        return state;
        byte[] Bytes(int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max || count > stream.Length - stream.Position) throw new InvalidDataException("Invalid case recovery field.");
            return reader.ReadBytes(count);
        }
    }
    internal ApplyState StagingState() => new() { Root = Root, Restore = new(Destination, Source.Size, Source.MtimeTicks, Source.Attributes, Source.FileHash),
        Vector = new Dictionary<ulong, ulong>(), Temporary = Temporary, TemporaryId = TemporaryId, Prepared = Prepared, TemporaryAttributes = TemporaryAttributes };
    public override string ToString() => "Case copy recovery state (redacted)";
    private static readonly UTF8Encoding Utf8 = new(false, true);
}
