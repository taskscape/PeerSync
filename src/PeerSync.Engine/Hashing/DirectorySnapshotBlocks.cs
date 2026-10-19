using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

// Bounded metadata I/O primitives, not graph completeness/deletion authority.
// The caller holds the folder operation gate and flushes before publication.
internal sealed class DirectorySnapshotBlocks : IDisposable
{
    private readonly PublishedBlocks blocks;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] peer;
    private readonly DiagnosticJournal? journal;
    private int pending;
    internal DirectorySnapshotBlocks(string rootPath, FolderIndex index, FolderKeys keys, byte[] sourceHint, DiagnosticJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(sourceHint);
        if (sourceHint.Length != 32) throw new ArgumentException("Invalid directory snapshot source hint.", nameof(sourceHint));
        this.index = index; this.keys = keys; peer = sourceHint.ToArray(); this.journal = journal; blocks = new(rootPath, index);
    }
    internal async Task<DirectorySnapshotLink> CaptureMemberAsync(string parentPath, SignedRecord record, CancellationToken token)
    {
        var member = VersionView.Open(record, keys, peer); RequireMember(parentPath, member);
        byte[] envelope = DirectorySnapshotEnvelope.Encode(member.Record); var fragments = new List<DirectorySnapshotLink>(DirectorySnapshotNode.MaximumRecordLinks);
        for (int offset = 0; offset < envelope.Length; offset += DirectorySnapshotNode.MaximumBlobBytes)
        {
            token.ThrowIfCancellationRequested();
            var blob = DirectorySnapshotNode.Blob(envelope.AsSpan(offset, Math.Min(DirectorySnapshotNode.MaximumBlobBytes, envelope.Length - offset)));
            fragments.Add(await PutAsync(blob, token).ConfigureAwait(false));
        }
        return await PutAsync(DirectorySnapshotNode.Record(fragments, envelope.Length), token).ConfigureAwait(false);
    }
    internal async Task<DirectorySnapshotLink> CaptureDirectoryAsync(string parentPath, IEnumerable<SignedRecord> children, CancellationToken token)
    {
        _ = PathRules.Key(parentPath); ArgumentNullException.ThrowIfNull(children);
        var builder = new DirectorySnapshotBuilder(PutAsync); string? previous = null;
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested(); var member = VersionView.Open(child, keys, peer); RequireMember(parentPath, member);
            string key = PathRules.Key(member.Path);
            if (previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0)
                throw new InvalidDataException("Directory snapshot members are duplicated or out of order.");
            previous = key;
            await builder.AddAsync(await CaptureMemberAsync(parentPath, member.Record, token).ConfigureAwait(false), token).ConfigureAwait(false);
        }
        var root = await builder.CompleteAsync(token).ConfigureAwait(false);
        await FlushAsync(token).ConfigureAwait(false); return root;
    }
    internal async Task<DirectorySnapshotLink> PutAsync(DirectorySnapshotNode node, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(node); token.ThrowIfCancellationRequested(); byte[] bytes = node.Encode();
        byte[] hash = Blake3.Hasher.Hash(bytes).AsSpan().ToArray(); blocks.Put(bytes, hash, token);
        if (++pending >= 128) await FlushAsync(token).ConfigureAwait(false);
        return new(hash, bytes.Length);
    }
    internal async Task<DirectorySnapshotNode> ReadNodeAsync(DirectorySnapshotLink expected,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(fetch); token.ThrowIfCancellationRequested();
        byte[]? bytes = blocks.TryRead(expected.Hash, expected.EncodedLength, token); bool missing = bytes is null;
        if (missing)
        {
            var response = await fetch(expected.Hash, expected.EncodedLength, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (response.Length != expected.EncodedLength) throw new InvalidDataException("Directory snapshot response length mismatch.");
            bytes = response.ToArray(); // Provider buffers never become retained mutable state.
        }
        var node = DirectorySnapshotNode.Open(expected, bytes!);
        if (missing)
        {
            blocks.Put(bytes!, expected.Hash, token);
            if (++pending >= 128) await FlushAsync(token).ConfigureAwait(false);
        }
        return node;
    }
    internal async Task<DirectorySnapshotNode> ReadRootAsync(SignedRecord directory,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, CancellationToken token)
    {
        var version = VersionView.Open(directory, keys, peer); var root = SnapshotOf(version);
        var node = await ReadNodeAsync(root, fetch, token).ConfigureAwait(false);
        if (node.Kind is not (DirectorySnapshotNodeKind.Entries or DirectorySnapshotNodeKind.Branch)) throw new InvalidDataException("Directory snapshot root is not a page.");
        return node;
    }
    internal async Task<VersionView> ReadMemberAsync(string parentPath, DirectorySnapshotLink expected,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, CancellationToken token)
    {
        var record = await ReadNodeAsync(expected, fetch, token).ConfigureAwait(false);
        if (record.Kind != DirectorySnapshotNodeKind.Record) throw new InvalidDataException("Directory snapshot entry is not a record.");
        byte[] envelope = new byte[record.PayloadLength]; int offset = 0;
        foreach (var fragment in record.Links)
        {
            var blob = await ReadNodeAsync(fragment, fetch, token).ConfigureAwait(false);
            if (blob.Kind != DirectorySnapshotNodeKind.Blob || blob.BlobPayload.Length > envelope.Length - offset)
                throw new InvalidDataException("Directory snapshot record fragment is invalid.");
            blob.BlobPayload.CopyTo(envelope.AsSpan(offset)); offset += blob.BlobPayload.Length;
        }
        if (offset != envelope.Length) throw new InvalidDataException("Directory snapshot record is incomplete.");
        var member = VersionView.Open(DirectorySnapshotEnvelope.Decode(envelope), keys, peer); RequireMember(parentPath, member); return member;
    }
    internal Task<int> FlushAsync(CancellationToken token)
    { pending = 0; return blocks.FlushAsync(index, token, journal); }
    internal static DirectorySnapshotLink SnapshotOf(VersionView directory)
    {
        var body = directory.ExportBody();
        if (directory.Deleted || directory.Type != 1 || body.DirectorySnapshotHash.Length != 32 || body.DirectorySnapshotLength == 0)
            throw new InvalidDataException("Directory snapshot is unavailable; absence is not an empty-tree proof.");
        return new(body.DirectorySnapshotHash.Span, checked((int)body.DirectorySnapshotLength));
    }
    private static void RequireMember(string parentPath, VersionView member)
    {
        if (member.Deleted || member.Type > 1 || ApplyState.Parent(PathRules.Key(member.Path)) != PathRules.Key(parentPath))
            throw new InvalidDataException("Directory snapshot member does not belong to the live parent.");
        if (member.Type == 1) _ = SnapshotOf(member);
    }
    public void Dispose() => blocks.Dispose();
}
