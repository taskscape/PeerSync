using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

internal sealed class IndexRangeRecoveryPendingException() : IOException("Sender index range is incomplete before deletion recovery.") { }

/// <summary>The caller already owns the filesystem gate. Keep index receipt
/// available for live transfers; hold references only for a selected deletion.</summary>
internal sealed class ScheduledApplySelection : IDisposable
{
    private IDisposable? references;
    internal LocalFileRow? Known { get; private set; }
    internal NeededDecision Decision { get; private set; } = null!;
    internal bool RangeComplete { get; private set; } = true;
    internal bool HoldsReferences => references is not null;

    internal static async Task<ScheduledApplySelection> AcquireAsync(FolderIndex index, NeededSet needed,
        byte[] ownDevice, string key, bool scheduled, CancellationToken token)
        => await AcquireCoreAsync(index, needed, ownDevice, key, scheduled, false, token).ConfigureAwait(false);

    internal static async Task<ScheduledApplySelection> AcquireDirectoryAsync(FolderIndex index, NeededSet needed,
        byte[] ownDevice, string key, bool scheduled, CancellationToken token)
        => await AcquireCoreAsync(index, needed, ownDevice, key, scheduled, true, token).ConfigureAwait(false);

    private static async Task<ScheduledApplySelection> AcquireCoreAsync(FolderIndex index, NeededSet needed,
        byte[] ownDevice, string key, bool scheduled, bool holdReferences, CancellationToken token)
    {
        var selection = new ScheduledApplySelection();
        try
        {
            Refresh();
            if (holdReferences || scheduled && selection.Decision.Winner is { Deleted: true })
            {
                selection.references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                Refresh();
                if (scheduled && selection.Decision.Winner is { Deleted: true } winner)
                    selection.RangeComplete = winner.SourceDevice.SequenceEqual(ownDevice)
                        || index.ReadRemoteIndexRange(winner.SourceDevice.ToArray())?.Complete == true;
                else if (!holdReferences) selection.Dispose(); // Never retain the reference gate across a live fetch.
            }
            return selection;
        }
        catch { selection.Dispose(); throw; }

        void Refresh()
        {
            selection.Known = index.LocalFiles.Find(key);
            selection.Decision = needed.Decide(new(key, selection.Known, index.RemotesForPath(key).ToArray()));
            bool newDirectory = selection.Decision.Winner is { Type: 1, Deleted: false } && selection.Known is null;
            if (index.HasPendingCasePreservation(key, includeDescendants: !newDirectory)) selection.Decision = selection.Decision with { BlockedReason = "case-preservation-pending" };
            if (BlocksDirectoryPublication(index, ownDevice, key, selection.Known, selection.Decision, scheduled))
                selection.Decision = selection.Decision with { BlockedReason = "directory-publication-pending" };
        }
    }

    internal static bool BlocksDirectoryPublication(FolderIndex index, byte[] ownDevice, string key,
        LocalFileRow? known, NeededDecision decision, bool scheduled)
    {
        if (!index.HasBlockingDirectoryPublication(key, includeDescendants: true)) return false;
        // The parent-root proof itself may be waiting for this exact direct
        // child. A scheduled authenticated live receipt changes the capture
        // epoch before the old parent can publish, breaking that dependency
        // cycle without releasing the barrier for unrelated work or an
        // unpublished target/descendant.
        if (scheduled && decision.BlockedReason is null && decision.RequiresApply
            && decision.Winner is { Deleted: false } live
            && !live.SourceDevice.SequenceEqual(ownDevice)
            && (known is null || known is { Flags: LocalFileState.None, Record: not null })
            && !index.HasBlockingDirectoryPublicationAtOrBelow(key)
            && index.HasUnprovenDirectoryParentMember(key)) return false;
        // Only a fully scheduled, causally newer remote tombstone can pass an
        // unpublished ancestor. Its mutation changes the capture epoch, so the
        // ancestor cannot commit a stale child snapshot. Its own and descendant
        // publications remain barriers, including concurrent local edits.
        return !scheduled || decision.BlockedReason is not null || !decision.RequiresApply
            || known is not { Deleted: false, Flags: LocalFileState.None, Record: not null }
            || decision.Winner is not { Deleted: true } winner
            || winner.SourceDevice.SequenceEqual(ownDevice)
            || VersionVector.Compare(winner.Vector, known.Vector) != VvOrder.Dominates
            || index.HasBlockingDirectoryPublicationAtOrBelow(key);
    }

    internal static bool BlocksDirectoryPublicationDuringRecovery(FolderIndex index, NeededSet needed, byte[] ownDevice,
        VersionView expected, bool scheduled)
    {
        string key = expected.Path;
        if (!index.HasBlockingDirectoryPublication(key, includeDescendants: true)) return false;
        var known = index.LocalFiles.Find(key);
        var decision = needed.Decide(new(PeerSync.Core.Paths.PathRules.Key(key), known, index.RemotesForPath(key).ToArray()));
        return decision.DesiredRecord is null || !decision.DesiredRecord.Body.SequenceEqual(expected.Record.Body)
            || !decision.DesiredRecord.Signature.SequenceEqual(expected.Record.Signature)
            || BlocksDirectoryPublication(index, ownDevice, PeerSync.Core.Paths.PathRules.Key(key), known, decision, scheduled);
    }

    public void Dispose() => Interlocked.Exchange(ref references, null)?.Dispose();

    internal static async ValueTask<IDisposable?> AcquireRecoveryAsync(FolderIndex index, VersionView version,
        byte[] ownDevice, bool required, CancellationToken token)
    {
        if (!required) return null;
        var lease = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
        try
        {
            if (version.SourceDevice.SequenceEqual(ownDevice) || index.ReadRemoteIndexRange(version.SourceDevice.ToArray())?.Complete == true) return lease;
            throw new IndexRangeRecoveryPendingException();
        }
        catch { lease.Dispose(); throw; }
    }
}
