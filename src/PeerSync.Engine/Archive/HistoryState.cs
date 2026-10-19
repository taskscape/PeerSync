using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

// Local-only recovery/manifest format; authenticated source bytes remain exact.
internal sealed class HistoryState
{
    internal required byte[] Root { get; init; }
    internal required VersionView Source { get; init; }
    internal bool Prepared { get; set; }
    internal bool PublicCopy { get; init; }
    internal long ArchivedTicks { get; init; }
    internal int Collision { get; set; } = 1;
    internal required string Temporary { get; set; }
    internal byte[] TemporaryId { get; set; } = [];
    internal string ExistingDestination { get; set; } = "";
    internal string Destination => ExistingDestination.Length == 0 ? Candidate(Collision) : ExistingDestination;
    internal string MarkerKey => "_history-" + Convert.ToHexString(Blake3.Hasher.Hash(Source.Record.Body).AsSpan());
    internal string Candidate(int collision) => PublicCopy
        ? ConflictNames.CopyPath(Source.Path, new(ArchivedTicks, TimeSpan.Zero), Source.ModifiedBy, collision)
        : ArchiveNames.Candidate(Source.Path, new(ArchivedTicks, TimeSpan.Zero), collision);
    internal static bool IsHistory(ApplyIntent intent) => intent.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(intent.Payload) == 4;

    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(4); writer.Write(Prepared); Bytes(Root); Bytes(Source.SourceDevice); Bytes(Source.Record.Body); Bytes(Source.Record.Signature);
        writer.Write(PublicCopy); writer.Write(ArchivedTicks); writer.Write(Collision); Text(Temporary); Bytes(TemporaryId); Text(ExistingDestination); writer.Flush();
        if (stream.Length > ApplyIntent.MaxPayloadBytes) throw new InvalidDataException("History state exceeds limit.");
        return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> value) { writer.Write(value.Length); writer.Write(value); }
        void Text(string value) => Bytes(Utf8.GetBytes(value));
    }

    internal static HistoryState Decode(ApplyIntent intent, FolderKeys keys) => DecodePayload(intent.Payload.ToArray(), intent.PathKey, keys);

    internal static HistoryState DecodeMarker(MetaRow marker, FolderKeys keys)
    {
        if (marker.Value is not { Length: > 0 and <= ApplyIntent.MaxPayloadBytes }) throw new InvalidDataException("Invalid history marker size.");
        var state = DecodePayload(marker.Value, null, keys);
        if (!state.Prepared || state.MarkerKey != marker.Key) throw new InvalidDataException("History marker does not match its authenticated source.");
        return state;
    }

    private static HistoryState DecodePayload(byte[] payload, string? expectedPath, FolderKeys keys)
    {
        using var stream = new MemoryStream(payload, writable: false); using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadInt32() != 4) throw new InvalidDataException("Unsupported history state.");
        bool prepared = reader.ReadBoolean(); byte[] root = Bytes(24), source = Bytes(32), body = Bytes(SignedRecord.MaxBodyBytes), signature = Bytes(64);
        var version = VersionView.Open(new(body, signature), keys, source);
        var state = new HistoryState { Root = root, Source = version, Prepared = prepared, PublicCopy = reader.ReadBoolean(),
            ArchivedTicks = reader.ReadInt64(), Collision = reader.ReadInt32(), Temporary = Utf8.GetString(Bytes(4352 * 4)), TemporaryId = Bytes(16),
            ExistingDestination = Utf8.GetString(Bytes(4352 * 4)) };
        bool existingValid = state.ExistingDestination.Length == 0 || prepared && (state.PublicCopy
            ? PathRules.IsValid(state.ExistingDestination) && state.ExistingDestination.Split('/').Any(p => p.Contains(".sync-conflict-", StringComparison.Ordinal))
                && PathRules.Key(state.ExistingDestination) != PathRules.Key(version.Path)
            : state.ExistingDestination.StartsWith(".psp/archive/", StringComparison.Ordinal) && PathRules.IsValid(state.ExistingDestination[13..]));
        if (stream.Position != stream.Length || root.Length != 24 || version.Type != 0 || version.Deleted
            || expectedPath is not null && PathRules.Key(version.Path) != expectedPath || state.ArchivedTicks <= 0 || state.ArchivedTicks > DateTime.MaxValue.Ticks
            || !existingValid || state.Collision is < 1 or > 1000 || state.TemporaryId.Length is not (0 or 16) || prepared && state.TemporaryId.Length != 16
            || ApplyState.Parent(state.Temporary) != ApplyState.Parent(state.Destination)
            || !ApplyState.Name(state.Temporary).EndsWith(".!psp", StringComparison.Ordinal)
            || !Guid.TryParseExact(ApplyState.Name(state.Temporary)[..^5], "N", out _))
            throw new InvalidDataException("Invalid history ownership or source.");
        FileMetadata.ValidateMtime(version.MtimeTicks); return state;
        byte[] Bytes(int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max || count > stream.Length - stream.Position) throw new InvalidDataException("Invalid history field length.");
            return reader.ReadBytes(count);
        }
    }
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public override string ToString() => "Historical version recovery state (redacted)";
}
