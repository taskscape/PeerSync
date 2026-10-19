using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Scanning;

public sealed partial class Scanner
{
    // Scanner owns the folder gate and has already committed file versions and
    // tombstones. Queue staging never replaces an advertised directory row.
    private async Task StageDirectoriesAsync(byte[] root, Dictionary<string, WalkEntry> entries, HashSet<string> protectedPrefixes,
        HashSet<string> ignoredPrefixes, IEnumerable<ScanProblem> problems, Guid scanSession, CancellationToken token)
    {
        long now = clock.GetUtcNow().UtcTicks;
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var problem in problems.Where(problem => problem.Code is "enumeration-failed" or "case-collision"))
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
        var children = entries.Where(pair => !pair.Value.Unsyncable && !Under(pair.Key, protectedPrefixes))
            .GroupBy(pair => ParentKey(pair.Key), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var pair in entries.Where(pair => pair.Value.Native.IsDirectory && !pair.Value.Unsyncable).OrderByDescending(pair => pair.Key.Count(value => value == '/')))
        {
            token.ThrowIfCancellationRequested(); string key = pair.Key; var entry = pair.Value;
            if (index.HasIncompleteDirectoryCaseCopy(entry.Path)) { Log("case_copy_publication_held", scanSession, "members-pending"); continue; }
            if (uncertain.Contains("") || uncertain.Contains(key) || Under(key, protectedPrefixes)) continue;
            var old = index.LocalFiles.Find(entry.Path);
            bool sameNative = old is { Type: 1, Deleted: false, Flags: LocalFileState.None } && MetadataEqual(old, entry);
            // A received nonempty root can describe children not yet materialized.
            // Do not turn their temporary absence into a newly signed local root.
            if (old?.Record is not null)
            {
                var received = VersionView.Open(old.Record, keys, device);
                if (received.Type == 1 && !received.Deleted && !received.ModifiedBy.SequenceEqual(device)
                    && received.ExportBody().DirectorySnapshotHash.Length != 0 && !CacheMatchesIdentity(old, root, entry, null, null))
                { Log("received_root_pending", scanSession, "incoming-graph-required"); continue; }
            }
            using var fingerprint = new DirectoryChildFingerprint(entry.Path);
            if (children.TryGetValue(key, out var names)) foreach (string name in names) fingerprint.Add(name);
            var inventory = fingerprint.Complete(index.ReadDirectoryCaptureEpoch());
            if (sameNative && old?.Record is not null && CacheMatchesIdentity(old, root, entry, inventory, inventory.Epoch))
            { Log("snapshot_cache_hit", scanSession, "validated-input"); continue; }
            var existing = index.ReadDirectoryPublication(entry.Path);
            bool sameQueuedNative = existing is not null && existing.Path == entry.Path && existing.ExpectedLocalSequence == (old?.LocalSequence ?? 0)
                && existing.Attributes == (uint)entry.Native.Attributes && existing.NativeId.AsSpan().SequenceEqual(entry.Native.FileId) && existing.RootId.AsSpan().SequenceEqual(root);
            // A newly staged directory has no published row yet. Its incidental
            // child-write timestamp can still settle after the first walk; keep
            // the original staged mtime just as for an existing published row.
            long mtime = sameNative ? old!.MtimeTicks : sameQueuedNative ? VersionView.Open(existing!.Template, keys, device).MtimeTicks : entry.Native.MtimeTicks;
            if (existing is not null && existing.Path == entry.Path && existing.ExpectedLocalSequence == (old?.LocalSequence ?? 0)
                && existing.Attributes == (uint)entry.Native.Attributes && existing.NativeId.AsSpan().SequenceEqual(entry.Native.FileId)
                && existing.RootId.AsSpan().SequenceEqual(root) && existing.ExpectedChildren == inventory.Count
                && existing.ExpectedKeysHash.AsSpan().SequenceEqual(inventory.Hash)
                && VersionView.Open(existing.Template, keys, device).MtimeTicks == mtime)
            {
                var touched = await index.StageDirectoryPublicationAsync(existing with { TouchedTicks = now }, token).ConfigureAwait(false);
                if (touched.Paused) await index.PauseDirectoryPublicationAsync(touched, false, now, token).ConfigureAwait(false);
                Log("queue_retained", touched.Version, "stable-template");
                continue;
            }
            var row = Metadata(entry, old, LocalFileState.None) with { MtimeTicks = mtime, Vector = VersionVector.Increment(old?.Vector ?? EmptyVector, deviceShort) };
            row = row with { Record = Sign(row, 0, []) };
            var staged = await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), entry.Path, root, entry.Native.FileId, old?.LocalSequence ?? 0,
                row.Attributes, row.Record!, inventory.Hash, inventory.Count, now, now), token).ConfigureAwait(false);
            Log("queue_staged", staged.Version, "complete-inventory");
        }
        bool CacheMatchesIdentity(LocalFileRow row, byte[] identity, WalkEntry observed, DirectoryChildInventory? inventory, long? epoch)
        {
            var cache = index.ReadDirectoryPublicationCache(row.Path);
            return cache is not null && row.Record is not null && cache.RootId.AsSpan().SequenceEqual(identity)
                && cache.NativeId.AsSpan().SequenceEqual(observed.Native.FileId)
                && cache.RecordSourceId.AsSpan().SequenceEqual(CaseConflictPolicy.SourceId(VersionView.Open(row.Record, keys, device)))
                && (epoch is null || cache.InputEpoch == epoch)
                && (inventory is null || cache.MemberCount == inventory.Count && cache.ExpectedKeysHash.AsSpan().SequenceEqual(inventory.Hash));
        }
        static string ParentKey(string key) { int separator = key.LastIndexOf('/'); return separator < 0 ? "" : key[..separator]; }
        void Log(string kind, Guid session, string reason) => journal?.TryWrite(new("directory." + kind, "directory-scan")
            { SessionId = session, Level = DiagnosticLevel.Debug, ErrorCode = reason, ItemsVisited = 1 });
    }
}
