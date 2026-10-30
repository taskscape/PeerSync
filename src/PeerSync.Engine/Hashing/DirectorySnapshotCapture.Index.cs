using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed partial class DirectorySnapshotCapture
{
    // Unlike arbitrary enumerables, this input is the engine's own indexed
    // local table. Its trigger-maintained epoch covers every possible mutation.
    internal async Task<DirectoryCaptureState> AdvancePublishedUnderGateAsync(string parentPath, int maximumMembers, CancellationToken token)
    {
        _ = PathRules.Key(parentPath);
        if (maximumMembers is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumMembers));
        Guid session = Guid.NewGuid(); int captured = 0;
        try
        {
            using var tree = new SafeFileTree(rootPath);
            if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } root || !tree.Identity.SequenceEqual(root))
                throw new IOException("Indexed directory capture root changed.");
            using var pinned = tree.Directory(parentPath); using var ancestor = tree.Directory(ApplyState.Parent(parentPath));
            using var native = ancestor.OpenMetadataNode(ApplyState.Name(parentPath)); byte[] nativeId = BlockCaptureProof.Identity(native);
            long epoch = index.ReadDirectoryCaptureEpoch(); var draft = index.ReadDirectoryCapture(parentPath);
            if (draft is null || draft.InputEpoch != epoch || draft.ParentPath != parentPath || !draft.RootId.AsSpan().SequenceEqual(root) || !draft.NativeId.AsSpan().SequenceEqual(nativeId))
            {
                // Revalidate the complete input only when its witness changed.
                // The generic path retains its two-pass/cursor consistency checks.
                var rebuilt = await AdvanceUnderGateAsync(parentPath, PublishedChildren(parentPath, token), maximumMembers, token).ConfigureAwait(false);
                Checkpoint?.Invoke("before-input-bind");
                var bound = await index.BindDirectoryCaptureEpochAsync(rebuilt, epoch, token).ConfigureAwait(false);
                journal?.TryWrite(new("directory.capture_input_revalidated", "directory-capture-index") { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = bound.MemberCount });
                return bound;
            }
            draft = await index.BindDirectoryCaptureEpochAsync(draft, epoch, token).ConfigureAwait(false); // Touch without clearing the validated witness.
            if (draft.Hash is not null) { Log(true, draft.Accepted); return draft; }
            using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, sourceHint, journal);
            try
            {
                var builder = DirectorySnapshotBuilder.RestoreCheckpoint(blocks.PutAsync, draft.BuilderState!);
                if (builder.AcceptedMembers != draft.Accepted) throw new InvalidDataException("Indexed directory checkpoint does not match its prefix.");
                string? cursor = draft.LastKey; bool exhausted = false;
                while (captured < maximumMembers)
                {
                    token.ThrowIfCancellationRequested(); var batch = index.ReadDirectorySnapshotChildren(parentPath, cursor, maximumMembers - captured);
                    foreach (var row in batch.Rows)
                    {
                        token.ThrowIfCancellationRequested(); ValidatePublishedChild(row);
                        var member = OpenMember(parentPath, row.Record!, cursor);
                        await builder.AddAsync(await blocks.CaptureMemberAsync(parentPath, member.Record, token).ConfigureAwait(false), token).ConfigureAwait(false);
                        cursor = PathRules.Key(member.Path); captured++; Checkpoint?.Invoke("member");
                    }
                    if (!batch.More) { exhausted = true; break; }
                    if (batch.Rows.Count == 0) throw new InvalidDataException("Indexed directory input did not make progress.");
                }
                long accepted = checked(draft.Accepted + captured);
                if (accepted > draft.MemberCount || exhausted && accepted != draft.MemberCount)
                    throw new InvalidDataException("Indexed directory input count changed.");
                bool complete = accepted == draft.MemberCount;
                if (complete && !exhausted && index.ReadDirectorySnapshotChildren(parentPath, cursor, 1).Rows.Count != 0)
                    throw new InvalidDataException("Indexed directory input has unexpected remaining members.");
                var completed = complete ? await builder.CompleteAsync(token).ConfigureAwait(false) : null;
                byte[]? state = complete ? null : builder.ExportCheckpoint();
                await blocks.FlushAsync(token).ConfigureAwait(false); Checkpoint?.Invoke("captured"); token.ThrowIfCancellationRequested();
                var result = await index.AdvanceDirectoryCaptureAsync(draft, accepted, cursor, state, completed?.Hash, completed?.EncodedLength, token).ConfigureAwait(false);
                Checkpoint?.Invoke("progress"); Log(complete, accepted); return result;
            }
            finally { await blocks.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.capture_index_failed", "directory-capture-index") { SessionId = session, Level = DiagnosticLevel.Warning,
                ItemsChanged = captured, ErrorCode = error is OperationCanceledException ? "cancelled" : error.GetType().Name, Exception = error, HResult = error.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }, RetryInMilliseconds = 10000 });
            throw;
        }
        void Log(bool complete, long accepted) => journal?.TryWrite(new(complete ? "directory.capture_index_complete" : "directory.capture_index_pending", "directory-capture-index")
        { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = accepted, ItemsChanged = captured, RetryInMilliseconds = complete ? null : 10000 });
    }
    private IEnumerable<SignedRecord> PublishedChildren(string parentPath, CancellationToken token)
    {
        string? cursor = null; DirectoryChildBatch batch;
        do
        {
            token.ThrowIfCancellationRequested(); batch = index.ReadDirectorySnapshotChildren(parentPath, cursor);
            foreach (var row in batch.Rows) { token.ThrowIfCancellationRequested(); ValidatePublishedChild(row); yield return row.Record!; }
            if (batch.More && (batch.Rows.Count == 0 || batch.NextKey == cursor)) throw new InvalidDataException("Directory child input did not make progress.");
            cursor = batch.NextKey;
        } while (batch.More);
    }
    private void ValidatePublishedChild(LocalFileRow row)
    {
        if (row.Record is null) throw new InvalidDataException("Directory child has no published record.");
        var version = VersionView.Open(row.Record, keys, sourceHint);
        if (PathRules.Key(version.Path) != PathRules.Key(row.Path) || version.Type != row.Type || version.Deleted != row.Deleted
            || version.Type == 0 && (version.Size != row.Size || row.FileHash is null || !version.FileHash.SequenceEqual(row.FileHash)))
            throw new InvalidDataException("Published directory child does not match its local row.");
    }
}
