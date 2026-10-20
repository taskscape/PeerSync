using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

// Original envelopes are reauthenticated from the retained source graph.
// This compact local intent never duplicates root/member signed bodies.
internal sealed class DirectoryCaseMemberState
{
    internal required byte[] Root { get; init; }
    internal required byte[] SourceId { get; init; }
    internal required byte[] RootFileId { get; init; }
    internal required string MemberKey { get; init; }
    internal required DirectorySnapshotLink Link { get; init; }
    internal required byte[] MemberId { get; init; }
    internal uint Type { get; init; }
    internal required string Destination { get; init; }
    internal required string Temporary { get; init; }
    internal required byte[] TemporaryId { get; init; }
    internal bool Prepared { get; set; }
    internal uint TemporaryAttributes { get; set; }
    internal bool InstalledInThisCall { get; set; }
    internal static bool IsMember(ApplyIntent intent) => intent.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(intent.Payload) == 9;
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(9); Bytes(Root); Bytes(SourceId); Bytes(RootFileId); Text(MemberKey); Bytes(Link.Hash); writer.Write(Link.EncodedLength); Bytes(MemberId);
        writer.Write(Type); Text(Destination); Text(Temporary); Bytes(TemporaryId); writer.Write(Prepared); writer.Write(TemporaryAttributes); writer.Flush(); return stream.ToArray();
        void Bytes(ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        void Text(string text) => Bytes(Utf8.GetBytes(text));
    }
    internal static DirectoryCaseMemberState Decode(ApplyIntent intent)
    {
        using var stream = new MemoryStream(intent.Payload.ToArray(), writable: false); using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadInt32() != 9) throw new InvalidDataException("Unsupported directory case member kind.");
        byte[] root = Bytes(24), source = Bytes(32), nativeRoot = Bytes(16); string key = Text(); byte[] hash = Bytes(32); int length = reader.ReadInt32();
        byte[] member = Bytes(32); uint type = reader.ReadUInt32(); string destination = Text(), temporary = Text(); byte[] native = Bytes(16); byte prepared = reader.ReadByte(); uint attributes = reader.ReadUInt32();
        if (stream.Position != stream.Length || root.Length != 24 || source.Length != 32 || nativeRoot.Length != 16 || hash.Length != 32 || member.Length != 32 || native.Length != 16
            || type > 1 || prepared > 1 || PathRules.Key(key) != key || PathRules.Key(destination) != intent.PathKey || length is < 48 or > 624 || (length - 12) % 36 != 0
            || ApplyState.Parent(temporary) != ApplyState.Parent(destination) || !ApplyState.Name(temporary).EndsWith(".!psp", StringComparison.Ordinal)
            || !Guid.TryParseExact(ApplyState.Name(temporary)[..^5], "N", out _)
            || prepared == 1 && (((attributes & (uint)FileAttributes.Directory) != 0) != (type == 1) || (attributes & (uint)FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Invalid directory case member journal.");
        return new() { Root = root, SourceId = source, RootFileId = nativeRoot, MemberKey = key, Link = new(hash, length), MemberId = member, Type = type,
            Destination = destination, Temporary = temporary, TemporaryId = native, Prepared = prepared == 1, TemporaryAttributes = attributes };
        byte[] Bytes(int maximum)
        {
            int count = reader.ReadInt32(); if (count < 0 || count > maximum || count > stream.Length - stream.Position) throw new InvalidDataException("Invalid directory case member field.");
            return reader.ReadBytes(count);
        }
        string Text() => Utf8.GetString(Bytes(4096 * 4));
    }
    public override string ToString() => "Directory case member intent (redacted)";
    private static readonly UTF8Encoding Utf8 = new(false, true);
}
