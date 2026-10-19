using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

public sealed record BlockReferenceRefreshResult(Guid? SnapshotId, long References, int Batches,
    long ArchivesWithoutManifest, int CaptureDeferrals, bool WaitingForRecovery = false)
{
    /// <summary>Coverage of sources known at refresh time only. This is not
    /// deletion authorization: cleanup must also establish archive/migration
    /// provenance and refresh while holding both gates through its sweep.</summary>
    public bool AllKnownSourcesCovered => SnapshotId is not null && !WaitingForRecovery && ArchivesWithoutManifest == 0 && CaptureDeferrals == 0;
}

/// <summary>Host owns the bound index and keys. Every production reference
/// mutation shares the folder gate; this builder does not delete any blob.</summary>
public sealed class BlockReferences
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] device;
    private readonly DiagnosticJournal? journal;
    internal Action<string>? Checkpoint { get; init; }

    public BlockReferences(string rootPath, FolderIndex index, FolderKeys keys, byte[] device, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(device);
        if (device.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(device));
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys; this.device = device.ToArray(); this.journal = journal;
    }

    public async Task<BlockReferenceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var references = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath);
        return await RefreshUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
    }

    // Maintenance retains both gates and this pinned root through its sweep.
    internal async Task<BlockReferenceRefreshResult> RefreshUnderGateAsync(SafeFileTree tree, CancellationToken cancellationToken)
    {
        Guid session = Guid.NewGuid(); var watch = Stopwatch.StartNew();
        try
        {
            if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } root || !root.AsSpan().SequenceEqual(tree.Identity))
                throw new IOException("Block reference root changed or is not bound.");
            var recovery = await PublishedBlocks.RecoverCapturesAsync(tree, index, journal, cancellationToken).ConfigureAwait(false);
            if (index.ReadApplyIntents().Any())
            {
                Log("block.references_wait", "pending-application"); return new(null, 0, 0, 0, recovery.Deferred, WaitingForRecovery: true);
            }
            await ArchiveContent.RecoverAsync(tree, index, journal, cancellationToken).ConfigureAwait(false);
            var snapshot = await index.BeginBlockReferenceSnapshotAsync(root, DateTime.UtcNow.Ticks, cancellationToken).ConfigureAwait(false);
            Checkpoint?.Invoke("begun");
            var batch = new Dictionary<string, BlockReferenceRow>(StringComparer.Ordinal); int batches = 0;
            // Hints also cover unpublished/withheld/local RO rows. A stale hint
            // can conservatively retain bytes; it can never authorize deletion.
            foreach (var chunk in index.Chunks.ReadAll())
                await AddAsync(chunk.Hash, chunk.Length, BlockReferenceKind.Current).ConfigureAwait(false);
            foreach (var row in index.LocalFiles.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Record is null) continue;
                var version = VersionView.Open(row.Record, keys, device);
                if (PathRules.Key(version.Path) != PathRules.Key(row.Path) || version.Type != row.Type || version.Deleted != row.Deleted
                    || !version.Deleted && version.Type == 0 && (version.Size != row.Size || !version.FileHash.SequenceEqual(row.FileHash)))
                    throw new InvalidDataException("Local reference manifest does not match its row.");
                await AddVersionAsync(version, BlockReferenceKind.Current).ConfigureAwait(false);
            }
            foreach (var needed in index.Needed.ReadAll())
                foreach (var remote in index.RemotesForPath(needed.PathKey))
                {
                    cancellationToken.ThrowIfCancellationRequested(); var version = VersionView.Open(remote.Record, keys, remote.DeviceId);
                    if (PathRules.Key(version.Path) != needed.PathKey) throw new InvalidDataException("Needed reference manifest does not match its path.");
                    await AddVersionAsync(version, BlockReferenceKind.Needed).ConfigureAwait(false);
                }
            var archiveCoverage = new List<ArchiveReferenceSource>(1000);
            foreach (var marker in index.Meta.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!marker.Key.StartsWith("_history-", StringComparison.Ordinal)) continue;
                var state = HistoryState.DecodeMarker(marker, keys);
                if (!state.Root.AsSpan().SequenceEqual(root)) throw new IOException("History reference belongs to another root.");
                await AddVersionAsync(state.Source, BlockReferenceKind.History).ConfigureAwait(false);
                if (!state.PublicCopy)
                {
                    archiveCoverage.Add(new(state.Source.Path, state.Destination, state.Source.Size, state.Source.FileHash.ToArray()));
                    if (archiveCoverage.Count == 1000) await FlushArchiveCoverageAsync().ConfigureAwait(false);
                }
            }
            await FlushArchiveCoverageAsync().ConfigureAwait(false);
            foreach (var source in index.ReadRetainedArchiveProvenance(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = index.FindActiveArchiveContent(source.SourceId);
                if (content is null && !index.IsBlockArchiveSourceCovered(snapshot.Id, source.SourceId))
                    content = await ArchiveContent.CaptureAsync(tree, index, source, journal, Checkpoint, cancellationToken).ConfigureAwait(false);
                if (content is null) continue;
                if (!content.RootId.AsSpan().SequenceEqual(root) || content.Size != source.Archive.Size
                    || !content.FileHash.AsSpan().SequenceEqual(source.Archive.FileHash)) throw new InvalidDataException("Archive content no longer matches its protected source.");
                long offset = 0; int count = 0;
                foreach (var chunk in index.ReadArchiveContentChunks(content.Id))
                {
                    if (chunk.Offset != offset || chunk.Length > content.Size - offset) throw new InvalidDataException("Archive content chunks changed.");
                    offset += chunk.Length; count++; await AddAsync(chunk.Hash, chunk.Length, BlockReferenceKind.Archive).ConfigureAwait(false);
                }
                if (offset != content.Size || count != content.ChunkCount) throw new InvalidDataException("Archive content manifest is incomplete.");
                await index.AppendBlockArchiveContentCoverageAsync(snapshot.Id, content.Id, cancellationToken).ConfigureAwait(false);
            }
            foreach (var pending in index.ReadPendingArchiveContents())
            {
                if (index.IsArchiveSourceReleased(pending.SourceId, root)) continue;
                if (!pending.RootId.AsSpan().SequenceEqual(root) || pending.ChunkCount > ArchiveContentSnapshot.MaximumChunks)
                    throw new InvalidDataException("Pending archive protection belongs to another root.");
                long offset = 0; int count = 0;
                foreach (var chunk in index.ReadPendingArchiveContentChunks(pending.Id))
                {
                    if (chunk.Offset != offset || chunk.Length > pending.NextOffset - offset) throw new InvalidDataException("Pending archive chunk prefix changed.");
                    offset += chunk.Length; count++; await AddAsync(chunk.Hash, chunk.Length, BlockReferenceKind.Archive).ConfigureAwait(false);
                }
                if (offset != pending.NextOffset || count != pending.ChunkCount) throw new InvalidDataException("Pending archive chunk prefix is incomplete.");
            }
            long unmanifested = index.CountUnmanifestedReferenceArchives(snapshot.Id);
            await FlushAsync().ConfigureAwait(false); Checkpoint?.Invoke("before-activate");
            await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            var active = index.ReadActiveBlockReferenceSnapshot() ?? throw new InvalidOperationException("Reference activation disappeared.");
            Checkpoint?.Invoke("activated"); Log("block.references_complete", changed: active.References, deferred: unmanifested + recovery.Deferred);
            return new(active.Id, active.References, batches, unmanifested, recovery.Deferred);

            async Task AddVersionAsync(VersionView version, BlockReferenceKind kind)
            {
                if (version.Type != 0 || version.Deleted) return;
                foreach (var chunk in version.ExportBody().Chunks)
                    await AddAsync(chunk.Hash.ToByteArray(), checked((int)chunk.Length), kind).ConfigureAwait(false);
            }
            async Task AddAsync(byte[] hash, int length, BlockReferenceKind kind)
            {
                cancellationToken.ThrowIfCancellationRequested(); string key = Convert.ToHexString(hash);
                if (batch.TryGetValue(key, out var known))
                {
                    if (known.Length != length) throw new InvalidDataException("Reference hash has conflicting lengths.");
                    batch[key] = known with { Kinds = known.Kinds | kind }; return;
                }
                batch.Add(key, new(hash.ToArray(), length, kind));
                if (batch.Count == 1000) await FlushAsync().ConfigureAwait(false);
            }
            async Task FlushAsync()
            {
                if (batch.Count == 0) return;
                await index.AppendBlockReferencesAsync(snapshot.Id, batch.Values.ToArray(), cancellationToken).ConfigureAwait(false);
                batches++; batch.Clear(); Checkpoint?.Invoke("batch");
            }
            async Task FlushArchiveCoverageAsync()
            {
                if (archiveCoverage.Count == 0) return;
                await index.AppendBlockArchiveCoverageAsync(snapshot.Id, archiveCoverage.ToArray(), cancellationToken).ConfigureAwait(false);
                archiveCoverage.Clear(); Checkpoint?.Invoke("archive-batch");
            }
        }
        catch (Exception error) { Log("block.references_failed", error.GetType().Name, error); throw; }

        void Log(string name, string? code = null, Exception? error = null, long? changed = null, long? deferred = null) =>
            journal?.TryWrite(new(name, "block-references") { SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
                ErrorCode = code, HResult = error?.HResult, NativeError = error switch { Win32Exception native => native.NativeErrorCode,
                    SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null }, ItemsChanged = changed, ItemsDeferred = deferred,
                ElapsedMilliseconds = watch.ElapsedMilliseconds, RetryInMilliseconds = error is not null || name == "block.references_wait" ? 10000 : null });
    }
}
