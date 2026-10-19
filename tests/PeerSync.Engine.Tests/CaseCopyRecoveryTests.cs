using System.Buffers.Binary;
using System.Text;
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
        var applier = new FileApplier(Root, index, access, Device(1));
        (await applier.PrepareCaseCopiesAsync(Fetch, Token)).ShouldBeTrue();
        (await applier.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        (await applier.ApplyAsync("file.txt", FetchWithoutPeer, Token)).ShouldBe(FileApplyOutcome.Blocked);
        var installed = index.ReadPendingCasePreservations().Single();
        index.LocalFiles.Find(installed.DestinationPath!).ShouldBeNull();
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
    public async Task ConcurrentDestinationCreationNeverOverwritesEvenWithIdenticalPayload()
    {
        await using var index = await Prepare(); bool collided = false; string? occupied = null;
        var applier = new FileApplier(Root, index, keys, Device(1)) { Checkpoint = stage =>
        {
            if (stage != "case-install-ready" || collided) return;
            collided = true; occupied = index.ReadPendingCasePreservations().Single().DestinationPath!;
            File.WriteAllText(Disk(occupied), First);
        } };
        (await applier.PrepareCaseCopiesAsync(Fetch, Token)).ShouldBeFalse();
        File.ReadAllText(Disk(occupied!)).ShouldBe(First); index.ReadApplyIntents().ShouldBeEmpty();
        await Finish(index, new(Root, index, keys, Device(1)), keys);
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

    private async Task<FolderIndex> Prepare(FolderKeys? access = null)
    {
        access ??= keys; var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, access, Device(1))) await scanner.ScanAsync(Token);
        var needed = new NeededSet(index, access, Device(1));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("file.txt", 2, 1, 10, First, 1)], Token);
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
    private SignedRecord Record(string path, ulong author, ulong counter, long time, string content, ulong sequence)
    {
        long ticks = DateTime.UtcNow.AddHours(-1).Ticks + time * TimeSpan.TicksPerSecond;
        var body = ConflictResolverTests.Body(author, counter, ticks, ticks, content, false); body.Path = path;
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
        keys.Dispose(); foreach (string file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, true);
    }
    private sealed class Stop : Exception;
}
