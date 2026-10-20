using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    // A newer tombstone may be the causal input a child reconstruction needs.
    // This exception changes only an already-deleted regular-file row; it never
    // mutates native content or releases the staged ancestor's publication hold.
    private async Task<bool> TryApplyAbsentTombstoneAsync(SafeFileTree tree, ScheduledApplySelection selection,
        string key, Guid session, CancellationToken token)
    {
        if (!selection.HoldsReferences || !selection.RangeComplete
            || selection.Known is not { Type: 0, Deleted: true, Flags: LocalFileState.None, Record: not null } known
            || index.HasPendingCasePreservation(key, includeDescendants: true)) return false;
        // Selection adds the publication barrier after ordinary policy. Recheck
        // that policy so this exception cannot mask ignored/otherwise blocked work.
        var decision = needed.Decide(new(key, known, index.RemotesForPath(key).ToArray()));
        if (decision.BlockedReason is not null || !decision.RequiresApply
            || decision.Winner is not { Type: 0, Deleted: true } winner || decision.DesiredRecord is null) return false;
        var desired = VersionView.Open(decision.DesiredRecord, keys, winner.SourceDevice.ToArray());
        if (!desired.Deleted || desired.Type != 0 || !Absent()) return false;
        var state = new ApplyState { Root = tree.Identity.ToArray(), Version = desired, Vector = decision.Vector,
            Prepared = true, ScheduledDeletion = true };
        var intent = new ApplyIntent(session, key, known.LocalSequence, state.Encode());
        await index.ReserveApplyAsync(intent, token).ConfigureAwait(false);
        Signal("prepared", session);
        if (!Absent())
        {
            await index.AbandonApplyAsync(intent, token).ConfigureAwait(false);
            Signal("tombstone-target-changed", session); return false;
        }
        await CompleteAsync(tree, state, intent, null, null, token).ConfigureAwait(false);
        Signal("tombstone-clock-applied", session); return true;

        bool Absent()
        {
            token.ThrowIfCancellationRequested();
            using var parent = tree.ProbeDirectory(ApplyState.Parent(desired.Path));
            if (parent.Absent) return true;
            using var node = TryOpen(parent.Directory!, ApplyState.Name(desired.Path));
            return node is null;
        }
    }
}
