using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>The caller already owns the filesystem gate. Keep index receipt
/// available for live transfers; hold references only for a selected deletion.</summary>
internal sealed class ScheduledApplySelection : IDisposable
{
    private IDisposable? references;
    internal LocalFileRow? Known { get; private set; }
    internal NeededDecision Decision { get; private set; } = null!;
    internal bool RangeComplete { get; private set; } = true;

    internal static async Task<ScheduledApplySelection> AcquireAsync(FolderIndex index, NeededSet needed,
        byte[] ownDevice, string key, bool scheduled, CancellationToken token)
    {
        var selection = new ScheduledApplySelection();
        try
        {
            Refresh();
            if (scheduled && selection.Decision.Winner is { Deleted: true })
            {
                selection.references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                Refresh();
                if (selection.Decision.Winner is { Deleted: true } winner)
                    selection.RangeComplete = winner.SourceDevice.SequenceEqual(ownDevice)
                        || index.ReadRemoteIndexRange(winner.SourceDevice.ToArray())?.Complete == true;
                else selection.Dispose(); // Never retain the reference gate across a live fetch.
            }
            return selection;
        }
        catch { selection.Dispose(); throw; }

        void Refresh()
        {
            selection.Known = index.LocalFiles.Find(key);
            selection.Decision = needed.Decide(new(key, selection.Known, index.RemotesForPath(key).ToArray()));
        }
    }

    public void Dispose() => Interlocked.Exchange(ref references, null)?.Dispose();
}
