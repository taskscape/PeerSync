using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class CaseCopyRecoveryTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-CaseCopy-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string First = "first case content", Second = "second case content";
    public CaseCopyRecoveryTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData("case-reserved")]
    [InlineData("case-prepared")]
    [InlineData("case-install-ready")]
    [InlineData("case-installed")]
    [InlineData("case-accounted")]
    public async Task RestartAtEveryCopyBoundaryPreservesBothVersionsWithoutPublishingTheOriginalEarly(string stopAt)
    {
        await using (var index = await Prepare())
        {
            var applier = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == stopAt) throw new Stop(); } };
            (await applier.ApplyAsync("file.txt", FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Blocked);
            await Should.ThrowAsync<Stop>(() => applier.PrepareCaseCopiesAsync(Fetch, Token));
            index.HasPendingCasePreservation("file.txt").ShouldBeTrue(); index.LocalFiles.Find("file.txt").ShouldBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var recovered = new FileApplier(Root, reopened, keys, Device(1)); await recovered.RecoverAsync(Token);
        await Finish(reopened, recovered, keys);
        RequireVersions(First, Second); reopened.ReadApplyIntents().ShouldBeEmpty(); reopened.ReadPendingCasePreservations().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyAndReadWriteCopiesRequireScanBeforeOriginalResolution(bool readOnly)
    {
        using var access = FolderKeys.FromText(readOnly ? keys.ExportReadOnlyText() : keys.ExportText());
        await using var index = await Prepare(access);
        var clock = new MutableClock(); var applier = new FileApplier(Root, index, access, Device(1), clock: clock);
        (await applier.PrepareCaseCopiesAsync(Fetch, Token)).ShouldBeTrue();
        (await applier.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        (await applier.ApplyAsync("file.txt", FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Blocked);
        var installed = index.ReadPendingCasePreservations().Single();
        index.LocalFiles.Find(installed.DestinationPath!).ShouldBeNull();
        clock.Advance(TimeSpan.FromSeconds(10));
        await Finish(index, applier, access); RequireVersions(First, Second);
        var copy = index.LocalFiles.Find(installed.DestinationPath!)!;
        if (readOnly) { copy.Record.ShouldBeNull(); index.ReadIndexBatch(0).Records.ShouldNotContain(row => row.Path == copy.Path); }
        else { copy.Record.ShouldNotBeNull(); copy.ContentReady.ShouldBeTrue(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownFileOrDirectoryAtStableNameSurvivesAndCopyUsesAnotherSlot(bool directory)
    {
        await using var index = await Prepare(); var pending = index.ReadPendingCasePreservations().Single();
        var version = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        string occupied = ConflictNames.CaseSourcePath(version.Path, pending.Source.SourceId);
        if (directory) { Directory.CreateDirectory(Disk(occupied)); File.WriteAllText(Disk(occupied + "/user.txt"), "unknown directory child"); }
        else File.WriteAllText(Disk(occupied), "unknown occupant");
        var applier = new FileApplier(Root, index, keys, Device(1)); await Finish(index, applier, keys);
        if (directory) File.ReadAllText(Disk(occupied + "/user.txt")).ShouldBe("unknown directory child");
        else File.ReadAllText(Disk(occupied)).ShouldBe("unknown occupant");
        Directory.GetFiles(Root).Select(File.ReadAllText).ShouldContain(First);
        File.ReadAllText(Disk("FILE.txt")).ShouldBe(Second);
    }

    [Fact]
    public async Task PreparedReparseDestinationReleasesRecoveryAndAllowsAnotherSafeSlot()
    {
        await using var index = await Prepare();
        var first = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == "case-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(Fetch, Token));
        var state = CaseCopyState.Decode(index.ReadApplyIntents().Single(), keys);
        string outside = Path.Combine(fixture, "outside-recovery"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "user.txt"), "outside recovery bytes");
        string link = Disk(state.Destination); Directory.CreateSymbolicLink(link, outside); links.Add(link);
        var recovered = new FileApplier(Root, index, keys, Device(1)); await recovered.RecoverAsync(Token);
        index.ReadApplyIntents().ShouldBeEmpty(); await Finish(index, recovered, keys); RequireVersions(First, Second);
        Directory.GetFiles(outside).Length.ShouldBe(1); File.ReadAllText(Path.Combine(outside, "user.txt")).ShouldBe("outside recovery bytes");
    }

    [Fact]
    public async Task StartupCapacityBackpressureDrainsAdmittedSourcesBeforeRepairingTheRest()
    {
        const int count = 1001; await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var rows = new List<RemoteFileRow>(count * 2); SignedRecord? lastWinner = null;
        for(int i=0;i<count;i++)
        {
            string path="file"+i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".txt";
            var a=Record(path,2,1,10,First,1); var b=Record(path.ToUpperInvariant(),3,1,20,Second,1);
            lastWinner=b;
            rows.Add(new(Device(2),path,VersionView.Open(a,keys,Device(2)).Vector,a)); rows.Add(new(Device(3),path,VersionView.Open(b,keys,Device(3)).Vector,b));
        }
        // Existing authenticated remote rows require startup reconstruction of needed work.
        await index.RemoteFiles.UpsertAsync(rows,Token);
        var legacy=VersionView.Open(lastWinner!,keys,Device(3));
        await index.Needed.UpsertAsync([new(legacy.Path,Device(3),1) { Work=new(1,legacy.Size,legacy.MtimeTicks,new byte[32]) }],Token);
        using var puller=new Puller(Root,index,keys,Device(1),Fetch);
        var initial=await puller.RunPassAsync(Token); initial.Visited.ShouldBe(0,"unrepaired high-priority legacy work must not merge away an unadmitted case source");
        File.Exists(Disk(legacy.Path)).ShouldBeFalse();
        Directory.GetFiles(Root).Any(path=>Path.GetFileName(path).Contains(".case-conflict",StringComparison.Ordinal)).ShouldBeTrue();
        for(int pass=0;pass<6;pass++) await puller.RunPassAsync(Token);
        index.ReadPendingCasePreservations().ShouldBeEmpty(); index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        Directory.GetFiles(Root).Count(path=>Path.GetFileName(path).Contains(".case-conflict",StringComparison.Ordinal)).ShouldBe(count);
        for(int i=0;i<count;i++) File.ReadAllText(Disk("FILE"+i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".TXT")).ShouldBe(Second);
    }

    [Fact]
    public async Task UnknownReparseCollisionCannotRedirectTheCopyOrStrandOriginalResolution()
    {
        await using var index = await Prepare(); var pending = index.ReadPendingCasePreservations().Single();
        var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        string occupied = Disk(ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId));
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "user.txt"), "outside bytes");
        Directory.CreateSymbolicLink(occupied, outside); links.Add(occupied);
        await Finish(index, new(Root, index, keys, Device(1)), keys);
        File.ReadAllText(Path.Combine(outside, "user.txt")).ShouldBe("outside bytes"); Directory.GetFiles(outside).Length.ShouldBe(1);
        RequireVersions(First, Second);
    }

    [Theory]
    [InlineData((uint)FileAttributes.Hidden)]
    [InlineData((uint)FileAttributes.ReadOnly)]
    public async Task RecoveryAfterMoveRestoresSignedCaseCopyAttributesBeforePublication(uint attributes)
    {
        await using var index = await Prepare(attributes: attributes);
        var applier = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == "case-installed") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => applier.PrepareCaseCopiesAsync(Fetch, Token));
        var recovered = new FileApplier(Root, index, keys, Device(1)); await recovered.RecoverAsync(Token);
        string destination = index.ReadPendingCasePreservations().Single().DestinationPath!;
        ((uint)File.GetAttributes(Disk(destination)) & attributes).ShouldBe(attributes);
        await Finish(index, recovered, keys); RequireVersions(First, Second);
    }

    [Fact]
    public async Task ConcurrentDestinationCreationNeverOverwritesEvenWithIdenticalPayload()
    {
        await using var index = await Prepare(); bool collided = false; string? occupied = null;
        var clock = new MutableClock(); var applier = new FileApplier(Root, index, keys, Device(1), clock: clock) { Checkpoint = stage =>
        {
            if (stage != "case-install-ready" || collided) return;
            collided = true; occupied = index.ReadPendingCasePreservations().Single().DestinationPath!;
            File.WriteAllText(Disk(occupied), First);
        } };
        (await applier.PrepareCaseCopiesAsync(Fetch, Token)).ShouldBeFalse();
        File.ReadAllText(Disk(occupied!)).ShouldBe(First); index.ReadApplyIntents().ShouldBeEmpty();
        clock.Advance(TimeSpan.FromSeconds(10)); await Finish(index, new(Root, index, keys, Device(1), clock: clock), keys);
        Directory.GetFiles(Root).Count(path => !path.EndsWith(".!psp", StringComparison.Ordinal) && File.ReadAllText(path) == First).ShouldBe(2);
    }

    [Fact]
    public async Task EditedPreparedStagingIsProtectedAndNeverReusedOrOverwritten()
    {
        await using var index = await Prepare();
        var applier = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == "case-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => applier.PrepareCaseCopiesAsync(Fetch, Token));
        var state = CaseCopyState.Decode(index.ReadApplyIntents().Single(), keys);
        using (var stream = new FileStream(Disk(state.Temporary), FileMode.Open, FileAccess.Write, FileShare.None))
        { stream.Write("edited prepared bytes"u8); stream.SetLength("edited prepared bytes"u8.Length); stream.Flush(flushToDisk: true); }
        var recovered = new FileApplier(Root, index, keys, Device(1)); await recovered.RecoverAsync(Token);
        await Finish(index, recovered, keys);
        File.ReadAllText(Disk(state.Temporary)).ShouldBe("edited prepared bytes");
        index.ReadReusableStaging(state.Root, state.Destination, false).ShouldBeEmpty(); RequireVersions(First, Second);
    }

    [Fact]
    public async Task ReplacedRemoteRowCannotDiscardThePreviouslyAdmittedSource()
    {
        await using var index = await Prepare(); var needed = new NeededSet(index, keys, Device(1));
        var replacement = Record("file.txt", 2, 2, 11, "third case content", 2);
        await needed.ApplyIndexUpdateAsync(Device(2), 1, 2, [replacement], Token);
        index.ReadPendingCasePreservations().Count.ShouldBe(2);
        ValueTask<ReadOnlyMemory<byte>> FetchAll(ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation) =>
            hash.Span.SequenceEqual(Blake3.Hasher.Hash("third case content"u8).AsSpan())
                ? ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("third case content")) : Fetch(default, hash, length, cancellation);
        var applier = new FileApplier(Root, index, keys, Device(1));
        await applier.PrepareCaseCopiesAsync(FetchAll, Token);
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        (await applier.CompleteCaseCopiesAsync(Token)).Count.ShouldBe(2);
        await needed.RecomputeAsync(["file.txt"], Token);
        (await applier.ApplyAsync("file.txt", FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Applied);
        RequireVersions(First, Second, "third case content");
    }

    [Fact]
    public async Task UnavailableCaseSourceBacksOffWhileIndependentCaseAndNormalWorkProgress()
    {
        await using var index = await Prepare(); var clock = new MutableClock(); var needed = new NeededSet(index, keys, Device(1));
        await needed.ApplyIndexUpdateAsync(Device(4), 0, 1, [Record("other.txt", 4, 1, 10, "independent source", 1)], Token);
        await needed.ApplyIndexUpdateAsync(Device(5), 0, 1, [Record("OTHER.txt", 5, 1, 20, "independent winner", 1)], Token);
        await needed.ApplyIndexUpdateAsync(Device(6), 0, 1, [Record("good.txt", 6, 1, 20, "normal work", 1)], Token);
        bool available = false; int attempts = 0;
        ValueTask<ReadOnlyMemory<byte>> FetchAll(ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation)
        {
            if (hash.Span.SequenceEqual(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(First)).AsSpan()))
            { attempts++; if (!available) throw new IOException("source unavailable"); }
            foreach (string payload in new[] { First, Second, "independent source", "independent winner", "normal work" })
                if (hash.Span.SequenceEqual(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(payload)).AsSpan())) return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(payload));
            throw new InvalidDataException("Unexpected test hash.");
        }
        using (var first = new Puller(Root, index, keys, Device(1), FetchAll, timeProvider: clock))
        {
            var pass = await first.RunPassAsync(Token); pass.NextAttemptTicks.ShouldBe(clock.GetUtcNow().UtcTicks + 10 * TimeSpan.TicksPerSecond);
            File.ReadAllText(Disk("good.txt")).ShouldBe("normal work"); File.ReadAllText(Disk("OTHER.txt")).ShouldBe("independent winner");
            var pending = index.ReadPendingCasePreservations().Single(); index.ReadCasePreservationRetry(pending.Source.SourceId)!.Value.Attempts.ShouldBe(1);
            await first.RunPassAsync(Token); attempts.ShouldBe(1);
        }
        available = true; clock.Advance(TimeSpan.FromSeconds(10));
        using var restarted = new Puller(Root, index, keys, Device(1), FetchAll, timeProvider: clock); await restarted.RunPassAsync(Token);
        attempts.ShouldBe(2); index.ReadPendingCasePreservations().ShouldBeEmpty(); index.Needed.Count().ShouldBe(0);
        File.ReadAllText(Disk("FILE.txt")).ShouldBe(Second);
    }

    [Theory]
    [InlineData("case-reserved")]
    [InlineData("case-prepared")]
    [InlineData("case-installed")]
    public async Task CancellationRetainsRecoverableProofAndNeverCompletesTheSource(string cancelAt)
    {
        await using var index = await Prepare(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var applier = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == cancelAt) stop.Cancel(); } };
        await Should.ThrowAsync<OperationCanceledException>(() => applier.PrepareCaseCopiesAsync(Fetch, stop.Token));
        index.ReadPendingCasePreservations().Count.ShouldBe(1); index.ReadApplyIntents().Count().ShouldBe(1);
        var recovered = new FileApplier(Root, index, keys, Device(1)); await recovered.RecoverAsync(Token); await Finish(index, recovered, keys);
        RequireVersions(First, Second);
    }

    [Fact]
    public async Task HostedTimerWakesForCaseOnlyDeadlineWithoutOrdinaryNeededWork()
    {
        await using var index = await Prepare();
        var winner = VersionView.Open(index.RemotesForPath("file.txt").Single(row => row.DeviceId.SequenceEqual(Device(3))).Record, keys, Device(3));
        File.WriteAllText(Disk("FILE.txt"), Second); File.SetLastWriteTimeUtc(Disk("FILE.txt"), new DateTime(winner.MtimeTicks, DateTimeKind.Utc));
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        // Simulate a retained case-only obligation after original metadata settled.
        var merged = new Dictionary<ulong, ulong> { [1] = 1, [2] = 1, [3] = 1 };
        var actual = index.LocalFiles.Find("FILE.txt")!;
        await index.LocalFiles.UpsertAsync([actual with { Record = ConflictResolver.MergeRecord(winner, merged, keys), Vector = merged }], Token);
        await new NeededSet(index, keys, Device(1)).RecomputeAsync(["file.txt"], Token); index.Needed.Count().ShouldBe(0);
        var clock = new TimerClock(); bool available = false;
        ValueTask<ReadOnlyMemory<byte>> FetchWhenAvailable(ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation)
        { if (!available) throw new IOException("case-only source unavailable"); return Fetch(default, hash, length, cancellation); }
        using var puller = new Puller(Root, index, keys, Device(1), FetchWhenAvailable, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); Task run = puller.RunAsync(stop.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var pass = await puller.Outcomes.ReadAsync(deadline.Token); pass.Visited.ShouldBe(0);
            var timer = await clock.Timers.Reader.ReadAsync(deadline.Token); timer.Delay.ShouldBe(TimeSpan.FromSeconds(10));
            available = true; clock.Advance(TimeSpan.FromSeconds(10)); timer.Fire();
            await puller.Outcomes.ReadAsync(deadline.Token); index.ReadPendingCasePreservations().ShouldBeEmpty(); index.Needed.Count().ShouldBe(0);
            RequireVersions(First, Second);
        }
        finally { stop.Cancel(); await run; }
    }

    [Fact]
    public async Task ReadyAuthenticatedGossipCopyAvoidsAnotherCollisionSlot()
    {
        await using var index = await Prepare(); var pending = index.ReadPendingCasePreservations().Single();
        var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        string copyPath = ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId);
        var needed = new NeededSet(index, keys, Device(1));
        await needed.ApplyIndexUpdateAsync(Device(4), 0, 1, [Record(copyPath, 4, 1, 10, First, 1)], Token);
        var applier = new FileApplier(Root, index, keys, Device(1));
        (await applier.ApplyAsync(copyPath, FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Applied);
        await Finish(index, applier, keys);
        RequireVersions(First, Second); index.ReadPendingCasePreservations().ShouldBeEmpty();
    }

    [Fact]
    public async Task LocalRescanCannotSuppressAdmissionOfConcurrentCaseSources()
    {
        File.WriteAllText(Disk("file.txt"), First); File.SetLastWriteTimeUtc(Disk("file.txt"), DateTime.UtcNow.AddHours(-2));
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var previous = index.LocalFiles.Find("file.txt")!;
        await index.LocalFiles.UpsertAsync([previous with { Flags = LocalFileState.NeedsRescan }], Token);
        await new NeededSet(index, keys, Device(1)).ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("FILE.txt", 2, 1, 20, Second, 1)], Token);
        var pending = index.ReadPendingCasePreservations().Single(); pending.Source.Record.Body.ToArray().ShouldBe(previous.Record!.Body.ToArray());
        index.HasPendingCasePreservation("file.txt").ShouldBeTrue();
    }

    [Fact]
    public async Task CaseArrivalDuringOrdinaryDownloadDefersBeforeReplacingTheOriginal()
    {
        File.WriteAllText(Disk("file.txt"), First); File.SetLastWriteTimeUtc(Disk("file.txt"), DateTime.UtcNow.AddHours(-2));
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var needed = new NeededSet(index, keys, Device(1));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("file.txt", 2, 1, 10, Second, 1)], Token);
        bool arrived = false; using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var applier = new FileApplier(Root, index, keys, Device(1));
        async ValueTask<ReadOnlyMemory<byte>> FetchDuring(ReadOnlyMemory<byte> _, int length, CancellationToken cancellation)
        {
            arrived = true;
            await needed.ApplyIndexUpdateAsync(Device(3), 0, 1, [Record("FILE.txt", 3, 1, 20, "third case content", 1)], cancellation);
            return Encoding.UTF8.GetBytes(Second);
        }
        (await applier.ApplyAsync("file.txt", FetchDuring, deadline.Token)).ShouldBe(FileApplyOutcome.Deferred);
        arrived.ShouldBeTrue(); File.ReadAllText(Disk("file.txt")).ShouldBe(First);
        index.ReadPendingCasePreservations().Count.ShouldBe(2); index.ReadApplyIntents().ShouldBeEmpty();
        ValueTask<ReadOnlyMemory<byte>> FetchAll(ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation) =>
            hash.Span.SequenceEqual(Blake3.Hasher.Hash("third case content"u8).AsSpan())
                ? ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("third case content")) : Fetch(default, hash, length, cancellation);
        await applier.PrepareCaseCopiesAsync(FetchAll, Token);
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var paths = await applier.CompleteCaseCopiesAsync(Token); paths.Count.ShouldBe(2); await needed.RecomputeAsync(paths, Token);
        (await applier.ApplyAsync("file.txt", (hash, length, cancellation) => FetchAll(default, hash, length, cancellation), Token)).ShouldBe(FileApplyOutcome.Applied);
        RequireVersions(First, Second, "third case content");
    }

    [Fact]
    public async Task CaseDiagnosticsContainStagesAndCorrelationWithoutPrivateNamesBodiesOrHashes()
    {
        await using var index = await Prepare(); using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "case", debugEnabled: true);
        await Finish(index, new(Root, index, keys, Device(1), journal: journal), keys);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("apply.case-reserved"); log.ShouldContain("apply.case-accounted"); log.ShouldContain("apply.case-completed"); log.ShouldContain("sessionId");
        log.ShouldNotContain("file.txt"); log.ShouldNotContain(First); log.ShouldNotContain(Root); log.ShouldNotContain(keys.ExportText());
    }

    private async Task<FolderIndex> Prepare(FolderKeys? access = null, uint attributes = 0)
    {
        access ??= keys; var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, access, Device(1))) await scanner.ScanAsync(Token);
        var needed = new NeededSet(index, access, Device(1));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("file.txt", 2, 1, 10, First, 1, attributes)], Token);
        await needed.ApplyIndexUpdateAsync(Device(3), 0, 1, [Record("FILE.txt", 3, 1, 20, Second, 1)], Token);
        index.ReadPendingCasePreservations().Count.ShouldBe(1); return index;
    }
    private async Task Finish(FolderIndex index, FileApplier applier, FolderKeys access)
    {
        await applier.PrepareCaseCopiesAsync(Fetch, Token);
        using (var scanner = new Scanner(Root, index, access, Device(1))) await scanner.ScanAsync(Token);
        var paths = await applier.CompleteCaseCopiesAsync(Token); await new NeededSet(index, access, Device(1)).RecomputeAsync(paths, Token);
        (await applier.ApplyAsync("file.txt", FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Applied);
    }
    private SignedRecord Record(string path, ulong author, ulong counter, long time, string content, ulong sequence, uint attributes = 0)
    {
        long ticks = DateTime.UtcNow.AddHours(-1).Ticks + time * TimeSpan.TicksPerSecond;
        var body = ConflictResolverTests.Body(author, counter, ticks, ticks, content, false); body.Path = path; body.Attributes = attributes;
        return SignedRecordFactory.Create(body, keys, sequence);
    }
    private static ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> hash, int length, CancellationToken token) => FetchWithoutPeer(hash, length, token);
    private static ValueTask<ReadOnlyMemory<byte>> FetchWithoutPeer(ReadOnlyMemory<byte> hash, int _, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[] first = Encoding.UTF8.GetBytes(First), second = Encoding.UTF8.GetBytes(Second);
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(hash.Span.SequenceEqual(Blake3.Hasher.Hash(first).AsSpan()) ? first : second);
    }
    private void RequireVersions(params string[] versions) => Directory.GetFiles(Root).Where(path => !path.EndsWith(".!psp", StringComparison.Ordinal)).Select(File.ReadAllText)
        .Order(StringComparer.Ordinal).ToArray().ShouldBe(versions.Order(StringComparer.Ordinal).ToArray());
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private static byte[] Device(ulong number) { byte[] device = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(device, number); return device; }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-CaseCopy-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        foreach (string link in links) Directory.Delete(link);
        keys.Dispose(); foreach (string file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, true);
    }
    private sealed class Stop : Exception;
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class TimerClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        internal Channel<ManualTimer> Timers { get; } = Channel.CreateUnbounded<ManualTimer>();
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state, dueTime); Timers.Writer.TryWrite(timer); return timer; }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private int disposed;
        internal TimeSpan Delay { get; } = delay;
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
        internal void Fire() { if (Interlocked.Exchange(ref disposed, 1) == 0) callback(state); }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
