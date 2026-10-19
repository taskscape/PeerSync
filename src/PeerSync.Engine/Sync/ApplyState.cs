using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;
using PeerSync.Engine.Native;

namespace PeerSync.Engine.Sync;

// Explicit, bounded local format. Never deserialize this from the network or log it.
internal sealed class ApplyState
{
    internal bool Prepared { get; set; }
    internal bool ScheduledDeletion { get; init; }
    internal required byte[] Root { get; init; }
    internal VersionView? Version { get; init; }
    internal LocalRestoreData? Restore { get; init; }
    internal required IReadOnlyDictionary<ulong, ulong> Vector { get; init; }
    internal bool IsRestore => Restore is not null;
    internal uint Type => Version?.Type ?? 0;
    internal bool OriginalIsDirectory => (OriginalAttributes & (uint)FileAttributes.Directory) != 0;
    internal string Path => Version?.Path ?? Restore!.Path;
    internal long Size => Version?.Size ?? Restore!.Size;
    internal long MtimeTicks => Version?.MtimeTicks ?? Restore!.MtimeTicks;
    internal uint Attributes => Version?.Attributes ?? Restore!.Attributes;
    internal bool Deleted => Version?.Deleted ?? false;
    internal ReadOnlySpan<byte> FileHash => Version is { } version ? version.FileHash : Restore!.FileHash;
    internal string Temporary { get; init; } = "";
    internal byte[] TemporaryId { get; init; } = [];
    internal uint TemporaryAttributes { get; set; }
    internal string Original { get; set; } = "";
    internal byte[] OriginalId { get; set; } = [];
    internal byte[] OriginalHash { get; set; } = [];
    internal byte[] OriginalDevice { get; set; } = [];
    internal long OriginalSize { get; set; }
    internal long OriginalMtime { get; set; }
    internal uint OriginalAttributes { get; set; }
    internal string Retained { get; set; } = "";
    internal long ArchivedTicks { get; set; }

