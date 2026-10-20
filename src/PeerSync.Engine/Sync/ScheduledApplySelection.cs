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
            if (index.HasBlockingDirectoryPublication(key, includeDescendants: true)) selection.Decision = selection.Decision with { BlockedReason = "directory-publication-pending" };
        }
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
