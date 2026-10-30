using System.ComponentModel;
using Google.Protobuf;
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
    // Caller owns the operation gate and history prerequisite. Null publication
    // releases an identical authenticated root without vector/sequence churn.
    internal async Task<DirectoryPublicationCommit> CommitQueuedUnderGateAsync(DirectoryPublication queued, DirectoryCaptureState draft,
        LocalFileRow? publication, long nowTicks, CancellationToken token)
    {
        Guid session = queued?.Version ?? Guid.NewGuid(); string stage = "verify-template";
        try
        {
            ArgumentNullException.ThrowIfNull(queued); ArgumentNullException.ThrowIfNull(draft);
            var current = publication ?? index.LocalFiles.Find(queued.Path) ?? throw new InvalidDataException("Directory release has no signed current row.");
            if (current.Record is null || draft.Hash is null || draft.Length is null) throw new InvalidDataException("Directory publication is incomplete.");
            var version = VersionView.Open(current.Record, keys, sourceHint); var root = DirectorySnapshotBlocks.SnapshotOf(version);
            var template = VersionView.Open(queued.Template, keys, sourceHint); var templateBody = template.ExportBody(); var body = version.ExportBody();
            if (template.Type != 1 || template.Deleted || (template.Path != queued.Path && (!queued.ReconstructionOnly || PathRules.Key(template.Path) != PathRules.Key(queued.Path)))
                || version.Path != queued.Path || draft.ParentPath != queued.Path || !root.Hash.AsSpan().SequenceEqual(draft.Hash) || root.EncodedLength != draft.Length)
                throw new InvalidDataException("Directory template does not match its captured root.");
            if (publication is not null)
            {
                if (!queued.ReconstructionOnly && templateBody.DirectorySnapshotHash.Length != 0) throw new InvalidDataException("New directory template already contains another root.");
                if (!keys.CanWrite || !version.ModifiedBy.SequenceEqual(sourceHint) || VersionVector.Compare(version.Vector, current.Vector) != VvOrder.Equal)
                    throw new InvalidDataException("New directory publication has no local signing authority.");
                body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0;
            }
            else
            {
                if (version.MtimeTicks != template.MtimeTicks || version.Attributes != template.Attributes)
                    throw new InvalidDataException("Unchanged directory release changed its metadata.");
                if (templateBody.DirectorySnapshotHash.Length != 0)
                {
                    var received = DirectorySnapshotBlocks.SnapshotOf(template);
                    if (!received.Hash.AsSpan().SequenceEqual(draft.Hash) || received.EncodedLength != draft.Length)
                        throw new InvalidDataException("Readonly directory reconstruction does not match its signed root.");
                }
            }
            const uint attributes = (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden);
            if (version.MtimeTicks != current.MtimeTicks || version.ChangedTicks != current.ChangedTicks || version.Attributes != (current.Attributes & attributes))
                throw new InvalidDataException("Directory row does not match its signed metadata.");
            stage = "verify-root"; using var tree = new SafeFileTree(rootPath);
            if (!tree.Identity.SequenceEqual(draft.RootId) || !tree.Identity.SequenceEqual(queued.RootId)
                || index.Meta.Find("_root-identity")?.Value is not { Length: 24 } bound || !tree.Identity.SequenceEqual(bound)) throw new IOException("Directory queue root changed.");
            stage = "verify-native"; using var pinned = tree.Directory(queued.Path); using var parent = tree.Directory(ApplyState.Parent(queued.Path));
            using var native = parent.OpenMetadataNode(ApplyState.Name(queued.Path)); byte[] identity = BlockCaptureProof.Identity(native);
            if (!identity.AsSpan().SequenceEqual(queued.NativeId) || !identity.AsSpan().SequenceEqual(draft.NativeId)
                || ((uint)FileSnapshot.Read(native).Attributes & attributes) != (queued.Attributes & attributes)) throw new IOException("Directory queue native identity or attributes changed.");
            stage = "commit-row-cache"; Checkpoint?.Invoke("before-queue-publication");
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            stage = "verify-parent-clock";
            if (publication is not null && !body.Equals(DirectoryPublicationClock.Template(index, keys, sourceHint, queued, version.ChangedTicks)))
                throw new InvalidDataException("Directory publication metadata or parent clock changed.");
            stage = "commit-row-cache";
            var result = await index.CommitDirectoryPublicationAsync(queued, draft, publication, nowTicks, token).ConfigureAwait(false);
            stage = "publication-committed"; Checkpoint?.Invoke("queue-published");
            journal?.TryWrite(new(result.Changed ? "directory.queue_published" : "directory.queue_unchanged", "directory-publication")
                { SessionId = session, Level = DiagnosticLevel.Debug, ItemsChanged = result.Changed ? 1 : 0 });
            return result;
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.queue_publish_failed", "directory-publication") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = stage + "." + (error is OperationCanceledException ? "cancelled" : error.GetType().Name), Exception = error, HResult = error.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }, RetryInMilliseconds = 10000 });
            throw;
        }
    }
}
