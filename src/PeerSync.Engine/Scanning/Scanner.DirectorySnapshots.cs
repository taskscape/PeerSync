using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Scanning;

public sealed partial class Scanner
{
    // Scanner owns the folder gate and has already committed file versions and
    // tombstones. Queue staging never replaces an advertised directory row.
    private async Task StageDirectoriesAsync(byte[] root, ScanObservations entries, HashSet<string> protectedPrefixes,
        HashSet<string> ignoredPrefixes, IEnumerable<ScanProblem> problems, Guid scanSession, CancellationToken token)
    {
        long now = clock.GetUtcNow().UtcTicks;
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var problem in problems.Where(problem => problem.Code is "enumeration-failed" or "case-collision" or "read-failed" or "changed-during-read" or "published-content-verification-failed"))
        {
            string path = problem.RelativePath.ToUpperInvariant(); uncertain.Add(path);
            for (int separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/'))
            { path = path[..separator]; uncertain.Add(path); }
            if (problem.RelativePath.Length == 0) uncertain.Add("");
        }
        // Include paused/backed-off work; due-only reads cannot reconcile stale
        // namespace barriers. Path-only pages avoid loading all signed templates.
        string? cursor = null;
        for (;;)
        {
            var paths = index.ReadDirectoryPublicationPaths(cursor); if (paths.Count == 0) break;
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested(); var queued = index.ReadDirectoryPublication(path)!;
                bool unknown = uncertain.Contains("") || uncertain.Contains(path) || Under(path, protectedPrefixes) || index.HasIncompleteDirectoryCaseCopy(path);
                if (unknown) { await index.PauseDirectoryPublicationAsync(queued, true, now, token).ConfigureAwait(false); Log("queue_paused", queued.Version, "incomplete-walk"); continue; }
                if (!keys.CanWrite || (index.LocalFiles.Find(path)?.LocalSequence ?? 0) != queued.ExpectedLocalSequence
                    || Under(path, ignoredPrefixes) || !entries.TryGetValue(path, out var found) || found.Unsyncable || !found.Native.IsDirectory)
                {
                    await index.DiscardDirectoryPublicationAsync(queued, token).ConfigureAwait(false);
                    Log("queue_discarded", queued.Version, "complete-walk");
                }
            }
            cursor = paths[^1]; if (paths.Count < 1000) break;
        }
        if (!keys.CanWrite) return;
        // Keep the common remote-peer path bounded even when its remote table
        // contains a very large corpus. A late arrival is checked again below.
        bool hasExternalState = index.RemoteFiles.Count() != 0 || index.Needed.Count() != 0;
        foreach (var pair in entries.ReadDirectoriesDeepestFirst())
        {
            token.ThrowIfCancellationRequested(); string key = pair.Key; var entry = pair.Value;
            if (entry.Unsyncable) continue;
            if (index.HasIncompleteDirectoryCaseCopy(entry.Path)) { Log("case_copy_publication_held", scanSession, "members-pending"); continue; }
            if (uncertain.Contains("") || uncertain.Contains(key) || Under(key, protectedPrefixes)) continue;
            var old = index.LocalFiles.Find(entry.Path);
            bool sameNative = old is { Type: 1, Deleted: false, Flags: LocalFileState.None } && MetadataEqual(old, entry);
            // Reconstruct the exact received root without blocking missing child
            // arrival. A mismatch cannot authorize a new local snapshot.
            bool reconstruct = false;
            if (old?.Record is not null)
            {
                var received = VersionView.Open(old.Record, keys, device);
                if (received.Type == 1 && !received.Deleted && !received.ModifiedBy.SequenceEqual(device)
                    && received.ExportBody().DirectorySnapshotHash.Length != 0 && !CacheMatchesIdentity(old, root, entry, null, null, false))
                { reconstruct = true; Log("received_root_pending", scanSession, "exact-reconstruction-required"); }
            }
            using var fingerprint = new DirectoryChildFingerprint(entry.Path);
            bool hasDirectoryChild = false;
            foreach (var child in entries.ReadChildren(key))
            {
                if (child.Unsyncable || Under(child.Key, protectedPrefixes)) continue;
                fingerprint.Add(child.Key); hasDirectoryChild |= child.IsDirectory;
            }
            var inventory = fingerprint.Complete(index.ReadDirectoryCaptureEpoch());
            if (sameNative && old?.Record is not null && CacheMatchesIdentity(old, root, entry, inventory, inventory.Epoch, hasDirectoryChild))
            { Log("snapshot_cache_hit", scanSession, "validated-input"); continue; }
            var existing = index.ReadDirectoryPublication(entry.Path);
            bool sameQueuedNative = existing is not null && existing.Path == entry.Path && existing.ExpectedLocalSequence == (old?.LocalSequence ?? 0)
                && existing.Attributes == (uint)entry.Native.Attributes && existing.NativeId.AsSpan().SequenceEqual(entry.Native.FileId) && existing.RootId.AsSpan().SequenceEqual(root);
            // A newly staged directory has no published row yet. Its incidental
            // child-write timestamp can still settle after the first walk; keep
            // the original staged mtime just as for an existing published row.
            long mtime = sameNative ? old!.MtimeTicks : sameQueuedNative ? VersionView.Open(existing!.Template, keys, device).MtimeTicks : ObservedMtimeTicks(entry, old);
            if (existing is not null && existing.Path == entry.Path && existing.ExpectedLocalSequence == (old?.LocalSequence ?? 0)
                && existing.Attributes == (uint)entry.Native.Attributes && existing.NativeId.AsSpan().SequenceEqual(entry.Native.FileId)
                && existing.RootId.AsSpan().SequenceEqual(root) && existing.ExpectedChildren == inventory.Count && existing.ReconstructionOnly == reconstruct
                && existing.ExpectedKeysHash.AsSpan().SequenceEqual(inventory.Hash)
                && VersionView.Open(existing.Template, keys, device).MtimeTicks == mtime)
            {
                var touched = await index.StageDirectoryPublicationAsync(existing with { TouchedTicks = now }, token).ConfigureAwait(false);
                if (touched.Paused) await index.PauseDirectoryPublicationAsync(touched, false, now, token).ConfigureAwait(false);
                Log("queue_retained", touched.Version, "stable-template");
                continue;
            }
            var row = reconstruct ? old! : Metadata(entry, old, LocalFileState.None) with { MtimeTicks = mtime, Vector = VersionVector.Increment(old?.Vector ?? EmptyVector, deviceShort) };
            if (!reconstruct) row = row with { Record = Sign(row, 0, []) };
            var staged = await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), entry.Path, root, entry.Native.FileId, old?.LocalSequence ?? 0,
                (uint)entry.Native.Attributes, row.Record!, inventory.Hash, inventory.Count, now, now) { ReconstructionOnly = reconstruct }, token).ConfigureAwait(false);
            Log("queue_staged", staged.Version, "complete-inventory");
        }
        bool CacheMatchesIdentity(LocalFileRow row, byte[] identity, WalkEntry observed, DirectoryChildInventory? inventory, long? epoch,
            bool hasDirectoryChild)
        {
            var cache = index.ReadDirectoryPublicationCache(row.Path);
            if (cache is null || row.Record is null || !cache.RootId.AsSpan().SequenceEqual(identity)
                || !cache.NativeId.AsSpan().SequenceEqual(observed.Native.FileId)) return false;
            var currentVersion = VersionView.Open(row.Record, keys, device);
            if (!cache.RecordSourceId.AsSpan().SequenceEqual(CaseConflictPolicy.SourceId(currentVersion))) return false;
            if (inventory is null || epoch is null) return true;
            if (cache.MemberCount != inventory.Count || !cache.ExpectedKeysHash.AsSpan().SequenceEqual(inventory.Hash)) return false;
            if (cache.InputEpoch == epoch) return true;
            // A global epoch change may come from another directory. Only a
            // local-only leaf can safely validate its exact signed input here;
            // remote and nested causal work keeps the conservative restage.
            if (!currentVersion.ModifiedBy.SequenceEqual(device)
                || currentVersion.Vector.Keys.Any(source => source != deviceShort)
                || hasExternalState
                || index.HasPendingChildDirectoryPublication(row.Path)
                || hasDirectoryChild) return false;
            bool validated = SameSignedLeafInput(row.Path, cache.InputHash, cache.MemberCount)
                && index.ReadDirectoryCaptureEpoch() == epoch
                && index.RemoteFiles.Count() == 0 && index.Needed.Count() == 0;
            if (validated) journal?.TryWrite(new("directory.signed_leaf_cache_hit", "directory-scan")
                { SessionId = scanSession, Level = DiagnosticLevel.Debug, ItemsVisited = cache.MemberCount });
            return validated;
        }
        bool SameSignedLeafInput(string parent, byte[] expectedHash, long expectedCount)
        {
            using var digest = Blake3.Hasher.New();
            digest.Update("PeerSync directory capture input v1"u8);
            byte[] path = Encoding.UTF8.GetBytes(parent); Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, path.Length); digest.Update(length); digest.Update(path);
            string? cursor = null; long count = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                var batch = index.ReadDirectorySnapshotChildren(parent, cursor);
                foreach (var child in batch.Rows)
                {
                    if (child.Record is null || child.Deleted || child.Type != 0) return false;
                    var member = VersionView.Open(child.Record, keys, device);
                    string key = PathRules.Key(member.Path);
                    if (member.Deleted || member.Type != 0 || ApplyState.Parent(key) != PathRules.Key(parent)
                        || key != PathRules.Key(child.Path) || child.Size != member.Size
                        || child.FileHash is null || !member.FileHash.SequenceEqual(child.FileHash)
                        || !member.ModifiedBy.SequenceEqual(device)
                        || member.Vector.Keys.Any(source => source != deviceShort)
                        || cursor is not null && StringComparer.Ordinal.Compare(cursor, key) >= 0) return false;
                    digest.Update(CaseConflictPolicy.SourceId(member)); cursor = key;
                    count = checked(count + 1);
                }
                if (batch.More && (batch.Rows.Count == 0 || batch.NextKey != cursor)) return false;
                if (!batch.More) break;
            } while (true);
            Span<byte> countBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(countBytes, count); digest.Update(countBytes);
            return count == expectedCount && digest.Finalize().AsSpan().SequenceEqual(expectedHash);
        }
        void Log(string kind, Guid session, string reason) => journal?.TryWrite(new("directory." + kind, "directory-scan")
            { SessionId = session, Level = DiagnosticLevel.Debug, ErrorCode = reason, ItemsVisited = 1 });
    }
}