    internal byte[] Encode()
    {
        if ((Version is null) == (Restore is null)) throw new InvalidDataException("Apply state requires exactly one operation kind.");
        if (ScheduledDeletion && (IsRestore || !Deleted)) throw new InvalidDataException("Scheduled barrier requires a remote deletion.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(IsRestore ? 2 : ScheduledDeletion ? 5 : 1); writer.Write(Prepared); Bytes(Root);
        if (Version is { } version)
        {
            Bytes(version.SourceDevice); Bytes(version.Record.Body); Bytes(version.Record.Signature); Bytes(VersionVector.Serialize(Vector));
        }
        else
        {
            Text(Path); writer.Write(Size); writer.Write(MtimeTicks); writer.Write(Attributes); Bytes(FileHash);
        }
        Text(Temporary); Bytes(TemporaryId); writer.Write(TemporaryAttributes);
        Text(Original); Bytes(OriginalId); Bytes(OriginalHash); Bytes(OriginalDevice); writer.Write(OriginalSize); writer.Write(OriginalMtime);
        writer.Write(OriginalAttributes); Text(Retained); writer.Write(ArchivedTicks); writer.Flush();
        if (stream.Length > ApplyIntent.MaxPayloadBytes) throw new InvalidDataException("Apply state exceeds limit.");
        return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        void Text(string value) => Bytes(Utf8.GetBytes(value));
    }

    internal static ApplyState Decode(ApplyIntent intent, FolderKeys keys)
    {
        using var stream = new MemoryStream(intent.Payload.ToArray(), writable: false); using var reader = new BinaryReader(stream, Utf8);
        int kind = reader.ReadInt32();
        if (kind is not (1 or 2 or 5)) throw new InvalidDataException("Unsupported apply state.");
        bool prepared = reader.ReadBoolean(); byte[] root = Bytes(24);
        VersionView? version = null; LocalRestoreData? restore = null;
        IReadOnlyDictionary<ulong, ulong> vector = new Dictionary<ulong, ulong>();
        if (kind is 1 or 5)
        {
            byte[] source = Bytes(32), body = Bytes(SignedRecord.MaxBodyBytes), signature = Bytes(64);
            vector = VersionVector.Deserialize(Bytes(ApplyIntent.MaxPayloadBytes));
            version = VersionView.Open(new(body, signature), keys, source);
        }
        else restore = new(Text(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt32(), Bytes(32));
        var result = new ApplyState
        {
            Prepared = prepared, ScheduledDeletion = kind == 5, Root = root, Version = version, Restore = restore, Vector = vector,
            Temporary = Text(), TemporaryId = Bytes(16), TemporaryAttributes = reader.ReadUInt32(), Original = Text(), OriginalId = Bytes(16), OriginalHash = Bytes(32),
            OriginalDevice = Bytes(32),
            OriginalSize = reader.ReadInt64(), OriginalMtime = reader.ReadInt64(), OriginalAttributes = reader.ReadUInt32(),
            Retained = Text(), ArchivedTicks = reader.ReadInt64(),
        };
        if (stream.Position != stream.Length || root.Length != 24 || PathRules.Key(result.Path) != intent.PathKey || result.ScheduledDeletion && !result.Deleted
            || version is not null && (version.Type > 1 || VersionVector.Compare(vector, version.Vector) is VvOrder.Concurrent or VvOrder.DominatedBy
                || keys.CanWrite && VersionVector.Compare(vector, version.Vector) != VvOrder.Equal))
            throw new InvalidDataException("Invalid apply identity or vector.");
        if (result.Temporary.Length != 0)
        {
            if (result.Deleted || result.TemporaryId.Length != 16 || Parent(result.Temporary) != Parent(result.Path)
                || !Guid.TryParseExact(Name(result.Temporary).Replace(".!psp", "", StringComparison.Ordinal), "N", out _)
                || !Name(result.Temporary).EndsWith(".!psp", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid temporary ownership.");
        }
        else if (result.TemporaryId.Length != 0) throw new InvalidDataException("Unexpected temporary identity.");
        if (result.IsRestore && result.Temporary.Length == 0) throw new InvalidDataException("Restore state has no staging ownership.");
        if (result.OriginalId.Length != 0)
        {
            if (result.OriginalId.Length != 16 || result.OriginalHash.Length != (result.OriginalIsDirectory ? 0 : 32)
                || result.OriginalIsDirectory && result.OriginalSize != 0 || result.OriginalDevice.Length != 32 || result.OriginalSize < 0
                || !PathRules.IsValid(result.Original) || PathRules.Key(result.Original) != intent.PathKey
                || result.OriginalMtime < 0 || result.OriginalMtime > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Invalid original ownership.");
        }
        else if (result.Original.Length != 0 || result.OriginalHash.Length != 0 || result.OriginalDevice.Length != 0 || result.Retained.Length != 0)
            throw new InvalidDataException("Unexpected original state.");
        if (result.Retained.Length != 0)
        {
            bool archive = result.Retained.StartsWith(".psp/archive/", StringComparison.Ordinal);
            string relative = archive ? result.Retained[13..] : result.Retained;
            if (archive && result.OriginalIsDirectory || !PathRules.IsValid(relative) || !archive && Parent(relative) != Parent(result.Original)
                || PathRules.Key(relative) == intent.PathKey || result.ArchivedTicks <= 0 || result.ArchivedTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Invalid retained version path.");
        }
        if (prepared && !result.Deleted && result.Temporary.Length == 0 && result.OriginalId.Length == 0)
            throw new InvalidDataException("Prepared live file has no owned data.");
        if (prepared && !result.Deleted && result.Temporary.Length == 0
            && (result.OriginalIsDirectory != (result.Type == 1) || result.OriginalSize != result.Size || !result.OriginalHash.AsSpan().SequenceEqual(result.FileHash)))
            throw new InvalidDataException("Metadata-only state changes file content.");
        return result;

        byte[] Bytes(int maximum)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximum || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid apply field length.");
            return reader.ReadBytes(length);
        }
        string Text() => Utf8.GetString(Bytes(4352 * 4));
    }

    internal static string Parent(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";
    internal static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    internal static string Join(string parent, string name) => parent.Length == 0 ? name : name.Length == 0 ? parent : parent + "/" + name;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public override string ToString() => "File application recovery state (redacted)";
}

// A local operation, never an authenticated remote version. It contains only
// verified restoration expectations; normal scanning generates publication.
internal sealed class LocalRestoreData
{
    private readonly byte[] hash;
    internal LocalRestoreData(string path, long size, long mtimeTicks, uint attributes, ReadOnlySpan<byte> hash)
    {
        if (!PathRules.IsValid(path) || size < 0 || hash.Length != 32
            || (attributes & ~(uint)(FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
            throw new InvalidDataException("Invalid local restoration expectations.");
        FileMetadata.ValidateMtime(mtimeTicks);
        Path = path; Size = size; MtimeTicks = mtimeTicks; Attributes = attributes; this.hash = hash.ToArray();
    }
    internal string Path { get; }
    internal long Size { get; }
    internal long MtimeTicks { get; }
    internal uint Attributes { get; }
    internal ReadOnlySpan<byte> FileHash => hash;
    public override string ToString() => "Local restoration expectations (redacted)";
}
