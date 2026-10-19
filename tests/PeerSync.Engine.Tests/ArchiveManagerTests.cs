using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ArchiveManagerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Archive-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly MutableClock clock = new();
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Content = "retained version"u8.ToArray();
    public ArchiveManagerTests() => Directory.CreateDirectory(Root);

    [Fact]
    public void MirroredNamesAreBoundedAndCollisionSuffixesPreserveTheExtension()
    {
        var at = new DateTimeOffset(2026, 10, 19, 12, 34, 56, TimeSpan.Zero);
        ArchiveNames.Candidate("dir/file.txt", at).ShouldBe(".psp/archive/dir/file~20261019-123456.txt");
        ArchiveNames.Candidate("dir/file.txt", at, 2).ShouldBe(".psp/archive/dir/file~20261019-123456-2.txt");
        string longName = string.Concat(Enumerable.Repeat("😀", 125)) + ".txt";
        string candidate = ArchiveNames.Candidate(longName, at, 1000);
        candidate.Length.ShouldBeLessThanOrEqualTo(13 + 255); PathRules.IsValid(candidate[13..]).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => ArchiveNames.Candidate("../file", at));
        Should.Throw<ArgumentOutOfRangeException>(() => ArchiveNames.Candidate("file", at, 0));
    }

    [Fact]
    public void CapacityIsResolvedFromTheHeldVolumeIdentity()
    {
        using var tree = new SafeFileTree(Root);
        ulong capacity = tree.CapacityBytes;
        capacity.ShouldBeGreaterThan(0UL);
        capacity.ShouldBeGreaterThanOrEqualTo((ulong)new DriveInfo(Path.GetPathRoot(Root)!).TotalSize);
    }

    [Fact]
    public async Task AgeBoundaryDeletesReadOnlyVersionsAndPreservesUnknownAndRecentFiles()
    {
        await using var index = await PrepareAsync();
        var expired = await AddAsync(index, "old.txt", 30); File.SetAttributes(Disk(expired.ArchivePath), FileAttributes.ReadOnly);
        var recent = await AddAsync(index, "recent.txt", 29);
        string unknown = Disk(".psp/archive/not-indexed"); File.WriteAllText(unknown, "unknown data");
        var result = await Manager(index).PruneAsync(cancellationToken: Token);
        result.Removed.ShouldBe(1); result.BytesRemoved.ShouldBe(Content.Length); result.IndexedBytesRemaining.ShouldBe(Content.Length);
        File.Exists(Disk(expired.ArchivePath)).ShouldBeFalse(); File.Exists(Disk(recent.ArchivePath)).ShouldBeTrue();
        File.ReadAllText(unknown).ShouldBe("unknown data"); index.Archive.Count().ShouldBe(1);
        result.NextRunIn.ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task VolumeCapRemovesOldestVersionsUntilTheIndexedTotalFits()
    {
        await using var index = await PrepareAsync();
        var oldest = await AddAsync(index, "first", 3); var second = await AddAsync(index, "second", 2); var newest = await AddAsync(index, "third", 1);
        var manager = new ArchiveManager(Root, index, clock: clock) { CapacityOverride = () => (ulong)Content.Length * 10 };
        var result = await manager.PruneAsync(cancellationToken: Token);
        result.Removed.ShouldBe(2); result.IndexedBytesRemaining.ShouldBe(Content.Length);
        File.Exists(Disk(oldest.ArchivePath)).ShouldBeFalse(); File.Exists(Disk(second.ArchivePath)).ShouldBeFalse();
        File.ReadAllBytes(Disk(newest.ArchivePath)).ShouldBe(Content);
    }

    [Fact]
    public async Task LockedOldestVersionDefersCapEvictionOfNewerVersionsUntilItCanBeRemoved()
    {
        await using var index = await PrepareAsync(); var oldest = await AddAsync(index, "oldest", 3); var newer = await AddAsync(index, "newer", 1);
        var manager = new ArchiveManager(Root, index, clock: clock) { CapacityOverride = () => (ulong)Content.Length * 10 };
        using (File.Open(Disk(oldest.ArchivePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await manager.PruneAsync(cancellationToken: Token);
            result.Deferred.ShouldBe(1); result.Removed.ShouldBe(0); File.Exists(Disk(newer.ArchivePath)).ShouldBeTrue();
        }
        (await manager.PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1);
        File.Exists(Disk(oldest.ArchivePath)).ShouldBeFalse(); File.Exists(Disk(newer.ArchivePath)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReplacedRootIsRejectedBeforePruningAnyOldOrNewRootFiles()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "original", 31);
        string previous = Path.Combine(fixture, "previous"); Directory.Move(Root, previous); Directory.CreateDirectory(Root);
        string replacement = Disk(row.ArchivePath); Directory.CreateDirectory(Path.GetDirectoryName(replacement)!); File.WriteAllBytes(replacement, Content);
        await Should.ThrowAsync<IOException>(() => Manager(index).PruneAsync(cancellationToken: Token));
        File.ReadAllBytes(replacement).ShouldBe(Content);
        File.ReadAllBytes(Path.Combine(previous, row.ArchivePath.Replace('/', Path.DirectorySeparatorChar))).ShouldBe(Content);
        index.Archive.Count().ShouldBe(1);
    }

    [Fact]
    public async Task MissingNewerEntriesCannotCausePrematureCapEvictionOfAnIntactOlderVersion()
    {
        await using var index = await PrepareAsync(); var retained = await AddAsync(index, "intact", 3);
        var missing = await AddAsync(index, "missing", 1); File.Delete(Disk(missing.ArchivePath));
        var manager = new ArchiveManager(Root, index, clock: clock) { CapacityOverride = () => (ulong)Content.Length * 10 };
        var result = await manager.PruneAsync(cancellationToken: Token);
        result.Missing.ShouldBe(1); result.Removed.ShouldBe(0); File.ReadAllBytes(Disk(retained.ArchivePath)).ShouldBe(Content);
    }

    [Fact]
    public async Task ChangedAndUnverifiedVersionsArePreservedAndBackoffIsBounded()
    {
        await using var index = await PrepareAsync(); var changed = await AddAsync(index, "changed", 31);
        using (var edit = new FileStream(Disk(changed.ArchivePath), FileMode.Open, FileAccess.Write)) edit.WriteByte(0);
        var unverified = await AddAsync(index, "unverified", 32);
        await index.Archive.UpsertAsync([unverified with { FileHash = null }], Token);
        var manager = Manager(index);
        foreach (int seconds in new[] { 10, 20, 40, 80, 160, 320, 600, 600 })
        {
            var result = await manager.PruneAsync(cancellationToken: Token);
            result.Deferred.ShouldBe(2); result.Removed.ShouldBe(0); result.NextRunIn.ShouldBe(TimeSpan.FromSeconds(seconds));
        }
        File.ReadAllBytes(Disk(changed.ArchivePath))[0].ShouldBe((byte)0); File.Exists(Disk(unverified.ArchivePath)).ShouldBeTrue();
        index.Meta.Find("_archive-last-success").ShouldBeNull();
    }

    [Fact]
    public async Task LockedVersionIsRetriedAndDiagnosticsNeverExposeNamesHashesOrContent()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "private-file-name", 31);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "archive", debugEnabled: true);
        var manager = new ArchiveManager(Root, index, clock: clock, journal: journal) { CapacityOverride = () => ulong.MaxValue };
        using (File.Open(Disk(row.ArchivePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            (await manager.PruneAsync(cancellationToken: Token)).Deferred.ShouldBe(1);
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        logs.ShouldContain("archive.defer"); logs.ShouldContain("\"nativeError\":32");
        logs.ShouldNotContain("private-file-name"); logs.ShouldNotContain(Encoding.UTF8.GetString(Content));
        logs.ShouldNotContain(Convert.ToHexString(row.FileHash!)); journal.DroppedEvents.ShouldBe(0);
        (await manager.PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1);
    }

    [Fact]
    public async Task AConcurrentFileCreatedAfterDeletionIsPreservedWithItsMetadataForAttention()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "concurrent", 31);
        var manager = new ArchiveManager(Root, index, clock: clock)
        {
            CapacityOverride = () => ulong.MaxValue,
            Checkpoint = stage => { if (stage == "data-deleted") File.WriteAllText(Disk(row.ArchivePath), "concurrent archive data"); },
        };
        var result = await manager.PruneAsync(cancellationToken: Token);
        result.Deferred.ShouldBe(1); result.Removed.ShouldBe(0); index.Archive.Count().ShouldBe(1);
        File.ReadAllText(Disk(row.ArchivePath)).ShouldBe("concurrent archive data");
        (await Manager(index).PruneAsync(cancellationToken: Token)).Deferred.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteSharingReaderKeepsTheRowUntilTheNativeDeleteActuallyCompletes()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "reader", 31);
        using (var reader = File.Open(Disk(row.ArchivePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await Manager(index).PruneAsync(cancellationToken: Token);
            result.Deferred.ShouldBe(1); index.Archive.Count().ShouldBe(1);
            byte[] bytes = new byte[Content.Length]; reader.ReadExactly(bytes); bytes.ShouldBe(Content);
        }
        var complete = await Manager(index).PruneAsync(cancellationToken: Token);
        complete.Missing.ShouldBe(1); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task DisposedDiagnosticOutputCannotPreventAnAuthorizedPrune()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "old", 31);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "archive", debugEnabled: true); journal.Dispose();
        var manager = new ArchiveManager(Root, index, clock: clock, journal: journal) { CapacityOverride = () => ulong.MaxValue };
        (await manager.PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1);
        File.Exists(Disk(row.ArchivePath)).ShouldBeFalse(); journal.DroppedEvents.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CancellationAfterNativeDeletionLeavesAMissingRowThatReconcilesOnRestart()
    {
        ArchiveRow row;
        await using (var index = await PrepareAsync())
        {
            row = await AddAsync(index, "interrupted", 31);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var manager = new ArchiveManager(Root, index, clock: clock)
            { CapacityOverride = () => ulong.MaxValue, Checkpoint = stage => { if (stage == "data-deleted") cancelled.Cancel(); } };
            await Should.ThrowAsync<OperationCanceledException>(() => manager.PruneAsync(cancellationToken: cancelled.Token));
            File.Exists(Disk(row.ArchivePath)).ShouldBeFalse(); index.Archive.Count().ShouldBe(1);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var result = await Manager(reopened).PruneAsync(cancellationToken: Token);
        result.Missing.ShouldBe(1); result.Removed.ShouldBe(0); reopened.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task PendingApplyDefersAllPruningUntilRecoveryFinishes()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "old", 31);
        var intent = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(intent, Token);
        var result = await Manager(index).PruneAsync(cancellationToken: Token);
        result.WaitingForRecovery.ShouldBeTrue(); result.IndexedBytesRemaining.ShouldBeNull();
        File.Exists(Disk(row.ArchivePath)).ShouldBeTrue(); index.Meta.Find("_archive-last-success").ShouldBeNull();
        await index.AbandonApplyAsync(intent, Token);
        (await Manager(index).PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1);
    }

    [Fact]
    public async Task DailySuccessPersistsAcrossRestartAndResumesAtTheRemainingInterval()
    {
        await using (var index = await PrepareAsync())
        {
            await AddAsync(index, "recent", 29); (await Manager(index).PruneAsync(cancellationToken: Token)).Removed.ShouldBe(0);
        }
        clock.Now += TimeSpan.FromHours(23);
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var result = await Manager(reopened).PruneAsync(cancellationToken: Token);
        result.NotDue.ShouldBeTrue(); result.NextRunIn.ShouldBe(TimeSpan.FromHours(1));
        clock.Now += TimeSpan.FromHours(1);
        (await Manager(reopened).PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1);
    }

    [Fact]
    public async Task DuplicateMetadataPathsFailClosedBeforeAnyDataDeletion()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "duplicate", 31);
        await index.Archive.UpsertAsync([row with { Id = 0, ArchivePath = row.ArchivePath.ToUpperInvariant() }], Token);
        await Should.ThrowAsync<InvalidDataException>(() => Manager(index).PruneAsync(cancellationToken: Token));
        File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(Content); index.Archive.Count().ShouldBe(2);
    }

    [Fact]
    public async Task ReparseArchiveAncestorIsNeverFollowedOrSwept()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "dir/file", 31);
        string moved = Path.Combine(fixture, "retained-outside"); Directory.Move(Disk(".psp/archive/dir"), moved);
        string link = Disk(".psp/archive/dir"); Directory.CreateSymbolicLink(link, moved); links.Add(link);
        var result = await Manager(index).PruneAsync(cancellationToken: Token);
        result.Deferred.ShouldBe(1); index.Archive.Count().ShouldBe(1);
        File.ReadAllBytes(Directory.GetFiles(moved).Single()).ShouldBe(Content);
    }

    [Fact]
    public async Task NativeDeletionKeepsArchiveAncestorsPinnedUntilMetadataCompletion()
    {
        await using var index = await PrepareAsync(); await AddAsync(index, "pinned/file", 31);
        int checks = 0;
        var manager = new ArchiveManager(Root, index, clock: clock)
        {
            CapacityOverride = () => ulong.MaxValue,
            Checkpoint = stage =>
            {
                if (stage is not ("before-delete" or "data-deleted")) return;
                Should.Throw<IOException>(() => Directory.Move(Disk(".psp/archive/pinned"), Path.Combine(fixture, "moved-parent")));
                checks++;
            },
        };
        (await manager.PruneAsync(cancellationToken: Token)).Removed.ShouldBe(1); checks.ShouldBe(2);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(13, 13)]
    [InlineData(14, 14)]
    [InlineData(5, 517)]
    public async Task TransientDatabaseFailuresRetryWithTheExtendedResultLogged(int primary, int extended)
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "untouched", 31);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "archive", debugEnabled: true);
        TimeSpan? due = null;
        var failureClock = new RetryClock(() => throw new SqliteException("injected database failure", primary, extended), delay =>
        { due = delay; stop.Cancel(); });
        var manager = new ArchiveManager(Root, index, clock: failureClock, journal: journal) { CapacityOverride = () => ulong.MaxValue };
        await manager.RunDailyAsync(stop.Token); due.ShouldBe(TimeSpan.FromSeconds(10));
        File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(Content); index.Meta.Find("_archive-last-success").ShouldBeNull();
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        logs.ShouldContain("archive.failed"); logs.ShouldContain("\"nativeError\":" + extended); journal.DroppedEvents.ShouldBe(0);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public async Task DatabaseCorruptionIsReportedInsteadOfRetriedAsATransientFailure(int code)
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "untouched", 31);
        var failureClock = new RetryClock(() => throw new SqliteException("injected corruption", code), _ => throw new InvalidOperationException("Must not retry"));
        var manager = new ArchiveManager(Root, index, clock: failureClock) { CapacityOverride = () => ulong.MaxValue };
        (await Should.ThrowAsync<SqliteException>(() => manager.RunDailyAsync(Token))).SqliteErrorCode.ShouldBe(code);
        File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(Content);
    }

    [Fact]
    public async Task RunningHostCanCancelAndRestartAndDoesNotPermitTwoDailyLoops()
    {
        await using var index = await PrepareAsync(); var manager = Manager(index);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task running = manager.RunDailyAsync(stopping.Token);
        await Should.ThrowAsync<InvalidOperationException>(() => manager.RunDailyAsync(Token));
        var watch = Stopwatch.StartNew();
        while (index.Meta.Find("_archive-last-success") is null && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, Token);
        index.Meta.Find("_archive-last-success").ShouldNotBeNull();
        stopping.Cancel(); await running;
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(Token); stopped.Cancel();
        await manager.RunDailyAsync(stopped.Token);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, new byte[32]); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private ArchiveManager Manager(FolderIndex index) => new(Root, index, clock: clock) { CapacityOverride = () => ulong.MaxValue };
    private async Task<ArchiveRow> AddAsync(FolderIndex index, string original, int days)
    {
        string archive = ArchiveNames.Candidate(original, clock.Now.AddDays(-days));
        Directory.CreateDirectory(Path.GetDirectoryName(Disk(archive))!); File.WriteAllBytes(Disk(archive), Content);
        await index.Archive.UpsertAsync([new(0, original, archive, clock.Now.AddDays(-days).UtcTicks, Content.Length,
            Blake3.Hasher.Hash(Content).AsSpan().ToArray())], Token);
        return index.Archive.ReadAll().Single(r => r.ArchivePath == archive);
    }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class RetryClock(Action read, Action<TimeSpan> scheduled) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() { read(); return DateTimeOffset.UtcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { scheduled(dueTime); return System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
    }
    public void Dispose()
    {
        keys.Dispose();
        // Only this test's GUID fixture is owned. Remove reparse links separately
        // so cleanup never follows the outside-target fixture through a link.
        string resolved = Path.GetFullPath(fixture);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-Archive-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Test cleanup escaped its owned fixture.");
        foreach (string link in links) Directory.Delete(link);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(resolved, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, recursive: true);
    }
}
