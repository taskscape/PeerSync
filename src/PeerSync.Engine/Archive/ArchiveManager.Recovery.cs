using System.ComponentModel;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

public sealed partial class ArchiveManager
{
    private async Task<(int Removed, int Missing, int Deferred, long Bytes)> RecoverRetirementsAsync(SafeFileTree tree, CancellationToken token)
    {
        int removed = 0, missing = 0, deferred = 0; long bytes = 0;
        foreach (var witness in index.ReadArchiveRetirements().Where(r => r.CompletedTicks is null).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var row = witness.Archive;
            try
            {
                if (witness.Id == Guid.Empty || !witness.RootId.AsSpan().SequenceEqual(tree.Identity) || witness.FileId.Length != 16
                    || row.FileHash is not { Length: 32 } || row.Size < 0 || witness.PreparedTicks <= 0 || witness.PreparedTicks > DateTime.MaxValue.Ticks
                    || !Enum.IsDefined(witness.Reason)) throw new IOException("Invalid archive retirement ownership.");
                var current = index.Archive.Find(row.Id);
                if (current is not null && !SameArchive(current, row)) throw new IOException("Archive row changed during policy recovery.");
                bool absent = false, deleted = false;
                try
                {
                    using var parent = Parent(tree, row);
                    using (var file = parent.OpenFile(ApplyState.Name(row.ArchivePath), mutation: true))
                    {
                        // An absent row alone does not authorize deleting a file
                        // which reappeared at its former location.
                        if (current is null || !BlockCaptureProof.Identity(file).AsSpan().SequenceEqual(witness.FileId))
                            throw new IOException("Archive retirement target changed.");
                        PublishedBlocks.RequireIndependent(file);
                        if (FileSnapshot.Read(file).Length != row.Size || !GuardedFile.Hash(file, token).AsSpan().SequenceEqual(row.FileHash))
                            throw new IOException("Archive retirement content changed.");
                        token.ThrowIfCancellationRequested(); Checkpoint?.Invoke("before-delete");
                        token.ThrowIfCancellationRequested(); GuardedFile.DeleteOnClose(file);
                    }
                    Checkpoint?.Invoke("data-deleted"); deleted = true;
                    try { using var stillThere = parent.OpenFile(ApplyState.Name(row.ArchivePath)); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { absent = true; }
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { absent = true; }
                if (!absent) throw new IOException("Archive deletion is pending or its path is occupied.");
                Checkpoint?.Invoke("before-retirement-commit");
                await index.CompleteArchiveRetirementAsync(witness, Math.Max(witness.PreparedTicks, clock.GetUtcNow().UtcTicks), token).ConfigureAwait(false);
                Log("archive.retirement_recovered", witness.Id, bytes: row.Size); Checkpoint?.Invoke("retirement-committed");
                if (deleted) { removed++; bytes = checked(bytes + row.Size); } else missing++;
            }
            catch (Exception error) when (IsFileError(error))
            { deferred++; Log("archive.retirement_deferred", witness.Id, error, "retirement-object-unavailable", retry: TimeSpan.FromSeconds(10)); }
        }
        return (removed, missing, deferred, bytes);
    }
    private static bool SameArchive(ArchiveRow a, ArchiveRow b) => a.Id == b.Id && a.OriginalPath == b.OriginalPath && a.ArchivePath == b.ArchivePath
        && a.ArchivedTicks == b.ArchivedTicks && a.Size == b.Size && a.FileHash is not null && b.FileHash is not null && a.FileHash.AsSpan().SequenceEqual(b.FileHash);
}
