using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal readonly record struct CaptureRetirementResult(int Deleted, int Missing, int Cancelled, int Deferred, long BytesRemoved, int Visited);

public sealed partial class PublishedBlocks
{
    // Caller retains both maintenance gates and the pinned root. A snapshot
    // may have deferred captures, but every archive/history source must be known.
    internal static async Task<CaptureRetirementResult> RetireCapturesAsync(SafeFileTree tree, FolderIndex index, Guid snapshot, long nowTicks, int limit,
        DiagnosticJournal? journal, Action<string>? checkpoint, CancellationToken token)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        int deleted = 0, missing = 0, cancelled = 0, deferred = 0, visited = 0; long bytes = 0; DirectoryLease? home = null;
        try
        {
            try { home = tree.Directory(BlockCaptureProof.Directory, privatePath: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
            var prior = index.ReadCaptureRetirements().ToArray(); var handled = prior.Select(r => r.Id).ToHashSet();
            foreach (var retirement in prior)
            {
                token.ThrowIfCancellationRequested(); if (visited >= limit) break; visited++;
                try
                {
                    var proof = Proof(retirement); RequireRoot(proof);
                    using var marker = TryMarker(proof); using var parent = TryParent(proof); using var data = TryData(parent, proof);
                    await FinishAsync(retirement, proof, marker, parent, data).ConfigureAwait(false);
                }
                catch (Exception error) when (FileFailure(error)) { deferred++; Log("capture.retirement_deferred", retirement.Id, error); }
                catch (Exception error) { Log("capture.retirement_failed", retirement.Id, error); throw; }
            }
            if (home is not null && visited < limit)
            {
                foreach (var entry in home.Entries(token))
                {
                    token.ThrowIfCancellationRequested(); if (visited >= limit) break;
                    if (entry.IsDirectory || entry.IsReparse || entry.Name.Length != 40 || !entry.Name.EndsWith(".capture", StringComparison.Ordinal)
                        || !Guid.TryParseExact(entry.Name[..^8], "N", out var id) || handled.Contains(id)) continue;
                    try
                    {
                        using var marker = home.OpenFile(entry.Name, mutation: true); var proof = ReadProof(marker, entry.Name, tree.Identity);
                        RequireRoot(proof);
                        if (nowTicks - proof.CapturedTicks < TimeSpan.TicksPerDay) { deferred++; continue; }
                        bool referenced = index.IsCaptureHashReferenced(snapshot, proof.RootId, proof.Hash);
                        using var parent = TryParent(proof); using var data = TryData(parent, proof);
                        if (data is null) { deferred++; continue; } // Ordinary recovery reconciles missing captures.
                        RequireIndependent(data); long length = RandomAccess.GetLength(data);
                        if (!BlockCaptureProof.Identity(data).AsSpan().SequenceEqual(proof.DataId) || length >= proof.Length)
                            throw new InvalidDataException("Capture prefix identity or size changed.");
                        using var replacement = referenced ? TryReplacement(parent, proof, null) : null;
                        if (referenced && replacement is null) { deferred++; continue; }
                        var retirement = new CaptureRetirement(proof.Id, proof.RootId, proof.MarkerId, proof.DataId, proof.Hash, proof.Length,
                            proof.CapturedTicks, checked((int)length), GuardedFile.Hash(data, token), nowTicks, snapshot, replacement is null ? null : BlockCaptureProof.Identity(replacement));
                        visited++;
                        if (!await index.ReserveCaptureRetirementAsync(retirement, token).ConfigureAwait(false)) { deferred++; continue; }
                        Signal("reserved", proof.Id);
                        await FinishAsync(retirement, proof, marker, parent, data, replacement).ConfigureAwait(false);
                    }
                    catch (Exception error) when (FileFailure(error)) { deferred++; Log("capture.retirement_deferred", id, error); }
                    catch (Exception error) { Log("capture.retirement_failed", id, error); throw; }
                }
            }
            Log("capture.retirement_complete", Guid.NewGuid()); return new(deleted, missing, cancelled, deferred, bytes, visited);
        }
        finally { home?.Dispose(); }

        async Task FinishAsync(CaptureRetirement retirement, BlockCaptureProof proof, SafeFileHandle? marker, DirectoryLease? parent, SafeFileHandle? data, SafeFileHandle? retainedReplacement = null)
        {
            if (marker is not null && !ReadProof(marker, proof.Name, tree.Identity).Encode().AsSpan().SequenceEqual(proof.Encode()))
                throw new InvalidDataException("Retiring capture proof changed.");
            using var reopenedReplacement = retainedReplacement is null && retirement.ReplacementId is { } replacementId ? TryReplacement(parent, proof, replacementId) : null;
            var replacement = retainedReplacement ?? reopenedReplacement;
            bool hadData = data is not null;
            if (data is not null)
            {
                RequireIndependent(data);
                if (!BlockCaptureProof.Identity(data).AsSpan().SequenceEqual(proof.DataId) || RandomAccess.GetLength(data) != retirement.PrefixLength
                    || !GuardedFile.Hash(data, token).AsSpan().SequenceEqual(retirement.PrefixHash)) throw new InvalidDataException("Retiring capture prefix changed.");
                bool referenced = index.IsCaptureHashReferenced(snapshot, proof.RootId, proof.Hash);
                if (referenced && replacement is null || nowTicks - proof.CapturedTicks < TimeSpan.TicksPerDay)
                {
                    if (marker is null) { deferred++; return; } // Keep durable ownership if its proof vanished.
                    await index.CancelCaptureRetirementAsync(retirement, snapshot, token).ConfigureAwait(false); cancelled++; Signal("cancelled", proof.Id); return;
                }
                GuardedFile.DeleteOnClose(data); data.Dispose(); Signal("data-deleted", proof.Id);
                ProveAbsent(parent!, proof.Temporary, temporary: true);
            }
            if (marker is not null)
            {
                GuardedFile.DeleteOnClose(marker); marker.Dispose(); Signal("proof-deleted", proof.Id);
                ProveAbsent(home!, proof.Name, temporary: false);
            }
            await index.CompleteProtectedCaptureRetirementAsync(retirement, snapshot, replacement is not null, token).ConfigureAwait(false);
            if (hadData) { deleted++; bytes += retirement.PrefixLength; } else missing++;
            Signal("completed", proof.Id);
        }
        void RequireRoot(BlockCaptureProof proof)
        { if (!proof.RootId.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Capture retirement belongs to another root."); }
        SafeFileHandle? TryMarker(BlockCaptureProof proof)
        {
            if (home is null) return null;
            try { return home.OpenFile(proof.Name, mutation: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        }
        DirectoryLease? TryParent(BlockCaptureProof proof)
        {
            try { return tree.Directory(proof.Parent, privatePath: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        }
        SafeFileHandle? TryReplacement(DirectoryLease? parent, BlockCaptureProof proof, byte[]? expectedId)
        {
            if (parent is null) return null; SafeFileHandle? file = null;
            try
            {
                file = parent.OpenReadGuard(proof.Final); RequireIndependent(file); byte[] id = BlockCaptureProof.Identity(file);
                if (expectedId is not null && !id.AsSpan().SequenceEqual(expectedId) || RandomAccess.GetLength(file) != proof.Length
                    || !GuardedFile.Hash(file, token).AsSpan().SequenceEqual(proof.Hash) || !index.IsCaptureReplacementKnown(snapshot, proof.RootId, proof.Hash, proof.Length, id))
                { file.Dispose(); return null; }
                return file;
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { file?.Dispose(); return null; }
            catch { file?.Dispose(); throw; }
        }
        static SafeFileHandle? TryData(DirectoryLease? parent, BlockCaptureProof proof)
        {
            if (parent is null) return null;
            try { return parent.OpenFile(proof.Temporary, mutation: true, temporary: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        }
        static void ProveAbsent(DirectoryLease parent, string name, bool temporary)
        {
            try { using var unexpected = parent.OpenFile(name, temporary: temporary); throw new IOException("Retired capture path is occupied."); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
        }
        static BlockCaptureProof Proof(CaptureRetirement row) => new(row.Id, row.RootId, row.MarkerId, row.DataId, row.ExpectedHash, row.ExpectedLength, row.CapturedTicks);
        static bool FileFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception;
        void Signal(string stage, Guid id) { Log("capture.retirement_" + stage, id); checkpoint?.Invoke("capture-" + stage); }
        void Log(string name, Guid id, Exception? error = null) => journal?.TryWrite(new(name, "capture-retirement")
        { SessionId = id, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning, ErrorCode = error?.GetType().Name, Exception = error, HResult = error?.HResult,
            NativeError = error switch { Win32Exception native => native.NativeErrorCode, SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null },
            ItemsChanged = deleted + missing + cancelled, ItemsVisited = visited, ItemsDeferred = deferred, Bytes = bytes, RetryInMilliseconds = error is null ? null : 10000 });
    }
}
