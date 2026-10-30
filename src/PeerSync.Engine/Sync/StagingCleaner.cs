using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

// The coordinator calls this only after scan/needed repair. Every destructive
// permission is checked again under the reference gate, after native hashing.
internal sealed class StagingCleaner(string rootPath, FolderIndex index, TimeProvider clock, DiagnosticJournal? journal = null)
{
    internal Action<string>? Checkpoint { get; set; }
    private bool cycleRetry;
    internal bool RetryNeeded { get; private set; }

    internal async Task<int> RecoverAsync(byte[] root, CancellationToken token)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); RequireRoot(tree, root);
        return await index.RecoverStagingRetirementsAsync(root, token).ConfigureAwait(false);
    }

    internal async Task<bool> RunAsync(byte[] root, CancellationToken token)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); RequireRoot(tree, root);
        var page = index.ReadStagingCleanupPage(root); Guid session = Guid.NewGuid(); int removed = 0, deferred = 0;
        foreach (var row in page.Objects)
        {
            token.ThrowIfCancellationRequested();
            long now = clock.GetUtcNow().UtcTicks;
            if (now < row.ParkedTicks || now - row.ParkedTicks < FolderIndex.StagingGraceTicks) continue;
            if (index.Needed.Find(row.TargetPath) is not null || index.ReadApplyIntents().Any()) { deferred++; continue; }
            try
            {
                using var probe = tree.ProbeDirectory(ApplyState.Parent(row.TemporaryPath));
                if (probe.Absent)
                {
                    using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                    if (await index.CompleteMissingStagingAsync(row, now, token).ConfigureAwait(false)) removed++;
                    continue;
                }
                SafeFileHandle handle;
                try
                {
                    handle = row.IsDirectory ? probe.Directory!.OwnedTemporaryDirectory(ApplyState.Name(row.TemporaryPath), create: false)
                        : probe.Directory!.OpenOwnedTemporary(ApplyState.Name(row.TemporaryPath));
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
                {
                    using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                    if (await index.CompleteMissingStagingAsync(row, now, token).ConfigureAwait(false)) removed++;
                    continue;
                }
                using (handle)
                {
                    var snapshot = FileSnapshot.Read(handle);
                    bool matches = BlockCaptureProof.Identity(handle).AsSpan().SequenceEqual(row.FileId)
                        && ((snapshot.Attributes & FileAttributes.Directory) != 0) == row.IsDirectory
                        && snapshot.Length == row.Length && snapshot.Changed == row.ChangeFileTime
                        && (row.IsDirectory ? Empty(handle, token) : PublishedBlocks.IsIndependent(handle)
                            && GuardedFile.Hash(handle, token).AsSpan().SequenceEqual(row.ContentHash));
                    Checkpoint?.Invoke("staging-cleanup-verified"); token.ThrowIfCancellationRequested();
                    using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                    if (!matches)
                    {
                        await index.CancelStagingRetirementAsync(row, protect: true, token).ConfigureAwait(false); deferred++; continue;
                    }
                    if (!await index.ReserveStagingRetirementAsync(row, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false)) { deferred++; continue; }
                    Checkpoint?.Invoke("staging-cleanup-prepared"); token.ThrowIfCancellationRequested();
                    GuardedFile.DeleteOnClose(handle); handle.Dispose();
                    Checkpoint?.Invoke("staging-cleanup-deleted");
                    if (await index.CompleteMissingStagingAsync(row, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false)) removed++;
                }
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
            {
                // A failed native removal never loses the ownership witness.
                await index.CancelStagingRetirementAsync(row, cancellationToken: token).ConfigureAwait(false); deferred++; cycleRetry = true;
                journal?.TryWrite(new("staging.cleanup_deferred", "staging") { SessionId = session, Level = DiagnosticLevel.Debug,
                    ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult, NativeError = error is Win32Exception native ? native.NativeErrorCode : null });
            }
        }
        await index.AdvanceStagingCleanupCursorAsync(root, page.Cursor, page.MoreWork ? page.Objects[^1].Id : null, token).ConfigureAwait(false);
        RetryNeeded = !page.MoreWork && cycleRetry;
        if (!page.MoreWork) cycleRetry = false;
        journal?.TryWrite(new("staging.cleanup_complete", "staging") { SessionId = session, ItemsVisited = page.Objects.Count, ItemsChanged = removed, ItemsDeferred = deferred });
        return page.MoreWork;
    }

    private static bool Empty(SafeFileHandle handle, CancellationToken token)
    { using var lease = new DirectoryLease(handle, []); return !lease.Entries(token).Any(); }
    private static void RequireRoot(SafeFileTree tree, byte[] root)
    { if (!tree.Identity.SequenceEqual(root)) throw new InvalidDataException("Staging root identity changed."); }
}
