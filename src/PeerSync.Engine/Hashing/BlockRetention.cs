using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

public sealed record BlockSweepResult(int Deleted, int Missing, int Cancelled, int Deferred, long BytesRemoved,
    long OwnedBytes, long CapacityBytes, bool WaitingForRecovery = false)
{
    public bool CapacityExceeded => !WaitingForRecovery && OwnedBytes > CapacityBytes;
    public int MetadataMutations { get; init; }
    public bool MetadataCleanupPending { get; init; }
}

/// <summary>Host owns keys/index/journal. Fresh authenticated references and
/// both maintenance gates remain held through ownership verification, durable
/// native deletion and restart recovery. Unknown filesystem data is preserved.</summary>
public sealed class BlockRetention
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] device;
    private readonly FolderSettings settings;
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    internal Func<ulong>? CapacityOverride { get; init; }
    internal Action<string>? Checkpoint { get; init; }

    public BlockRetention(string rootPath, FolderIndex index, FolderKeys keys, byte[] device,
        FolderSettings? settings = null, TimeProvider? clock = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(device);
        if (device.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(device));
        if (!keys.CanRead) throw new ArgumentException("Opaque retention requires the P6 engine.", nameof(keys));
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys; this.device = device.ToArray();
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson()); this.clock = clock ?? TimeProvider.System; this.journal = journal;
    }

    public async Task<BlockSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        // Archive pruning has its own gate. Recheck all policy/reference state
        // after acquiring both gates; new holds are FULL-accounted atomically.
        var bootstrap = await new HistoryRecovery(rootPath, index, keys, device, settings, journal).BootstrapAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap.WaitingForRecovery) return new(0, 0, 0, 0, 0, index.ReadOwnedBlockBytes(), 0, true);
        await new ArchiveManager(rootPath, index, settings, clock, journal).PruneAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var references = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        Guid session = Guid.NewGuid(); var watch = Stopwatch.StartNew(); int deleted = 0, missing = 0, cancelled = 0, deferred = 0, visited = 0; long removed = 0, cap = 0;
        TimeSpan nextProgress = TimeSpan.FromSeconds(5);
        int metadataMutations = 0; bool metadataPending = false;
        try
        {
            using var tree = new SafeFileTree(rootPath); byte[] root = tree.Identity.ToArray();
            if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } bound || !bound.AsSpan().SequenceEqual(root)) throw new IOException("Block retention root changed or is not bound.");
            ulong capacity = CapacityOverride?.Invoke() ?? tree.CapacityBytes; if (capacity == 0) throw new IOException("Block capacity must be positive.");
            cap = (long)UInt128.Min(long.MaxValue, (UInt128)capacity * (uint)settings.ArchiveMaxPercentOfVolume / 100);
            var policy = await new HistoryPolicy(rootPath, index, keys, device, settings, clock, journal).RetireUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
            if (policy.WaitingForRecovery) return Result(waiting: true);
            var fresh = await new BlockReferences(rootPath, index, keys, device, journal).RefreshUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
            if (fresh.SnapshotId is null || fresh.WaitingForRecovery || fresh.ArchivesWithoutManifest != 0)
            { deferred += checked((int)Math.Min(int.MaxValue, fresh.ArchivesWithoutManifest + fresh.CaptureDeferrals)); Log("block.sweep_wait", code: "reference-coverage"); return Result(waiting: true); }
            Guid snapshot = fresh.SnapshotId.Value; long now = clock.GetUtcNow().UtcTicks;
            // Reconcile/cancel old journals even while partial captures defer.
            // Present data may be deleted only with complete capture coverage.
            foreach (var intent in index.ReadBlockDeletionIntents().ToArray())
            {
                if (visited >= 1000) break; cancellationToken.ThrowIfCancellationRequested(); visited++;
                try { await DeleteAsync(intent, recovering: true, allowNativeDeletion: fresh.AllKnownSourcesCovered).ConfigureAwait(false); }
                catch (Exception error) when (FileError(error)) { deferred++; Log("block.sweep_deferred", intent.Id, error); }
                Progress();
            }
            if (visited < 1000)
            {
                var captures = await PublishedBlocks.RetireCapturesAsync(tree, index, snapshot, now, 1000 - visited, journal, Checkpoint, cancellationToken).ConfigureAwait(false);
                deleted += captures.Deleted; missing += captures.Missing; cancelled += captures.Cancelled; removed += captures.BytesRemoved; visited += captures.Visited;
                if (captures.Visited != 0) fresh = await new BlockReferences(rootPath, index, keys, device, journal).RefreshUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
                if (captures.Deferred != 0 && fresh.CaptureDeferrals == 0) { deferred += captures.Deferred; Log("block.sweep_wait", code: "capture-retirement"); return Result(waiting: true); }
            }
            if (!fresh.AllKnownSourcesCovered) { deferred += checked((int)Math.Min(int.MaxValue, fresh.ArchivesWithoutManifest + fresh.CaptureDeferrals)); Log("block.sweep_wait", code: "reference-coverage"); return Result(waiting: true); }
            snapshot = fresh.SnapshotId!.Value; Checkpoint?.Invoke("refreshed");
            var metadata = await index.PruneRetiredMetadataAsync(snapshot, root, now, settings.ArchiveDays, cancellationToken: cancellationToken).ConfigureAwait(false);
            metadataMutations = metadata.Mutations; metadataPending = metadata.MoreWork; Checkpoint?.Invoke("metadata-pruned");
            journal?.TryWrite(new("block.metadata_pruned", "block-sweep") { SessionId = session, Level = DiagnosticLevel.Debug,
                ItemsChanged = metadata.Mutations, ErrorCode = metadata.MoreWork ? "batch-limit" : null,
                RetryInMilliseconds = metadata.MoreWork ? 10000 : null, ElapsedMilliseconds = watch.ElapsedMilliseconds });
            byte[]? cursor = null;
            do
            {
                var batch = await index.ObserveBlockOrphansAsync(snapshot, root, now, settings.ArchiveDays, cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
                cursor = batch.NextHash; Progress(); if (batch.Visited < 1000) break;
            } while (cursor is not null);
            Checkpoint?.Invoke("observed");
            foreach (var candidate in index.ReadBlockSweepCandidates(snapshot, root, now, settings.ArchiveDays, cap, limit: Math.Max(1, 1000 - visited)).ToArray())
            {
                if (visited >= 1000) break; visited++; cancellationToken.ThrowIfCancellationRequested();
                var intent = new BlockDeletionIntent(Guid.NewGuid(), candidate, now, snapshot,
                    candidate.FirstUnreferencedTicks < Math.Max(0, now - settings.ArchiveDays * TimeSpan.TicksPerDay) ? BlockDeletionReason.Age : BlockDeletionReason.Capacity);
                try { await DeleteAsync(intent, recovering: false).ConfigureAwait(false); }
                catch (Exception error) when (FileError(error)) { deferred++; Log("block.sweep_deferred", intent.Id, error); }
                Progress();
            }
            Log("block.sweep_complete"); return Result();

            async Task DeleteAsync(BlockDeletionIntent intent, bool recovering, bool allowNativeDeletion = true)
            {
                var row = intent.Candidate.OwnedBlock;
                if (!row.RootId.AsSpan().SequenceEqual(root)) throw new IOException("Block deletion belongs to another root.");
                bool mayDelete = allowNativeDeletion && (!recovering || await index.CanContinueBlockDeletionAsync(intent, snapshot, now, settings.ArchiveDays, cap, cancellationToken).ConfigureAwait(false));
                string name = Convert.ToHexString(row.Hash); DirectoryLease? parent = null; SafeFileHandle? file = null;
                try
                {
                    try { parent = tree.Directory(".psp/blocks/" + name[..2], privatePath: true); file = parent.OpenFile(name, mutation: true); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
                    if (file is null)
                    {
                        if (recovering) await index.CompleteBlockDeletionAsync(intent, snapshot, cancellationToken).ConfigureAwait(false);
                        else await index.ForgetMissingBlockAsync(intent.Candidate, snapshot, cancellationToken).ConfigureAwait(false);
                        missing++; Checkpoint?.Invoke("completed"); return;
                    }
                    if (!mayDelete)
                    { await index.CancelBlockDeletionAsync(intent, snapshot, cancellationToken).ConfigureAwait(false); cancelled++; return; }
                    PublishedBlocks.RequireIndependent(file);
                    if (!BlockCaptureProof.Identity(file).AsSpan().SequenceEqual(row.FileId) || RandomAccess.GetLength(file) != row.Length
                        || !GuardedFile.Hash(file, cancellationToken).AsSpan().SequenceEqual(row.Hash)) throw new InvalidDataException("Owned block identity or content changed.");
                    if (!recovering && !await index.ReserveBlockDeletionAsync(intent, settings.ArchiveDays, cap, cancellationToken).ConfigureAwait(false)) return;
                    Checkpoint?.Invoke("prepared");
                    GuardedFile.DeleteOnClose(file); file.Dispose(); file = null; Checkpoint?.Invoke("deleted");
                    try { using var unexpected = parent!.OpenFile(name); throw new IOException("Deleted block path is occupied."); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
                    await index.CompleteBlockDeletionAsync(intent, snapshot, cancellationToken).ConfigureAwait(false);
                    deleted++; removed += row.Length; Checkpoint?.Invoke("completed");
                }
                finally { file?.Dispose(); parent?.Dispose(); }
            }
        }
        catch (Exception error) { Log("block.sweep_failed", error: error); throw; }

        BlockSweepResult Result(bool waiting = false) => new(deleted, missing, cancelled, deferred, removed, index.ReadOwnedBlockBytes(), cap, waiting)
        { MetadataMutations = metadataMutations, MetadataCleanupPending = metadataPending };
        void Progress() { if (watch.Elapsed >= nextProgress) { Log("block.sweep_progress"); nextProgress = watch.Elapsed + TimeSpan.FromSeconds(5); } }
        void Log(string name, Guid? id = null, Exception? error = null, string? code = null) => journal?.TryWrite(new(name, "block-sweep")
        { SessionId = id ?? session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning, ErrorCode = code ?? error?.GetType().Name,
            HResult = error?.HResult, NativeError = error switch { Win32Exception native => native.NativeErrorCode, SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null },
            ItemsChanged = deleted + missing + cancelled, ItemsVisited = visited, ItemsDeferred = deferred, Bytes = removed, ElapsedMilliseconds = watch.ElapsedMilliseconds,
            RetryInMilliseconds = error is not null || name == "block.sweep_wait" ? 10000 : null });
    }

    private static bool FileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception;
}
