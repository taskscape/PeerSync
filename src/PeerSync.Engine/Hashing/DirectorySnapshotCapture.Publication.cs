using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed partial class DirectorySnapshotCapture
{
    // Scanner owns FolderOperationGate, has retained old versions and supplied
    // only durably published children. This method does no native replacement.
    internal async Task<LocalFileRow> PublishUnderGateAsync(DirectoryCaptureState draft, LocalFileRow publication, long expectedLocalSequence, CancellationToken token)
    {
        Guid session = Guid.NewGuid(); string stage = "verify-signed-root";
        journal?.TryWrite(new("directory.publish_start", "directory-capture") { SessionId = session, Level = DiagnosticLevel.Debug });
        try
        {
            var result = await PublishCoreAsync(draft, publication, expectedLocalSequence, value => stage = value, token).ConfigureAwait(false);
            journal?.TryWrite(new("directory.publish_complete", "directory-capture") { SessionId = session, Level = DiagnosticLevel.Debug, ItemsChanged = 1 });
            return result;
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.publish_failed", "directory-capture") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = stage + "." + (error is OperationCanceledException ? "cancelled" : error.GetType().Name), Exception = error, HResult = error.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }, RetryInMilliseconds = 10000 });
            throw;
        }
    }
    private async Task<LocalFileRow> PublishCoreAsync(DirectoryCaptureState draft, LocalFileRow publication, long expectedLocalSequence,
        Action<string> markStage, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(publication);
        if (!keys.CanWrite) throw new InvalidOperationException("Local directory snapshot publication requires write access.");
        if (draft.Hash is null || draft.Length is null || publication.Record is null) throw new InvalidDataException("Directory capture publication is incomplete.");
        var version = VersionView.Open(publication.Record, keys, sourceHint); var root = DirectorySnapshotBlocks.SnapshotOf(version);
        const uint attributes = (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden);
        if (!root.Hash.AsSpan().SequenceEqual(draft.Hash) || root.EncodedLength != draft.Length || version.Path != draft.ParentPath
            || version.Path != publication.Path || version.MtimeTicks != publication.MtimeTicks || version.ChangedTicks != publication.ChangedTicks
            || version.Attributes != (publication.Attributes & attributes) || !version.ModifiedBy.SequenceEqual(sourceHint)
            || VersionVector.Compare(version.Vector, publication.Vector) != VvOrder.Equal)
            throw new InvalidDataException("Signed directory publication does not match its captured root or row.");
        markStage("verify-root"); using var tree = new SafeFileTree(rootPath);
        if (!tree.Identity.SequenceEqual(draft.RootId) || index.Meta.Find("_root-identity")?.Value is not { Length: 24 } bound || !tree.Identity.SequenceEqual(bound))
            throw new IOException("Directory publication root changed.");
        markStage("verify-native"); using var pinned = tree.Directory(draft.ParentPath);
        using var ancestor = tree.Directory(ApplyState.Parent(draft.ParentPath));
        using var native = ancestor.OpenMetadataNode(ApplyState.Name(draft.ParentPath));
        if (!BlockCaptureProof.Identity(native).AsSpan().SequenceEqual(draft.NativeId)
            || ((uint)FileSnapshot.Read(native).Attributes & attributes) != (publication.Attributes & attributes))
            throw new IOException("Directory publication identity or attributes changed.");
        markStage("commit-row-and-draft"); Checkpoint?.Invoke("before-publication");
        using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
        var result = await index.PublishDirectoryCaptureAsync(draft, publication, expectedLocalSequence, token).ConfigureAwait(false);
        markStage("publication-committed"); Checkpoint?.Invoke("published"); return result;
    }
}
