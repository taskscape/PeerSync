using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class HistoryPolicyTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-HistoryPolicy-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static byte[] Device => Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly MutableClock clock = new(DateTimeOffset.UtcNow.AddDays(-40));
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public HistoryPolicyTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task ExpiryRetiresHistoryAndArchiveReferencesWhileCurrentAndNeededReferencesSurviveAndAllFilesRemain()
    {
        await using var index = await PrepareAsync(); var archive = index.Archive.ReadAll().Single();
        await AddNeededAsync(index, "original bytes"); var builder = new BlockReferences(Root, index, keys, Device);
        (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue(); clock.Now = clock.Now.AddDays(31);
        var result = await Policy(index).RetireAsync(Token); result.MarkersRetired.ShouldBe(1); result.SourcesReleased.ShouldBe(1);
        Markers(index).ShouldBeEmpty(); index.ReadRetainedArchiveProvenance(RootId(index)).ShouldBeEmpty();
        (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        var old = index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash("original bytes"))); old.Kinds.ShouldBe(BlockReferenceKind.Needed);
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash("new current bytes"))).Kinds.ShouldBe(BlockReferenceKind.Current);
        File.ReadAllText(Disk(archive.ArchivePath)).ShouldBe("original bytes"); File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
        using var blocks = new PublishedBlocks(Root, index); blocks.TryRead(Hash("original bytes"), 14, Token).ShouldNotBeNull();
    }

    [Fact]
    public async Task CapacityWitnessReleasesOnlyItsCompletedSourceBeforeTheAgeWindow()
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single();
        clock.Now = clock.Now.AddDays(1); var manager = new ArchiveManager(Root, index, clock: clock) { CapacityOverride = () => 1 };
        (await manager.PruneAsync(force: true, cancellationToken: Token)).Removed.ShouldBe(1);
        var result = await Policy(index).RetireAsync(Token); result.MarkersRetired.ShouldBe(1); result.SourcesReleased.ShouldBe(1);
        index.IsArchiveSourceReleased(source.SourceId, RootId(index)).ShouldBeTrue();
        var references = await new BlockReferences(Root, index, keys, Device).RefreshAsync(Token); references.AllKnownSourcesCovered.ShouldBeTrue(); references.References.ShouldBe(1);
    }

    [Fact]
    public async Task PhysicalDisappearanceBeforePolicyExpiryRetainsHistoryAndItsSource()
    {
        await using var index = await PrepareAsync(); var row = index.Archive.ReadAll().Single(); File.Delete(Disk(row.ArchivePath));
        clock.Now = clock.Now.AddDays(1); var manager = new ArchiveManager(Root, index, clock: clock);
        (await manager.PruneAsync(force: true, cancellationToken: Token)).Missing.ShouldBe(1);
        var result = await Policy(index).RetireAsync(Token); result.MarkersRetired.ShouldBe(0); result.SourcesReleased.ShouldBe(0);
        Markers(index).Count().ShouldBe(1); index.ReadRetainedArchiveProvenance(RootId(index)).Count().ShouldBe(1);
        (await new BlockReferences(Root, index, keys, Device).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    [Theory]
    [InlineData("unobserved", false)]
    [InlineData("published", true)]
    [InlineData("deleted", true)]
    [InlineData("withheld", false)]
    public async Task PublicCopyExpiryRequiresAnAuthenticatedLocalObservation(string observation, bool retire)
    {
        var settings = new FolderSettings { KeepBothCopies = true }; await using var index = await PrepareAsync(settings);
        var state = HistoryState.DecodeMarker(Markers(index).Single(), keys);
        if (observation != "unobserved") await ScanAsync(index, settings);
        if (observation == "deleted") { File.Delete(Disk(state.Destination)); await ScanAsync(index, settings); }
        if (observation == "withheld") { var row = index.LocalFiles.Find(state.Destination)!; await index.LocalFiles.UpsertAsync([row with { ContentReady = false }], Token); }
        clock.Now = clock.Now.AddDays(31); var result = await Policy(index).RetireAsync(Token);
        result.MarkersRetired.ShouldBe(retire ? 1 : 0); result.SourcesReleased.ShouldBe(0); index.Archive.Count().ShouldBe(0);
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
        if (observation != "deleted") File.ReadAllText(Disk(state.Destination)).ShouldBe("original bytes");
        await new BlockReferences(Root, index, keys, Device).RefreshAsync(Token);
        if (observation == "published") index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash("original bytes"))).Kinds.ShouldBe(BlockReferenceKind.Current);
    }

    [Theory]
    [InlineData("marker-retired")]
    [InlineData("source-released")]
    public async Task RestartAfterEachDurablePolicyBoundaryRebuildsFreshReferences(string boundary)
    {
        await using (var index = await PrepareAsync())
        {
            await new BlockReferences(Root, index, keys, Device).RefreshAsync(Token); clock.Now = clock.Now.AddDays(31);
            var policy = new HistoryPolicy(Root, index, keys, Device, clock: clock) { Checkpoint = stage => { if (stage == boundary) throw new InvalidOperationException("process boundary"); } };
            await Should.ThrowAsync<InvalidOperationException>(() => policy.RetireAsync(Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); await Policy(reopened).RetireAsync(Token);
        Markers(reopened).ShouldBeEmpty(); reopened.ReadRetainedArchiveProvenance(RootId(reopened)).ShouldBeEmpty();
        (await new BlockReferences(Root, reopened, keys, Device).RefreshAsync(Token)).References.ShouldBe(1);
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
    }

    [Fact]
    public async Task SourceReleaseFailureAndInvalidMarkerKeepPriorSnapshotAndRetryWithPrivateDiagnostics()
    {
        await using var index = await PrepareAsync(); var builder = new BlockReferences(Root, index, keys, Device); var before = await builder.RefreshAsync(Token);
        clock.Now = clock.Now.AddDays(31); Sql("CREATE TRIGGER reject_policy BEFORE INSERT ON archive_policy_releases BEGIN SELECT RAISE(ABORT,'private SQL text'); END;");
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "history-policy", debugEnabled: true))
            await Should.ThrowAsync<SqliteException>(() => new HistoryPolicy(Root, index, keys, Device, clock: clock, journal: journal).RetireAsync(Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value); index.ReadBlockReferences().Count().ShouldBe(2);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldContain("history.policy_failed"); log.ShouldContain("1811");
        log.ShouldNotContain("private SQL text"); log.ShouldNotContain(Root); log.ShouldNotContain("original bytes"); log.ShouldNotContain(Convert.ToHexString(Hash("original bytes")));
        Sql("DROP TRIGGER reject_policy;"); (await Policy(index).RetireAsync(Token)).SourcesReleased.ShouldBe(1);
    }

    [Fact]
    public async Task BothGatesAndCancellationPreventConcurrentReferenceMutationAndPendingApplicationsDeferPolicy()
    {
        await using var index = await PrepareAsync(); clock.Now = clock.Now.AddDays(31);
        using (var held = await FolderReferenceGate.AcquireAsync(index, Token))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            Task<HistoryPolicyResult> pending = Policy(index).RetireAsync(stop.Token); pending.IsCompleted.ShouldBeFalse(); stop.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => pending);
        }
        var intent = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(intent, Token);
        (await Policy(index).RetireAsync(Token)).WaitingForRecovery.ShouldBeTrue(); Markers(index).Count().ShouldBe(1);
        await index.AbandonApplyAsync(intent, Token); (await Policy(index).RetireAsync(Token)).MarkersRetired.ShouldBe(1);
    }

    [Fact]
    public async Task ReleasedActiveAndPartialArchiveManifestsNoLongerProtectExpiredBlocksButNativeDataRemains()
    {
        await using var index = await PrepareAsync(); Directory.CreateDirectory(Disk(".psp/archive"));
        byte[] bytes = "unrelated archive"u8.ToArray(); File.WriteAllBytes(Disk(".psp/archive/other"), bytes);
        await index.Archive.UpsertAsync([new(0, "other", ".psp/archive/other", clock.Now.UtcTicks, bytes.Length, Hash("unrelated archive"))], Token);
        var source = index.ReadArchiveProvenance().Single(p => p.Archive.OriginalPath == "other"); var builder = new BlockReferences(Root, index, keys, Device);
        (await builder.RefreshAsync(Token)).References.ShouldBe(3); var content = index.FindActiveArchiveContent(source.SourceId)!;
        var pending = await index.BeginArchiveContentAsync(source, RootId(index), content.FileId, clock.Now.UtcTicks, Token);
        await index.AppendArchiveContentChunksAsync(pending.Id, index.ReadArchiveContentChunks(content.Id).ToArray(), Token);
        clock.Now = clock.Now.AddDays(31); (await Policy(index).RetireAsync(Token)).SourcesReleased.ShouldBe(2);
        (await builder.RefreshAsync(Token)).References.ShouldBe(1); index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(content.Id);
        index.ReadPendingArchiveContents().Single().Id.ShouldBe(pending.Id); File.ReadAllBytes(Disk(".psp/archive/other")).ShouldBe(bytes);
    }

    private HistoryPolicy Policy(FolderIndex index) => new(Root, index, keys, Device, clock: clock);
    private static IEnumerable<MetaRow> Markers(FolderIndex index) => index.Meta.ReadAll().Where(m => m.Key.StartsWith("_history-", StringComparison.Ordinal));
    private static byte[] RootId(FolderIndex index) => index.Meta.Find("_root-identity")!.Value!;
    private async Task<FolderIndex> PrepareAsync(FolderSettings? settings = null)
    {
        File.WriteAllText(Disk("current"), "original bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try
        {
            await ScanAsync(index, settings); File.WriteAllText(Disk("current"), "new current bytes"); await ScanAsync(index, settings); return index;
        }
        catch { await index.DisposeAsync(); throw; }
    }
    private async Task ScanAsync(FolderIndex index, FolderSettings? settings = null)
    { using var scanner = new Scanner(Root, index, keys, Device, timeProvider: clock, settings: settings); await scanner.ScanAsync(Token); }
    private async Task AddNeededAsync(FolderIndex index, string bytes)
    {
        byte[] peer = Enumerable.Repeat((byte)2, 32).ToArray(); byte[] hash = Hash(bytes);
        var body = new FileRecordBody { Path = "incoming", Size = (ulong)System.Text.Encoding.UTF8.GetByteCount(bytes), MtimeTicks = clock.Now.UtcTicks,
            ChangedTicks = clock.Now.UtcTicks, ModifiedBy = ByteString.CopyFrom(peer), FileHash = ByteString.CopyFrom(hash) };
        body.Vv.Add(new VvEntry { Device = BitConverter.ToUInt64(peer), Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(hash), Length = (uint)body.Size });
        await new NeededSet(index, keys, Device).ApplyIndexUpdateAsync(peer, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
    }
    private static byte[] Hash(string text) => Blake3.Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(text)).AsSpan().ToArray();
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-HistoryPolicy-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
