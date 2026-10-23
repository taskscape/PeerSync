using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Engine.Scanning;

public sealed record ScheduledFolder(Scanner Scanner, int RescanIntervalSeconds = 600, bool WatchChanges = true);
public sealed record ScanOutcome(Scanner Scanner, ScanResult? Result, string? ErrorCode, int RetryInSeconds);

/// <summary>The caller owns scanners, keys and indexes. Cancel and await RunAsync before disposing them.</summary>
public sealed class RescanScheduler
{
    private readonly ScheduledFolder[] folders;
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    private readonly Channel<ScanOutcome> outcomes = Channel.CreateBounded<ScanOutcome>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
    private int running;
    public ChannelReader<ScanOutcome> Outcomes => outcomes.Reader;

    public RescanScheduler(IEnumerable<ScheduledFolder> folders, TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        this.folders = folders.ToArray();
        var unique = new HashSet<Scanner>();
        foreach (var folder in this.folders)
        {
            ArgumentNullException.ThrowIfNull(folder);
            ArgumentNullException.ThrowIfNull(folder.Scanner);
            if (!unique.Add(folder.Scanner)) throw new ArgumentException("Duplicate scanner registration.", nameof(folders));
            if (folder.RescanIntervalSeconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(folders));
        }
        clock = timeProvider ?? TimeProvider.System;
        this.journal = journal;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("The scheduler is already running.");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Enumeration and synchronous SQLite reads must not block the hosting/UI thread.
            var tasks = folders.Select(folder => Task.Run(() => Guarded(() => RunFolderAsync(folder, stopping.Token), stopping), CancellationToken.None)).ToArray();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task RunFolderAsync(ScheduledFolder folder, CancellationToken cancellationToken)
    {
        Guid session = Guid.NewGuid();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        requests.Writer.TryWrite(true);
        // Subscribe before the startup scan so changes in that walk have notifications.
        var tasks = new List<Task>();
        if (folder.WatchChanges) tasks.Add(Guarded(() => WatchLoop(), stopped));
        tasks.Add(Guarded(() => PeriodicLoop(), stopped));
        tasks.Add(Guarded(() => ScanLoop(), stopped));
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally { requests.Writer.TryComplete(); }

        async Task ScanLoop()
        {
            int attempt = 0;
            await foreach (bool _ in requests.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
            {
                ScanResult? result = null;
                string? errorCode = null;
                var scanLease = await FolderScanBudget.AcquireAsync(stopped.Token).ConfigureAwait(false);
                using var activity = new Activity("PeerSync.ScheduledScan").SetIdFormat(ActivityIdFormat.W3C).Start();
                try
                {
                    journal?.TryWrite(new("scheduler.scan_start", "rescan") { SessionId = session });
                    result = await folder.Scanner.ScanAsync(stopped.Token).ConfigureAwait(false);
                    if (result.Problems.Any(p => p.Code is "read-failed" or "changed-during-read" or "enumeration-failed" or "published-content-verification-failed" or "problem-overflow"))
                        errorCode = "scan-deferred";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or System.Security.Cryptography.CryptographicException
                    || error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 or 14 })
                {
                    errorCode = "scan-unavailable";
                    journal?.TryWrite(new("scheduler.scan_failed", "rescan") { SessionId = session, Level = DiagnosticLevel.Warning,
                        ErrorCode = error is SqliteException sqlite ? sqlite.SqliteErrorCode switch
                        {
                            5 => "sqlite-busy", 6 => "sqlite-locked", 10 => "sqlite-io", 13 => "sqlite-full", 14 => "sqlite-open",
                            _ => "sqlite-error",
                        } : errorCode,
                        HResult = error.HResult, NativeError = error switch
                        {
                            Win32Exception native => native.NativeErrorCode,
                            SqliteException databaseError => databaseError.SqliteExtendedErrorCode,
                            _ => null,
                        } });
                }
                finally { scanLease.Dispose(); }
                if (errorCode is null) attempt = 0;
                int retry = errorCode is null ? 0 : BackoffSeconds(attempt);
                if (errorCode is not null) attempt = Math.Min(attempt + 1, 6);
                outcomes.Writer.TryWrite(new(folder.Scanner, result, errorCode, retry));
                if (result?.RequiresFollowupScan == true) requests.Writer.TryWrite(true);
                if (retry == 0) continue;
                journal?.TryWrite(new("scheduler.retry", "rescan")
                { SessionId = session, Level = DiagnosticLevel.Debug, ErrorCode = errorCode, RetryInMilliseconds = retry * 1000 });
                await Task.Delay(TimeSpan.FromSeconds(retry), clock, stopped.Token).ConfigureAwait(false);
                requests.Writer.TryWrite(true);
            }
        }

        async Task PeriodicLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(folder.RescanIntervalSeconds), clock);
            while (await timer.WaitForNextTickAsync(stopped.Token).ConfigureAwait(false)) requests.Writer.TryWrite(true);
        }

        async Task WatchLoop()
        {
            while (true)
            {
                stopped.Token.ThrowIfCancellationRequested();
                try
                {
                    using var watcher = new FolderWatcher(folder.Scanner.RootPath, clock, journal);
                    requests.Writer.TryWrite(true); // Covers changes while a failed watcher was being recreated.
                    await foreach (var batch in watcher.ReadyBatchesAsync(stopped.Token).ConfigureAwait(false))
                    {
                        requests.Writer.TryWrite(true);
                        if (batch.FullScan && batch.Reason == "watcher-overflow") break; // Recreate a failed native subscription.
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
                {
                    journal?.TryWrite(new("scheduler.watcher_retry", "watch") { SessionId = session, Level = DiagnosticLevel.Warning,
                        ErrorCode = "watcher-unavailable", HResult = error.HResult, RetryInMilliseconds = 10000 });
                    requests.Writer.TryWrite(true);
                    await Task.Delay(TimeSpan.FromSeconds(10), clock, stopped.Token).ConfigureAwait(false);
                }
            }
        }
    }

    internal static int BackoffSeconds(int attempt) => Math.Min(600, 10 * (1 << Math.Clamp(attempt, 0, 6)));

    private static async Task Guarded(Func<Task> operation, CancellationTokenSource stopping)
    {
        try { await operation().ConfigureAwait(false); }
        catch { stopping.Cancel(); throw; }
    }
}
