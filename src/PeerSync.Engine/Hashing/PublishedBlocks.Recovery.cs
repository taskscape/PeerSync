using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal readonly record struct BlockRecoveryResult(int Registered, int Installed, int ProofsRemoved, int Deferred);

public sealed partial class PublishedBlocks
{
    // Caller holds the shared folder gate. No incomplete blob is discarded here.
    internal static async Task<BlockRecoveryResult> RecoverCapturesAsync(SafeFileTree tree, FolderIndex index,
        DiagnosticJournal? journal = null, CancellationToken cancellationToken = default)
    {
        int registered = 0, installed = 0, removed = 0, deferred = 0;
        DirectoryLease home;
        try { home = tree.Directory(BlockCaptureProof.Directory, privatePath: true); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return default; }
        using (home)
        {
            // Proofs have their own directory: warm scans do not enumerate every blob.
            var names = home.Entries(cancellationToken).Where(e => !e.IsDirectory && !e.IsReparse && e.Name.EndsWith(".capture", StringComparison.Ordinal)).Select(e => e.Name).ToArray();
            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested(); Guid session = Guid.NewGuid();
                if (name.Length == 40 && Guid.TryParseExact(name[..^8], "N", out var capture) && index.HasCaptureRetirement(capture)) { deferred++; continue; }
                try
                {
                    using var marker = home.OpenFile(name, mutation: true);
                    var proof = ReadProof(marker, name, tree.Identity); session = proof.Id;
                    using var parent = tree.Directory(proof.Parent, privatePath: true);
                    SafeFileHandle? final = null;
                    try
                    {
                        try { final = parent.OpenReadGuard(proof.Final); }
                        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
                        if (final is not null && BlockCaptureProof.Identity(final).AsSpan().SequenceEqual(proof.DataId))
                        {
                            if (!Complete(final, proof, cancellationToken)) throw new IOException("Installed capture changed.");
                            await RegisterAsync(proof, index, cancellationToken).ConfigureAwait(false); registered++;
                            GuardedFile.DeleteOnClose(marker); removed++; continue;
                        }
                        using var data = TryTemporary(parent, proof, out bool replaced);
                        if (data is null)
                        {
                            if (!replaced && final is not null
                                && index.IsRegisteredBlockObject(proof.Hash, proof.Length, proof.RootId, BlockCaptureProof.Identity(final))
                                && Complete(final, proof, cancellationToken))
                            {
                                // No owned staging bytes remain. A different,
                                // verified and durably owned winner needs no
                                // adoption; retire only this obsolete metadata.
                                GuardedFile.DeleteOnClose(marker); removed++;
                                journal?.TryWrite(new("block.recovery_obsolete_proof", "block-recovery")
                                { SessionId = proof.Id, Level = DiagnosticLevel.Debug, ErrorCode = "cataloged-replacement", ItemsChanged = 1 });
                                continue;
                            }
                            if (final is not null || replaced) { deferred++; continue; }
                            // Remove only verified metadata. Unknown/replaced data stays untouched.
                            GuardedFile.DeleteOnClose(marker); removed++; continue;
                        }
                        if (!Complete(data, proof, cancellationToken) || final is not null) { deferred++; continue; }
                        RandomAccess.FlushToDisk(data); parent.MoveHere(data, proof.Final); RandomAccess.FlushToDisk(data); installed++;
                        await RegisterAsync(proof, index, cancellationToken).ConfigureAwait(false); registered++;
                        GuardedFile.DeleteOnClose(marker); removed++;
                    }
                    finally { final?.Dispose(); }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
                {
                    deferred++;
                    journal?.TryWrite(new("block.recovery_failed", "block-recovery") { SessionId = session, Level = DiagnosticLevel.Warning,
                        ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = error is Win32Exception native ? native.NativeErrorCode : null,
                        RetryInMilliseconds = 10000 });
                }
            }
        }
        journal?.TryWrite(new("block.recovery_complete", "block-recovery") { Level = DiagnosticLevel.Debug,
            ItemsChanged = registered, ItemsVisited = installed + removed, ItemsDeferred = deferred });
        return new(registered, installed, removed, deferred);

        static bool Complete(SafeFileHandle data, BlockCaptureProof proof, CancellationToken token) => IsIndependent(data)
            && RandomAccess.GetLength(data) == proof.Length && GuardedFile.Hash(data, token).AsSpan().SequenceEqual(proof.Hash);
        async Task RegisterAsync(BlockCaptureProof proof, FolderIndex folderIndex, CancellationToken token)
        {
            try
            {
                await folderIndex.RegisterBlockObjectsAsync([new(proof.Hash, proof.Length, proof.RootId, proof.DataId, proof.CapturedTicks,
                    Math.Max(proof.CapturedTicks, DateTime.UtcNow.Ticks))], token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                journal?.TryWrite(new("block.recovery_catalog_failed", "block-recovery") { SessionId = proof.Id, Level = DiagnosticLevel.Error,
                    ErrorCode = error is OperationCanceledException ? "cancelled" : error.GetType().Name, HResult = error.HResult,
                    NativeError = error is SqliteException sqlite ? sqlite.SqliteExtendedErrorCode : null, RetryInMilliseconds = 10000 });
                throw;
            }
        }
        static SafeFileHandle? TryTemporary(DirectoryLease parent, BlockCaptureProof proof, out bool replaced)
        {
            SafeFileHandle? file = null; replaced = false;
            try
            {
                file = parent.OpenOwnedTemporary(proof.Temporary);
                if (BlockCaptureProof.Identity(file).AsSpan().SequenceEqual(proof.DataId)) return file;
                replaced = true; file.Dispose(); return null;
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { file?.Dispose(); return null; }
            catch { file?.Dispose(); throw; }
        }
    }
}
