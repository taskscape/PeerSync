using PeerSync.Engine.Sync;

namespace PeerSync.Engine.Archive;

/// <summary>Restores as a normal local edit. After success the watcher/caller
/// scans, generating a fresh RW version or an unpublished RO local change.
/// Recover through the same FileApplier before scanning on restart.</summary>
public sealed class ArchiveRestorer
{
    private readonly FileApplier applier;
    public ArchiveRestorer(FileApplier applier)
    {
        ArgumentNullException.ThrowIfNull(applier); this.applier = applier;
    }

    public Task<FileApplyOutcome> RestoreAsync(long archiveId, CancellationToken cancellationToken = default) =>
        Task.Run(() => applier.RestoreArchiveAsync(archiveId, null, copyOnly: false, cancellationToken), CancellationToken.None);

    /// <summary>The explicit copy destination is never overwritten.</summary>
    public Task<FileApplyOutcome> RestoreAsCopyAsync(long archiveId, string destination, CancellationToken cancellationToken = default) =>
        Task.Run(() => applier.RestoreArchiveAsync(archiveId, destination, copyOnly: true, cancellationToken), CancellationToken.None);
}
