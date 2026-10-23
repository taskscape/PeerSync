using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Ignore;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Net;
using PeerSync.Storage.FolderDb;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Sync;

public sealed record PullPassResult(int Visited, int Applied, int Deferred, int Failed, int Recovered, int Scans, bool MoreWork, long? NextAttemptTicks)
{
    public int ScansCompleted { get; init; }
    public int ScannedEntries { get; init; }
    public int ScanProblems { get; init; }
}

/// <summary>One host-owned coordinator per folder. Caller owns index, keys and
/// transport. Cancel and await hosted/direct work before disposing this object.
/// Host watcher notifications call RequestScan; authenticated transport uses
/// ReceiveIndexAsync/AnnounceIndexRangeAsync so pending work wakes promptly.</summary>
public sealed class Puller : IDisposable
{
    private const int PublicationScanBatchSize = 10_000;
    private const int PublicationScanMaximumSeconds = 300;
    private static readonly ConditionalWeakTable<FolderIndex, SemaphoreSlim> PassGates = new();
    private readonly FolderIndex index;
    private readonly string rootPath;
    private readonly byte[] own;
    private readonly FolderSettings settings;
    private readonly NeededSet needed;
    private readonly Scanner scanner;
    private readonly ConcurrentDictionary<string, byte> recentRemoteWrites = new(StringComparer.Ordinal);
    private readonly FileApplier files;
    private readonly DirectoryApplier directories;
    private readonly StagingCleaner staging;
    private readonly DirectoryPublicationWorker? publications;
    private readonly DirectoryMetadataWorker metadata;
    private long? nextMetadataTicks;
    private readonly Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch;
    internal Func<ReadOnlyMemory<byte>, IReadOnlyList<PeerBlockRequest>, bool, CancellationToken, ValueTask<IReadOnlyList<PeerBlockResult>>>? BatchFetch { get; set; }
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly Channel<PullPassResult> outcomes = Channel.CreateBounded<PullPassResult>(new BoundedChannelOptions(128)
    { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<PullPassResult> Outcomes => outcomes.Reader;
    internal Scanner Scanner => scanner;
    private byte[]? rootId;
    private bool initialized;
    private bool neededRepairPending = true;
    internal Action<string>? Checkpoint { get; set; }
    private long nextScanTicks, scanRetryTicks, nextStagingTicks;
    private long publicationScanSinceTicks;
    private int publicationScanPending;
    private int scanRequested = 1, scanAttempt, stagingRetryAttempt, running, disposed;

    public Puller(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        FolderSettings? settings = null, TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
        : this(rootPath, index, keys, ownDevice, fetch, settings, timeProvider, journal, directorySnapshots: true) { }

    internal Puller(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal, bool directorySnapshots,
        IgnoreMatcher? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(index); ArgumentNullException.ThrowIfNull(fetch);
        this.index = index; this.rootPath = rootPath; this.fetch = fetch; own = ownDevice?.ToArray() ?? throw new ArgumentNullException(nameof(ownDevice));
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson()); clock = timeProvider ?? TimeProvider.System; this.journal = journal;
        needed = new(index, keys, own, this.settings, journal: journal);
        scanner = new(rootPath, index, keys, own, ignore: ignore, timeProvider: clock, settings: this.settings, journal: journal)
            { StageDirectorySnapshots = directorySnapshots };
        if (directorySnapshots) publications = new(rootPath, index, keys, own, this.settings, clock, journal);
        metadata = new(rootPath, index, keys, own, clock, fetch, journal);
        files = new(rootPath, index, keys, own, this.settings, journal, clock);
        directories = new(rootPath, index, keys, own, this.settings, journal);
        staging = new(rootPath, index, clock, journal);
    }

    public void NotifyWorkAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); wake.Writer.TryWrite(true);
    }
    public void RequestScan() { Volatile.Write(ref scanRequested, 1); NotifyWorkAvailable(); }
    internal void RequestWatcherScan(WatchBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.FullScan || batch.RelativePaths.Count == 0 || Volatile.Read(ref publicationScanPending) == 0)
        { RequestScan(); return; }
        foreach (string path in batch.RelativePaths)
        {
            string key;
            try { key = PathRules.Key(path); }
            catch (ArgumentException) { RequestScan(); return; }
            if (!recentRemoteWrites.ContainsKey(key)) { RequestScan(); return; }
        }
        // The current pull batch owns these native writes. A same-path local
        // edit is still caught by guarded application and the five-minute
        // publication scan; unrelated paths request a scan immediately.
        journal?.TryWrite(new("puller.own_write_watch_coalesced", "watch")
            { Level = DiagnosticLevel.Debug, ItemsDeferred = batch.RelativePaths.Count });
        NotifyWorkAvailable();
    }

    internal void RecordBlockRoute(string eventName, int attempts, int bytes, Guid? sessionId = null, Exception? error = null)
        => journal?.TryWrite(new(eventName, "block-transfer")
        {
            SessionId = sessionId,
            Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
            Bytes = bytes,
            ItemsVisited = attempts,
            ErrorCode = error?.GetType().Name,
            HResult = error?.HResult
        });

    internal void RecordIndexPush(string eventName, long targetSequence, Guid? sessionId = null, Exception? error = null)
        => journal?.TryWrite(new(eventName, "index-push")
        {
            SessionId = sessionId,
            Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
            ItemsVisited = targetSequence,
            ErrorCode = error?.GetType().Name,
            HResult = error?.HResult,
        });

    /// <summary>The caller must authenticate both the TLS device identity and
    /// folder membership before exposing this facade to a remote peer.</summary>
    public PeerIndexSession OpenPeerIndexSession(byte[] authenticatedDevice)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); ArgumentNullException.ThrowIfNull(authenticatedDevice);
        if (authenticatedDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(authenticatedDevice));
        Guid session = Guid.NewGuid();
        return new(this, index, needed, authenticatedDevice, new ClockGuard(clock, journal, session), journal, session);
    }

    public async Task<RemoteIndexResult> ReceiveIndexAsync(byte[] source, long afterSequence, long lastSequence,
        IReadOnlyList<Envelope> records, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(records); source = source.ToArray(); records = records.ToArray();
        using var root = await PinReceivingRootAsync(cancellationToken).ConfigureAwait(false);
        var result = await needed.ApplyIndexUpdateAsync(source, afterSequence, lastSequence, records, cancellationToken).ConfigureAwait(false);
        NotifyWorkAvailable(); return result;
    }
    internal async Task<SafeFileTree> PinReceivingRootAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var tree = new SafeFileTree(rootPath);
        try
        {
            byte[]? bound = index.Meta.Find("_root-identity")?.Value;
            if (bound is null)
            {
                using var operation = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
                using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                await index.BindPhysicalRootAsync(tree.Identity.ToArray(), token).ConfigureAwait(false);
                bound = index.Meta.Find("_root-identity")?.Value;
            }
            if (bound is not { Length: 24 } || !tree.Identity.SequenceEqual(bound)) throw new IOException("Index receipt root identity changed.");
            Checkpoint?.Invoke("index-root-bound"); return tree;
        }
        catch { tree.Dispose(); throw; }
    }

    public async Task<RemoteIndexRange> AnnounceIndexRangeAsync(byte[] source, long targetSequence, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var result = await needed.AnnounceIndexRangeAsync(source, targetSequence, cancellationToken).ConfigureAwait(false);
        NotifyWorkAvailable(); return result;
    }

    public async Task<PullPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var gate = PassGates.GetValue(index, _ => new(1, 1)); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Guid session = Guid.NewGuid();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            journal?.TryWrite(new("puller.pass_start", "pull") { SessionId = session });
            var result = await RunPassCoreAsync(cancellationToken).ConfigureAwait(false);
            journal?.TryWrite(new("puller.pass_complete", "pull") { SessionId = session, ItemsVisited = result.Visited,
                ItemsChanged = result.Applied, ItemsDeferred = result.Deferred + result.Failed });
            outcomes.Writer.TryWrite(result);
            return result;
        }
        catch (Exception error) { Failure("puller.pass_failed", session, error); throw; }
        finally { gate.Release(); }
    }

    // Installed hosts expose their scanner to status and watcher registration
    // before the first transport pass. Drain native intents before exposure;
    // the first pass repeats the check in case new work arrived meanwhile.
    internal async Task RecoverBeforeExternalScanAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var gate = PassGates.GetValue(index, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int recovered = await RecoverPendingAsync(token).ConfigureAwait(false);
            journal?.TryWrite(new("puller.startup_recovery_complete", "pull")
                { Level = DiagnosticLevel.Debug, ItemsChanged = recovered });
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("puller.startup_recovery_failed", "pull")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
                    NativeError = NativeError(error) });
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task<int> RecoverPendingAsync(CancellationToken token)
    {
        if (index.Meta.Find("_root-identity")?.Value is { } boundRoot)
        {
            int recovered = await staging.RecoverAsync(boundRoot, token).ConfigureAwait(false);
            return recovered + await files.RecoverAsync(token).ConfigureAwait(false);
        }
        if (index.ReadApplyIntents().Any() || index.HasStagingRetirements())
            throw new InvalidDataException("Unbound index has recovery state.");
        return 0;
    }

    private async Task<PullPassResult> RunPassCoreAsync(CancellationToken token)
    {
        int recovered = 0, scans = 0, scansCompleted = 0, scanEntries = 0, scanProblems = 0, applied = 0, deferred = 0, failed = 0;
        bool mustScan = false;
        if (!initialized)
        {
            recovered += await RecoverPendingAsync(token).ConfigureAwait(false);
            await RecordScanAsync(rebuild: true).ConfigureAwait(false);
            rootId = index.Meta.Find("_root-identity")?.Value?.ToArray();
            if (rootId is not { Length: 24 }) throw new InvalidDataException("Startup scan did not bind the index.");
            initialized = true;
        }
        else
        {
            if (index.HasStagingRetirements())
            {
                recovered += await staging.RecoverAsync(rootId!, token).ConfigureAwait(false);
                neededRepairPending = true; Volatile.Write(ref scanRequested, 1); scanRetryTicks = 0; nextStagingTicks = 0;
            }
            if (index.ReadApplyIntents().Any())
            {
                Volatile.Write(ref scanRequested, 1); recovered += await files.RecoverAsync(token).ConfigureAwait(false); scanRetryTicks = 0;
            }
            long now = Now;
            if ((Volatile.Read(ref scanRequested) != 0 || now >= nextScanTicks) && now >= scanRetryTicks)
            { await RecordScanAsync(rebuild: false).ConfigureAwait(false); }
        }
        if (await files.PrepareCaseCopiesAsync(fetch, token).ConfigureAwait(false))
        { await RecordScanAsync(rebuild: false).ConfigureAwait(false); }
        if (publicationScanPending != 0 && !neededRepairPending)
        {
            // New remote work can replace the live backlog between passes.
            // Publish the installed files before a newly eligible directory
            // transition or deletion enters the native mutation path.
            var upcoming = index.ReadScheduledPullWork(rootId!, own, Now, settings.Priority, 1000);
            if (upcoming.Count == 0 || upcoming.Any(row => row.Work!.Stage != 1))
            { await RecordScanAsync(rebuild: false).ConfigureAwait(false); }
        }
        if (publications is not null)
        {
            var published = await publications.RunAsync(token: token).ConfigureAwait(false);
            if (published.RequiresScan) RequestScan();
            if (published.RequiresNeededRebuild) { neededRepairPending = true; RequestScan(); }
            if (!neededRepairPending && published.ChangedPaths.Count != 0)
            {
                try { await needed.RecomputeAsync(published.ChangedPaths, token).ConfigureAwait(false); }
                catch (CasePreservationCapacityException) { CaseBackpressure(); }
                catch (DirectoryMetadataCapacityException) { MetadataBackpressure(); }
            }
        }
        var metadataPass = await metadata.RunAsync(token: token).ConfigureAwait(false); nextMetadataTicks = metadataPass.NextTicks;
        var casePaths = await files.CompleteCaseCopiesAsync(token).ConfigureAwait(false);
        if (casePaths.Count != 0)
        {
            if (neededRepairPending) { scanRetryTicks = 0; Volatile.Write(ref scanRequested, 1); }
            try { await needed.RecomputeAsync(casePaths, token).ConfigureAwait(false); }
            catch (CasePreservationCapacityException) { CaseBackpressure(); }
            catch (DirectoryMetadataCapacityException) { MetadataBackpressure(); }
            foreach (string path in casePaths)
                if (index.Needed.Find(path)?.Work is { } descriptor) await index.ClearPullRetryAsync(path, descriptor.Fingerprint, token).ConfigureAwait(false);
        }
        using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
        {
            int retired = await index.RetireDirectoryCaseWitnessesAsync(rootId!, Now, limit: 64, cancellationToken: token).ConfigureAwait(false);
            if (retired != 0) journal?.TryWrite(new("puller.case_witnesses_retired", "case-maintenance")
                { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Debug, ItemsChanged = retired });
            await index.CompactCompletedCaseSourcesAsync(rootId!, Now, limit: 64, cancellationToken: token).ConfigureAwait(false);
        }
        int visited = 0;
        bool installedDirectory;
        bool workAllowed = !neededRepairPending;
        bool deleting = false;
        do
        {
            // A newly installed parent makes its queued children eligible.
            // Refill only after such a change, within the normal 1,000-work
            // pass budget, so a file-first index does not create retry churn.
            IReadOnlyList<NeededRow> work = !workAllowed ? [] :
                index.ReadScheduledPullWork(rootId!, own, Now, settings.Priority, 1000 - visited);
            if (work.Count == 0) break;
            installedDirectory = false;
            foreach (var row in work)
            {
                // A parent can unlock more live children than were visible at
                // the start of the pass. Finish live work before tombstones,
                // preserving the stage order across refreshed batches.
                if ((row.Work!.Stage >= 2) != deleting) continue;
                visited++;
                token.ThrowIfCancellationRequested();
                try
                {
                    FileApplyOutcome outcome; bool requiresScan;
                    bool opensDirectory = row.Work!.Stage is 0 or 2 && index.LocalFiles.Find(row.PathKey) is { Type: 0, Deleted: false, Record: not null };
                    if (row.Work!.Stage is 0 or 3)
                    {
                        var result = await directories.ApplyScheduledAsync(row.PathKey, token).ConfigureAwait(false);
                        outcome = result.Outcome; requiresScan = result.RequiresScan;
                        if (outcome == FileApplyOutcome.Deferred && !requiresScan)
                        {
                            var transition = await files.ApplyScheduledAsync(row.PathKey, fetch, BatchFetch, token).ConfigureAwait(false);
                            outcome = transition.Outcome; requiresScan = transition.RequiresScan;
                        }
                    }
                    else
                    {
                        var result = await files.ApplyScheduledAsync(row.PathKey, fetch, BatchFetch, token).ConfigureAwait(false);
                        outcome = result.Outcome; requiresScan = result.RequiresScan;
                        if (outcome == FileApplyOutcome.Deferred && !requiresScan)
                        {
                            var transition = await directories.ApplyScheduledAsync(row.PathKey, token).ConfigureAwait(false);
                            outcome = transition.Outcome; requiresScan = transition.RequiresScan;
                        }
                    }
                    mustScan |= requiresScan;
                    if (outcome is FileApplyOutcome.Applied or FileApplyOutcome.Unchanged)
                    {
                        if (outcome == FileApplyOutcome.Applied)
                        {
                            applied++;
                            if (publications is not null)
                            {
                                // The installed signed file row is already durable. Defer only
                                // its parent snapshot walk while more live files are flowing;
                                // native races, preserved copies and deletions still scan now.
                                if (row.Work.Stage == 1 && !requiresScan && !opensDirectory)
                                {
                                    publicationScanSinceTicks = publicationScanPending == 0 ? Now : publicationScanSinceTicks;
                                    publicationScanPending++;
                                    recentRemoteWrites.TryAdd(row.PathKey, 0);
                                    string parent = ApplyState.Parent(row.PathKey);
                                    while (parent.Length != 0)
                                    {
                                        recentRemoteWrites.TryAdd(parent, 0);
                                        parent = ApplyState.Parent(parent);
                                    }
                                }
                                else mustScan = true;
                            }
                            if (opensDirectory) { neededRepairPending = true; mustScan = true; }
                            if (row.Work.Stage == 0 && index.LocalFiles.Find(row.PathKey) is { Type: 1, Deleted: false, FileId: not null })
                                installedDirectory = true;
                        }
                        else await needed.RecomputeAsync([row.PathKey], token).ConfigureAwait(false);
                        await index.ClearPullRetryAsync(row.PathKey, row.Work.Fingerprint, token).ConfigureAwait(false);
                    }
                    else { deferred++; await RetryAsync(row, null, token).ConfigureAwait(false); }
                }
                catch (Exception error) when (Retryable(error))
                {
                    failed++; await RetryAsync(row, error, token).ConfigureAwait(false);
                    if (index.ReadApplyIntents().Any())
                    {
                        // Stage recovery releases a locked target's intent before another
                        // independent path. Prepared recovery may itself require folder retry.
                        Volatile.Write(ref scanRequested, 1); mustScan = true;
                        recovered += await files.RecoverAsync(token).ConfigureAwait(false);
                    }
                }
            }
            if (deleting) break;
            if (!installedDirectory) deleting = true;
        }
        while (visited < 1000);
        // Flush the delayed parent snapshots at a bounded count/time, on an
        // idle pass, or before any destructive scheduled work. The scanner can
        // also run independently on a watcher/explicit request above.
        long scanNow = Now;
        int nextStage = 2;
        if (publicationScanPending != 0 && visited == 1000 && !mustScan)
        {
            var nextBatch = index.ReadScheduledPullWork(rootId!, own, scanNow, settings.Priority, 1);
            if (nextBatch.Count != 0) nextStage = nextBatch[0].Work!.Stage;
        }
        if (publicationScanPending != 0 && (publicationScanPending >= PublicationScanBatchSize
            || scanNow - publicationScanSinceTicks >= PublicationScanMaximumSeconds * TimeSpan.TicksPerSecond
            || visited < 1000 || nextStage != 1)) mustScan = true;
        if (mustScan)
        {
            await RecordScanAsync(rebuild: false).ConfigureAwait(false);
        }
        else if (publicationScanPending != 0)
            journal?.TryWrite(new("puller.publication_scan_deferred", "pull")
            { Level = DiagnosticLevel.Debug, ItemsDeferred = publicationScanPending,
                RetryInMilliseconds = (int)Math.Clamp((publicationScanSinceTicks
                    + PublicationScanMaximumSeconds * TimeSpan.TicksPerSecond - scanNow)
                    / TimeSpan.TicksPerMillisecond, 0, PublicationScanMaximumSeconds * 1000) });
        if (Now >= nextStagingTicks && !neededRepairPending && scanRetryTicks == 0 && Volatile.Read(ref scanRequested) == 0 && !index.ReadApplyIntents().Any())
        {
            bool moreStaging = await staging.RunAsync(rootId!, token).ConfigureAwait(false);
            if (moreStaging) nextStagingTicks = Now;
            else if (staging.RetryNeeded)
            {
                nextStagingTicks = AddSeconds(Now, RescanScheduler.BackoffSeconds(stagingRetryAttempt));
                stagingRetryAttempt = Math.Min(6, stagingRetryAttempt + 1);
            }
            else { nextStagingTicks = AddSeconds(Now, 3600); stagingRetryAttempt = 0; }
        }
        long finished = Now; long? next = NextWorkTicks(finished);
        return new(visited, applied, deferred, failed, recovered, scans, next <= finished || nextStagingTicks <= finished && scanRetryTicks == 0 && Volatile.Read(ref scanRequested) == 0, next)
        { ScansCompleted = scansCompleted, ScannedEntries = scanEntries, ScanProblems = scanProblems };

        async Task RecordScanAsync(bool rebuild)
        {
            ScanResult? result = await ScanAsync(rebuild, token).ConfigureAwait(false);
            scans++;
            if (result is not null)
            {
                scansCompleted++;
                scanEntries = checked(scanEntries + result.Enumerated);
                scanProblems = checked(scanProblems + result.ProblemCount);
            }
        }
    }

    private async Task RetryAsync(NeededRow row, Exception? error, CancellationToken token)
    {
        var retry = await index.RecordPullFailureAsync(row.PathKey, rootId!, row.Work!.Fingerprint, Now, token).ConfigureAwait(false);
        journal?.TryWrite(new("puller.work_retry", "pull") { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Debug,
            ErrorCode = error?.GetType().Name ?? "work-deferred", HResult = error?.HResult, NativeError = NativeError(error),
            RetryInMilliseconds = retry is null ? 0 : (int)Math.Clamp((retry.NextTicks - retry.FailedTicks) / TimeSpan.TicksPerMillisecond, 0, 600000), ItemsDeferred = 1 });
    }

    private async Task<ScanResult?> ScanAsync(bool rebuild, CancellationToken token)
    {
        Interlocked.Exchange(ref scanRequested, 0);
        bool repairAll = rebuild || neededRepairPending;
        neededRepairPending = true;
        bool scanned = false;
        try
        {
            ScanResult result;
            using (await FolderScanBudget.AcquireAsync(token).ConfigureAwait(false)) result = await scanner.ScanAsync(token).ConfigureAwait(false);
            scanned = true;
            Checkpoint?.Invoke("after-local-scan");
            if (repairAll || result.RequiresNeededRebuild) await needed.RebuildAsync(token).ConfigureAwait(false);
            else if (result.ChangedPaths.Count != 0) await needed.RecomputeAsync(result.ChangedPaths.Where(PathRules.IsValid), token).ConfigureAwait(false);
            neededRepairPending = false;
            long now = Now; nextScanTicks = AddSeconds(now, settings.RescanIntervalSeconds);
            bool incomplete = result.Problems.Any(p => p.Code is "read-failed" or "changed-during-read" or "enumeration-failed" or "published-content-verification-failed" or "problem-overflow");
            if (incomplete)
            {
                scanRetryTicks = AddSeconds(now, RescanScheduler.BackoffSeconds(scanAttempt)); scanAttempt = Math.Min(6, scanAttempt + 1);
                Volatile.Write(ref scanRequested, 1);
            }
            else { scanAttempt = 0; scanRetryTicks = 0; if (result.RequiresFollowupScan) Volatile.Write(ref scanRequested, 1); }
            if (!incomplete && !neededRepairPending)
            {
                if (publicationScanPending != 0)
                    journal?.TryWrite(new("puller.publication_scan_flushed", "pull")
                    { Level = DiagnosticLevel.Debug, ItemsChanged = publicationScanPending,
                        ElapsedMilliseconds = Math.Max(0, (Now - publicationScanSinceTicks) / (double)TimeSpan.TicksPerMillisecond) });
                publicationScanPending = 0;
                publicationScanSinceTicks = 0;
                recentRemoteWrites.Clear();
            }
            return result;
        }
        catch (CasePreservationCapacityException) when (scanned) { CaseBackpressure(); return null; }
        catch (DirectoryMetadataCapacityException) when (scanned) { MetadataBackpressure(); return null; }
        catch { Volatile.Write(ref scanRequested, 1); throw; }
    }

    private void CaseBackpressure()
    {
        neededRepairPending = true; Volatile.Write(ref scanRequested, 1); scanRetryTicks = AddSeconds(Now, 10);
        journal?.TryWrite(new("puller.case_backpressure", "pull") { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Warning,
            ItemsDeferred = index.ReadPendingCasePreservations().Count, RetryInMilliseconds = 10000 });
    }

    private void MetadataBackpressure()
    {
        neededRepairPending = true; Volatile.Write(ref scanRequested, 1); scanRetryTicks = AddSeconds(Now, 10);
        journal?.TryWrite(new("puller.metadata_backpressure", "directory-metadata")
            { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Warning, ItemsDeferred = 1, RetryInMilliseconds = 10000 });
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("The puller is already running.");
        try
        {
            await Task.Run(async () =>
            {
                int attempt = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested(); while (wake.Reader.TryRead(out _)) { }
                    TimeSpan delay; bool folderRetry = false;
                    try
                    {
                        await RunPassAsync(cancellationToken).ConfigureAwait(false); attempt = 0;
                        long now = Now; long scan = Volatile.Read(ref scanRequested) != 0 ? Math.Max(now, scanRetryTicks) : nextScanTicks;
                        long next = Math.Min(scan, NextWorkTicks(now) ?? DateTime.MaxValue.Ticks);
                        if (!neededRepairPending && scanRetryTicks == 0 && Volatile.Read(ref scanRequested) == 0) next = Math.Min(next, nextStagingTicks);
                        delay = TimeSpan.FromTicks(Math.Clamp(next - now, 0, 600 * TimeSpan.TicksPerSecond));
                    }
                    catch (Exception error) when (Retryable(error))
                    {
                        int seconds = RescanScheduler.BackoffSeconds(attempt); attempt = Math.Min(6, attempt + 1); delay = TimeSpan.FromSeconds(seconds); folderRetry = true;
                        journal?.TryWrite(new("puller.folder_retry", "pull") { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Warning,
                            ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = NativeError(error), RetryInMilliseconds = seconds * 1000 });
                    }
                    if (delay == TimeSpan.Zero) { await Task.Yield(); continue; }
                    if (folderRetry) { await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false); continue; }
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task available = wake.Reader.WaitToReadAsync(wait.Token).AsTask(); Task timer = Task.Delay(delay, clock, wait.Token);
                    await Task.WhenAny(available, timer).ConfigureAwait(false); wait.Cancel();
                    try { await Task.WhenAll(available, timer).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref running, 0); }
    }

    private long Now => Math.Max(1, clock.GetUtcNow().UtcTicks);
    private long? NextWorkTicks(long now)
    {
        long? pull = neededRepairPending ? Math.Max(now, scanRetryTicks) : index.ReadNextScheduledPullTicks(rootId!, own, now);
        long? cases = index.ReadNextCasePreservationTicks(rootId!, now);
        long? result = pull is null ? cases : cases is null ? pull : Math.Min(pull.Value, cases.Value);
        long? publication = publications is null ? null : index.ReadNextDirectoryPublicationTicks(rootId!, now);
        result = result is null ? publication : publication is null ? result : Math.Min(result.Value, publication.Value);
        long? metadataDue = index.ReadNextDirectoryMetadataTicks(rootId!, now);
        if (nextMetadataTicks is long scheduled) metadataDue = metadataDue is null ? scheduled : Math.Min(metadataDue.Value, scheduled);
        return result is null ? metadataDue : metadataDue is null ? result : Math.Min(result.Value, metadataDue.Value);
    }
    private static long AddSeconds(long now, int seconds) => now + Math.Min(DateTime.MaxValue.Ticks - now, seconds * TimeSpan.TicksPerSecond);
    private static bool Retryable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException
        || error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 or 14 };
    private static int? NativeError(Exception? error) => error switch { Win32Exception native => native.NativeErrorCode, SqliteException database => database.SqliteExtendedErrorCode, _ => null };
    private void Failure(string name, Guid session, Exception error) => journal?.TryWrite(new(name, "pull")
    { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = NativeError(error) });
    public void Dispose()
    {
        if (Volatile.Read(ref running) != 0) throw new InvalidOperationException("Stop the puller before disposal.");
        if (Interlocked.Exchange(ref disposed, 1) == 0) scanner.Dispose();
    }
}
