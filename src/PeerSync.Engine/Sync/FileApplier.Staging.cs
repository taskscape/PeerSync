using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    private SafeFileHandle OpenTemporary(SafeFileTree tree, DirectoryLease parent, string path, out string temporary, out byte[] identity, bool directory = false, Guid session = default, CancellationToken cancellationToken = default)
    {
        foreach (var row in index.ReadReusableStaging(tree.Identity.ToArray(), path, directory))
        {
            SafeFileHandle? handle = null; bool transferred = false;
            try
            {
                handle = directory ? parent.OwnedTemporaryDirectory(ApplyState.Name(row.TemporaryPath), create: false) : parent.OpenOwnedTemporary(ApplyState.Name(row.TemporaryPath));
                Signal("staging-reuse-verifying", session); cancellationToken.ThrowIfCancellationRequested();
                var snapshot = FileSnapshot.Read(handle);
                bool matches = HasIdentity(handle, row.FileId) && ((snapshot.Attributes & FileAttributes.Directory) != 0) == directory
                    && snapshot.Length == row.Length && snapshot.Changed == row.ChangeFileTime
                    && (directory || PublishedBlocks.IsIndependent(handle) && GuardedFile.Hash(handle, cancellationToken).AsSpan().SequenceEqual(row.ContentHash));
                if (matches)
                {
                    // The catalog retains its original spelling/age. A new
                    // strict intent must use the current target parent spelling.
                    temporary = ApplyState.Join(ApplyState.Parent(path), ApplyState.Name(row.TemporaryPath));
                    identity = row.FileId.ToArray(); transferred = true; return handle;
                }
                Signal("staging-reuse-rejected", session);
            }
            catch (Exception error) when (error is IOException or Win32Exception) { StagingProbeFailed("staging-reuse-skipped", session, error); }
            finally { if (!transferred) handle?.Dispose(); }
        }
        // Legacy pointers have ownership but no trustworthy age/fingerprint.
        // Once cataloged, they must not bypass protected/changed catalog state.
        if (index.Meta.Find(StagingKey(path, directory))?.Value is { Length: > 40 and <= 17448 } parked && parked.AsSpan(0, 24).SequenceEqual(tree.Identity))
        {
            string? candidate = null;
            try { candidate = new UTF8Encoding(false, true).GetString(parked.AsSpan(40)); }
            catch (DecoderFallbackException error) { StagingProbeFailed("staging-legacy-invalid",session,error); }
            if (candidate is not null && ApplyState.Parent(candidate) == ApplyState.Parent(path) && NameIsTemporary(candidate)
                && !index.IsCatalogedStaging(tree.Identity.ToArray(), parked.AsSpan(24, 16).ToArray(), candidate))
            {
                SafeFileHandle? handle = null; bool transferred = false;
                try
                {
                    handle = directory ? parent.OwnedTemporaryDirectory(ApplyState.Name(candidate), create: false) : parent.OpenOwnedTemporary(ApplyState.Name(candidate));
                    if (HasIdentity(handle, parked.AsSpan(24, 16)) && (directory || PublishedBlocks.IsIndependent(handle)))
                    { temporary = candidate; identity = parked.AsSpan(24, 16).ToArray(); transferred = true; return handle; }
                }
                catch (Exception error) when (error is IOException or Win32Exception) { StagingProbeFailed("staging-legacy-skipped", session, error); }
                finally { if (!transferred) handle?.Dispose(); }
            }
        }
        string name = Guid.NewGuid().ToString("N") + ".!psp";
        var created = directory ? parent.OwnedTemporaryDirectory(name, create: true) : parent.CreateFile(name, temporary: true);
        temporary = ApplyState.Join(ApplyState.Parent(path), name); identity = Identity(FileSnapshot.Read(created)); return created;
    }

    private async Task ParkAsync(SafeFileTree tree, ApplyState state, ApplyIntent intent, CancellationToken token, SafeFileHandle? heldTemporary = null)
    {
        if (state.Temporary.Length == 0) return;
        StagedObject? row = null;
        try
        {
            using var parent = heldTemporary is null ? tree.Directory(ApplyState.Parent(state.Temporary)) : null;
            using var reopened = heldTemporary is null
                ? state.Type == 1 ? parent!.OwnedTemporaryDirectory(ApplyState.Name(state.Temporary), create: false) : parent!.OpenOwnedTemporary(ApplyState.Name(state.Temporary)) : null;
            var handle = heldTemporary ?? reopened!; var snapshot = FileSnapshot.Read(handle);
            bool directory = (snapshot.Attributes & FileAttributes.Directory) != 0;
            if (HasIdentity(handle, state.TemporaryId) && directory == (state.Type == 1) && (directory || PublishedBlocks.IsIndependent(handle)))
            {
                byte[]? hash = directory ? null : GuardedFile.Hash(handle, token);
                bool changedPrepared = state.Prepared && (snapshot.Length != state.Size || (uint)snapshot.Attributes != state.TemporaryAttributes
                    || snapshot.LastWrite != state.MtimeTicks - DateTime.FromFileTimeUtc(0).Ticks
                    || directory && !IsEmptyStagingDirectory(handle, token)
                    || !directory && !hash!.AsSpan().SequenceEqual(state.FileHash));
                row = new(Guid.NewGuid(), state.Root, state.TemporaryId, state.Path, state.Temporary, directory, clock.GetUtcNow().UtcTicks,
                    snapshot.Length, hash, snapshot.Changed, changedPrepared ? StagingObjectState.Protected : StagingObjectState.Parked);
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception) { StagingProbeFailed("staging-catalog-deferred", intent.Id, error); }
        if (row is not null)
        {
            await index.ParkStagingAsync(intent, row, token).ConfigureAwait(false);
            Signal(row.State==StagingObjectState.Protected?"staging-protected":"staging-cataloged", intent.Id);
        }
        else
        {
            // Preserve the old safe reuse witness; it confers no cleanup permission.
            await index.Meta.UpsertAsync([new(StagingKey(state.Path, state.Type == 1), [.. state.Root, .. state.TemporaryId, .. Encoding.UTF8.GetBytes(state.Temporary)])], token).ConfigureAwait(false);
        }
    }

    private static string StagingKey(string path, bool directory = false) => "_apply-staging-" + Convert.ToHexString(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(PathRules.Key(path))).AsSpan())
        + (directory ? "-directory" : "");
    private static bool NameIsTemporary(string path) => ApplyState.Name(path).EndsWith(".!psp", StringComparison.Ordinal) && Guid.TryParseExact(ApplyState.Name(path)[..^5], "N", out _);
    private static bool IsEmptyStagingDirectory(SafeFileHandle handle, CancellationToken token)
    { using var lease = new DirectoryLease(handle, []); return !lease.Entries(token).Any(); }
    private void StagingProbeFailed(string stage,Guid session,Exception error)
    {
        journal?.TryWrite(new("apply."+stage,"staging") { SessionId=session,Level=DiagnosticLevel.Debug,ErrorCode=error.GetType().Name,HResult=error.HResult,
            NativeError=error is Win32Exception native?native.NativeErrorCode:null });
        Checkpoint?.Invoke(stage);
    }
}
