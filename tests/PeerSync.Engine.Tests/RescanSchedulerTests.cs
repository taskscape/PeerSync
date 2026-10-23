using PeerSync.Crypto;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class RescanSchedulerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Scheduler-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public RescanSchedulerTests() => Directory.CreateDirectory(fixture);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(3, 80)]
    [InlineData(4, 160)]
    [InlineData(5, 320)]
    [InlineData(6, 600)]
    [InlineData(100, 600)]
    public void RetryBackoffIsBounded(int attempt, int seconds) => RescanScheduler.BackoffSeconds(attempt).ShouldBe(seconds);

    [Fact]
    public async Task StartupAndConfiguredPeriodicScanRunAndCancellationDrainsWorkers()
    {
        string root = Tree("periodic");
        await File.WriteAllTextAsync(Path.Combine(root, "first"), "data", Token);
        using var keys = FolderKeys.CreateNew();
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "periodic.db"), Token);
        using var scanner = new Scanner(root, index, keys, new byte[32]);
        var scheduler = new RescanScheduler([new(scanner, 1, WatchChanges: false)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            (await Next(scheduler)).Result!.Hashed.ShouldBe(1);
            await Should.ThrowAsync<InvalidOperationException>(() => scheduler.RunAsync(Token));
            await File.WriteAllTextAsync(Path.Combine(root, "second"), "periodic change", Token);
            var next = await Next(scheduler);
            next.Result.ShouldNotBeNull(); next.Result.Hashed.ShouldBe(1);
            index.LocalFiles.Find("second").ShouldNotBeNull();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        // The same host object can restart cleanly after cancellation.
        using var restarting = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task restarted = scheduler.RunAsync(restarting.Token);
        try { (await Next(scheduler)).Result!.Updated.ShouldBe(0); }
        finally { restarting.Cancel(); await restarted.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task ScheduledAndScannerDiagnosticsShareATraceWithoutNamesOrContent()
    {
        string root = Tree("diagnostics");
        await File.WriteAllTextAsync(Path.Combine(root, "private-file-name"), "private file content", Token);
        using var keys = FolderKeys.CreateNew();
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "scheduler", debugEnabled: true);
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "diagnostics.db"), Token);
        using var scanner = new Scanner(root, index, keys, new byte[32], journal: journal);
        var scheduler = new RescanScheduler([new(scanner, WatchChanges: false)], journal: journal);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            (await Next(scheduler)).Result!.Updated.ShouldBe(1);
            string text = await File.ReadAllTextAsync(Path.Combine(fixture, "logs", "debug.jsonl"), Token);
            text.ShouldNotContain("private-file-name"); text.ShouldNotContain("private file content");
            var traces = new HashSet<string>();
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                string name = json.RootElement.GetProperty("eventName").GetString()!;
                if (name is "scheduler.scan_start" or "scan.start" or "scan.complete")
                    traces.Add(json.RootElement.GetProperty("traceId").GetString()!);
            }
            traces.Count.ShouldBe(1); traces.Single().Length.ShouldBe(32);
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task NativeWatcherSchedulesAChangedFileBeforeTheTenMinuteRescan()
    {
        string root = Tree("watched");
        using var keys = FolderKeys.CreateNew();
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "watched.db"), Token);
        using var scanner = new Scanner(root, index, keys, new byte[32]);
        var scheduler = new RescanScheduler([new(scanner)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            (await Next(scheduler)).Result!.Enumerated.ShouldBe(0);
            await File.WriteAllTextAsync(Path.Combine(root, "new"), "watcher data", Token);
            var changed = await Next(scheduler);
            changed.Result!.Hashed.ShouldBe(1); index.LocalFiles.Find("new").ShouldNotBeNull();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task CoordinatedWatcherScansThroughFolderPassWithoutNestingScanPermit()
    {
        string root = Tree("coordinated");
        using var keys = FolderKeys.CreateNew();
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "coordinated.db"), Token);
        await using var folder = new TransportFolder(root, index, keys, new byte[32]);
        var scheduler = new RescanScheduler([new(folder.Scanner, Coordinator: folder)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            var initial = await Next(scheduler);
            initial.Scanner.ShouldBeSameAs(folder.Scanner);
            initial.Result.ShouldBeNull();
            initial.Pass.ShouldNotBeNull();
            initial.Pass.ScansCompleted.ShouldBe(1);

            await File.WriteAllTextAsync(Path.Combine(root, "new"), "coordinated watcher data", Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            ScanOutcome changed;
            do { changed = await scheduler.Outcomes.ReadAsync(timeout.Token); }
            while (changed.Pass?.ScansCompleted == 0 || index.LocalFiles.Find("new") is null);
            changed.ErrorCode.ShouldBeNull();
            changed.Pass!.ScannedEntries.ShouldBeGreaterThan(0);
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task ProcessWidePermitSerializesSchedulersAndCancelledWaitersDoNotReleaseIt()
    {
        string firstRoot = Tree("first"), secondRoot = Tree("second");
        await File.WriteAllTextAsync(Path.Combine(firstRoot, "file"), "first", Token);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "file"), "second", Token);
        using var keys = FolderKeys.CreateNew();
        await using var firstIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "first.db"), Token);
        await using var secondIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "second.db"), Token);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var first = new Scanner(firstRoot, firstIndex, keys, new byte[32], timeProvider: new CallbackClock(() =>
        {
            firstEntered.TrySetResult(); release.Wait(Token);
        }));
        using var second = new Scanner(secondRoot, secondIndex, keys, new byte[32], timeProvider: new CallbackClock(() => secondEntered.TrySetResult()));
        var firstScheduler = new RescanScheduler([new(first, WatchChanges: false)]);
        var secondScheduler = new RescanScheduler([new(second, WatchChanges: false)]);
        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stopSecond = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task firstRun = firstScheduler.RunAsync(stopFirst.Token);
        Task? secondRun = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            secondRun = secondScheduler.RunAsync(stopSecond.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
            secondEntered.Task.IsCompleted.ShouldBeFalse();
            stopSecond.Cancel(); await secondRun.WaitAsync(TimeSpan.FromSeconds(5), Token);
            secondIndex.LocalFiles.Count().ShouldBe(0);
            release.Set(); (await Next(firstScheduler)).Result!.Updated.ShouldBe(1);
        }
        finally
        {
            release.Set(); stopFirst.Cancel(); stopSecond.Cancel();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (secondRun is not null) await secondRun.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        using var resumed = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task resumedRun = secondScheduler.RunAsync(resumed.Token);
        try { (await Next(secondScheduler)).Result!.Updated.ShouldBe(1); }
        finally { resumed.Cancel(); await resumedRun.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task DeferredFileReportsBackoffAndReleasesBudgetForAnotherFolder()
    {
        string lockedRoot = Tree("locked"), otherRoot = Tree("other");
        await File.WriteAllTextAsync(Path.Combine(lockedRoot, "file"), "locked", Token);
        await File.WriteAllTextAsync(Path.Combine(otherRoot, "file"), "other", Token);
        using var keys = FolderKeys.CreateNew();
        await using var lockedIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "locked.db"), Token);
        await using var otherIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "other.db"), Token);
        using var lockedScanner = new Scanner(lockedRoot, lockedIndex, keys, new byte[32]);
        using var otherScanner = new Scanner(otherRoot, otherIndex, keys, new byte[32]);
        using var locked = File.Open(Path.Combine(lockedRoot, "file"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scheduler = new RescanScheduler([new(lockedScanner, WatchChanges: false)]);
        var otherScheduler = new RescanScheduler([new(otherScanner, WatchChanges: false)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        Task? other = null;
        try
        {
            var deferred = await Next(scheduler);
            deferred.ErrorCode.ShouldBe("scan-deferred"); deferred.RetryInSeconds.ShouldBe(10);
            other = otherScheduler.RunAsync(stopping.Token);
            (await Next(otherScheduler)).Result!.Updated.ShouldBe(1);
        }
        finally
        {
            stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (other is not null) await other.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 517)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(13, 13)]
    [InlineData(14, 14)]
    public async Task RecoverableSqliteFailuresBackOffWithoutStoppingOtherFolders(int code, int extendedCode)
    {
        string failedRoot = Tree("failed"), healthyRoot = Tree("healthy");
        File.WriteAllText(Path.Combine(failedRoot, "file"), "first"); File.WriteAllText(Path.Combine(healthyRoot, "file"), "second");
        using var keys = FolderKeys.CreateNew();
        await using var failedIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "failed.db"), Token);
        await using var healthyIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "healthy.db"), Token);
        // Inject the database exception through the existing clock seam; exercise the real scheduler loop.
        using var failed = new Scanner(failedRoot, failedIndex, keys, new byte[32], timeProvider: new CallbackClock(() => throw new SqliteException("injected", code, extendedCode)));
        using var healthy = new Scanner(healthyRoot, healthyIndex, keys, new byte[32]);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "sqlite-logs"), "scheduler");
        var scheduler = new RescanScheduler([new(failed, WatchChanges: false), new(healthy, WatchChanges: false)], journal: journal);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token); Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            var outcomes = new[] { await Next(scheduler), await Next(scheduler) };
            outcomes.Single(o => o.Scanner == failed).RetryInSeconds.ShouldBe(10);
            outcomes.Single(o => o.Scanner == healthy).Result!.Updated.ShouldBe(1); running.IsCompleted.ShouldBeFalse();
            bool found = false;
            foreach (string line in File.ReadAllLines(Path.Combine(fixture, "sqlite-logs", "debug.jsonl")))
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.GetProperty("eventName").GetString() != "scheduler.scan_failed") continue;
                found = true;
                json.RootElement.GetProperty("errorCode").GetString()!.ShouldStartWith("sqlite-");
                json.RootElement.GetProperty("nativeError").GetInt32().ShouldBe(extendedCode);
            }
            found.ShouldBeTrue();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public async Task NonretryableDatabaseCorruptionRemainsFatalAndReleasesTheScanBudget(int code)
    {
        string failedRoot = Tree("failed"), healthyRoot = Tree("healthy");
        File.WriteAllText(Path.Combine(failedRoot, "file"), "first"); File.WriteAllText(Path.Combine(healthyRoot, "file"), "second");
        using var keys = FolderKeys.CreateNew();
        await using var failedIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "failed.db"), Token);
        await using var healthyIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "healthy.db"), Token);
        using var failed = new Scanner(failedRoot, failedIndex, keys, new byte[32], timeProvider: new CallbackClock(() => throw new SqliteException("injected corruption", code)));
        using var healthy = new Scanner(healthyRoot, healthyIndex, keys, new byte[32]);
        var scheduler = new RescanScheduler([new(failed, WatchChanges: false)]);
        (await Should.ThrowAsync<SqliteException>(() => scheduler.RunAsync(Token))).SqliteErrorCode.ShouldBe(code);
        var other = new RescanScheduler([new(healthy, WatchChanges: false)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token); Task running = other.RunAsync(stopping.Token);
        try { (await Next(other)).Result!.Updated.ShouldBe(1); }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task PublicHistoryCopiesReceiveAFollowupScanWithoutWaitingForWatcherOrPeriodicTimer()
    {
        string root = Tree("history"); File.WriteAllText(Path.Combine(root, "file"), "old published bytes");
        using var keys = FolderKeys.CreateNew(); await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "history.db"), Token);
        using var scanner = new Scanner(root, index, keys, new byte[32], settings: new FolderSettings { KeepBothCopies = true });
        await scanner.ScanAsync(Token); File.WriteAllText(Path.Combine(root, "file"), "longer changed user bytes");
        var scheduler = new RescanScheduler([new(scanner, WatchChanges: false)]); using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            (await Next(scheduler)).Result!.RequiresFollowupScan.ShouldBeTrue();
            var followup = await Next(scheduler); followup.Result!.RequiresFollowupScan.ShouldBeFalse();
            string copy = Directory.GetFiles(root, "*.sync-conflict-*").Single(); File.ReadAllText(copy).ShouldBe("old published bytes");
            index.LocalFiles.Find(Path.GetFileName(copy))!.Record.ShouldNotBeNull(); index.Archive.Count().ShouldBe(0);
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task PublishedContentVerificationFailureBacksOffAndReleasesBudgetForOtherFolders()
    {
        string badRoot = Tree("bad-content"), goodRoot = Tree("good-content");
        using var keys = FolderKeys.CreateNew(); await using var badIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "bad.db"), Token);
        await using var goodIndex = await FolderIndex.OpenAsync(Path.Combine(fixture, "good.db"), Token);
        using var bad = new Scanner(badRoot, badIndex, keys, new byte[32]); using var good = new Scanner(goodRoot, goodIndex, keys, new byte[32]);
        await bad.ScanAsync(Token); byte[] bytes = "content with corrupt cache"u8.ToArray(); string hash = Convert.ToHexString(Blake3.Hasher.Hash(bytes).AsSpan());
        string parent = Path.Combine(badRoot, ".psp", "blocks", hash[..2]); Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, hash), new byte[bytes.Length]); File.WriteAllBytes(Path.Combine(badRoot, "file"), bytes);
        File.WriteAllText(Path.Combine(goodRoot, "file"), "good folder bytes");
        var scheduler = new RescanScheduler([new(bad, WatchChanges: false), new(good, WatchChanges: false)]);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token); Task running = scheduler.RunAsync(stopping.Token);
        try
        {
            var outcomes = new[] { await Next(scheduler), await Next(scheduler) };
            outcomes.Single(o => o.Scanner == bad).RetryInSeconds.ShouldBe(10);
            outcomes.Single(o => o.Scanner == good).Result!.Updated.ShouldBe(1); running.IsCompleted.ShouldBeFalse();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    private static async Task<ScanOutcome> Next(RescanScheduler scheduler)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await scheduler.Outcomes.ReadAsync(timeout.Token);
    }
    private string Tree(string name) { string path = Path.Combine(fixture, name); Directory.CreateDirectory(path); return path; }
    public void Dispose() => Directory.Delete(fixture, recursive: true);
    private sealed class CallbackClock(Action callback) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() { callback(); return DateTimeOffset.UtcNow; }
    }
}
