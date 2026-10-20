using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

// Caller retains FolderOperationGate. This prepares a logical snapshot draft;
// it does not publish a directory row or release the GC barrier.
internal sealed partial class DirectorySnapshotCapture(string rootPath, FolderIndex index, FolderKeys keys, byte[] peer, DiagnosticJournal? journal = null)
{
    private readonly byte[] sourceHint = peer is { Length: 32 } ? peer.ToArray() : throw new ArgumentException("Invalid directory capture source hint.", nameof(peer));
    internal Action<string>? Checkpoint { get; init; }
    internal async Task<DirectoryCaptureState> AdvanceUnderGateAsync(string parentPath, IEnumerable<SignedRecord> children, int maximumMembers, CancellationToken token)
    {
        _ = PathRules.Key(parentPath); ArgumentNullException.ThrowIfNull(children);
        if (maximumMembers is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumMembers));
        Guid session = Guid.NewGuid(); int captured = 0;
        try
        {
            using var tree = new SafeFileTree(rootPath);
            if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } root || !root.AsSpan().SequenceEqual(tree.Identity))
                throw new IOException("Directory capture root changed or is unbound.");
            using var pinnedDirectory = tree.Directory(parentPath);
            using var ancestor = tree.Directory(ApplyState.Parent(parentPath));
            using var metadata = ancestor.OpenMetadataNode(ApplyState.Name(parentPath));
            byte[] native = BlockCaptureProof.Identity(metadata);
            var manifest = ReadManifest(parentPath, children, token);
            byte[] initial = new DirectorySnapshotBuilder(static (_, _) => throw new InvalidOperationException()).ExportCheckpoint();
            var draft = await index.BeginDirectoryCaptureAsync(parentPath, root, native, manifest.Hash, manifest.Count, initial, token).ConfigureAwait(false);
            if (draft.Hash is not null) { Log(true, draft.Accepted); return draft; }
            using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, sourceHint, journal);
            try
            {
                var builder = DirectorySnapshotBuilder.RestoreCheckpoint(blocks.PutAsync, draft.BuilderState!);
                if (builder.AcceptedMembers != draft.Accepted) throw new InvalidDataException("Directory capture checkpoint count does not match its prefix.");
                using var digest = StartDigest(parentPath); long count = 0; string? previous = null, cursor = draft.LastKey;
                bool boundary = draft.Accepted == 0;
                foreach (var child in children)
                {
                    token.ThrowIfCancellationRequested(); var member = OpenMember(parentPath, child, previous); string key = PathRules.Key(member.Path); previous = key;
                    digest.Update(CaseConflictPolicy.SourceId(member));
                    if (count == draft.Accepted - 1)
                    {
                        if (key != draft.LastKey) throw new InvalidDataException("Directory capture cursor does not match its input.");
                        boundary = true;
                    }
                    if (count >= draft.Accepted && captured < maximumMembers)
                    {
                        await builder.AddAsync(await blocks.CaptureMemberAsync(parentPath, member.Record, token).ConfigureAwait(false), token).ConfigureAwait(false);
                        captured++; cursor = key; Checkpoint?.Invoke("member");
                    }
                    count = checked(count + 1);
                }
                byte[] countBytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(countBytes, count); digest.Update(countBytes);
                if (!boundary || count != manifest.Count || !digest.Finalize().AsSpan().SequenceEqual(manifest.Hash))
                    throw new InvalidDataException("Directory capture input changed during advancement.");
                long accepted = checked(draft.Accepted + captured); bool complete = accepted == manifest.Count;
                DirectorySnapshotLink? completed = complete ? await builder.CompleteAsync(token).ConfigureAwait(false) : null;
                byte[]? state = complete ? null : builder.ExportCheckpoint();
                await blocks.FlushAsync(token).ConfigureAwait(false); Checkpoint?.Invoke("captured"); token.ThrowIfCancellationRequested();
                var result = await index.AdvanceDirectoryCaptureAsync(draft, accepted, cursor, state, completed?.Hash, completed?.EncodedLength, token).ConfigureAwait(false);
                Checkpoint?.Invoke("progress"); Log(complete, result.Accepted); return result;
            }
            finally { await blocks.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.capture_failed", "directory-capture") { SessionId = session, Level = DiagnosticLevel.Warning,
                ItemsChanged = captured, ErrorCode = error is OperationCanceledException ? "cancelled" : error.GetType().Name,
                HResult = error.HResult, NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }, RetryInMilliseconds = 10000 });
            throw;
        }
        void Log(bool complete, long accepted) => journal?.TryWrite(new(complete ? "directory.capture_complete" : "directory.capture_pending", "directory-capture")
        { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = accepted, ItemsChanged = captured, RetryInMilliseconds = complete ? null : 10000 });
    }
    private CaptureManifest ReadManifest(string parentPath, IEnumerable<SignedRecord> children, CancellationToken token)
    {
        using var digest = StartDigest(parentPath); long count = 0; string? previous = null;
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested(); var member = OpenMember(parentPath, child, previous); previous = PathRules.Key(member.Path);
            digest.Update(CaseConflictPolicy.SourceId(member)); count = checked(count + 1);
        }
        byte[] bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, count); digest.Update(bytes);
        return new(digest.Finalize().AsSpan().ToArray(), count);
    }
    private VersionView OpenMember(string parent, SignedRecord child, string? previous)
    {
        var member = VersionView.Open(child, keys, sourceHint); string key = PathRules.Key(member.Path);
        if (member.Deleted || member.Type > 1 || ApplyState.Parent(key) != PathRules.Key(parent)
            || previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0)
            throw new InvalidDataException("Invalid or unordered directory capture member.");
        if (member.Type == 1) _ = DirectorySnapshotBlocks.SnapshotOf(member); return member;
    }
    private static Blake3.Hasher StartDigest(string parentPath)
    {
        var digest = Blake3.Hasher.New(); digest.Update("PeerSync directory capture input v1"u8);
        byte[] name = Encoding.UTF8.GetBytes(parentPath); byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, name.Length);
        digest.Update(length); digest.Update(name); return digest;
    }
    private sealed record CaptureManifest(byte[] Hash, long Count);
}
