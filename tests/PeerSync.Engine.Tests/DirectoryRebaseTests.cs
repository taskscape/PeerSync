using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedRootRebasesLocalChildEditsOnlyAfterEveryOriginalChildArrives(bool lateChild)
    {
        WriteFiles("a", "b"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        var original = await PrepareProjection(producer); await using var receiver = await OpenMetadataReceiver(keys);
        await PrepareRebaseReceiver(producer, receiver, original, lateChild ? ["a"] : ["a", "b"]);
        var clock = new CompletionClock(DateTimeOffset.UtcNow); var worker = RebaseWorker(receiver, clock);
        long originalTime = VersionView.Open(original, keys, device).MtimeTicks;
        if (lateChild)
        {
            (await worker.RunAsync(token: Token)).Deferred.ShouldBe(1);
            receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
            await AdmitRebaseChild(producer, receiver, "b", 3);
            using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true };
            await scanner.ScanAsync(Token);
        }
        clock.AdvanceTo(receiver.ReadNextDirectoryPublicationTicks(receiver.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
        (await worker.RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        var current = VersionView.Open(receiver.LocalFiles.Find("topic")!.Record!, keys, MetadataDevice);
        current.ModifiedBy.ToArray().ShouldBe(MetadataDevice); current.MtimeTicks.ShouldBe(originalTime);
        VersionVector.Compare(current.Vector, VersionView.Open(original, keys, device).Vector).ShouldBe(VvOrder.Dominates);
        receiver.HasPendingDirectoryPublications().ShouldBeFalse(); File.ReadAllText(Path.Combine(MetadataRoot, "topic", "a")).ShouldBe("local edit before reconstruction");
        receiver.ReadDirectoryGraph(CaseConflictPolicy.SourceId(VersionView.Open(original, keys, device)))!.Complete.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebaseProofSurvivesRestartOrFinalSqlFailureWithoutReplacingTheOriginalEarly(bool sqlFailure)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        var clock = new CompletionClock(DateTimeOffset.UtcNow);
        await using (var receiver = await OpenMetadataReceiver(keys))
        {
            await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
            if (sqlFailure) MetadataSql("CREATE TRIGGER fail_rebase BEFORE UPDATE ON local_files WHEN NEW.path_key='TOPIC' BEGIN SELECT RAISE(ABORT,'rebase commit'); END;");
            var first = new DirectoryPublicationWorker(MetadataRoot, receiver, keys, MetadataDevice, new FolderSettings(), clock)
            { Checkpoint = stage => { if (!sqlFailure && stage == "rebase-proven") throw new Stop(); } };
            if (sqlFailure) (await first.RunAsync(token: Token)).Deferred.ShouldBe(1);
            else await Should.ThrowAsync<Stop>(() => first.RunAsync(token: Token));
            receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
            receiver.HasPendingDirectoryPublications().ShouldBeTrue();
        }
        if (sqlFailure) MetadataSql("DROP TRIGGER fail_rebase;");
        await using var reopened = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        clock.AdvanceTo(reopened.ReadNextDirectoryPublicationTicks(reopened.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
        (await RebaseWorker(reopened, clock).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        reopened.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    [Fact]
    public async Task RebaseUsesBoundedDurablePrefixesAcrossWorkerAndDatabaseRestarts()
    {
        WriteFiles("a", "b", "c"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using (var receiver = await OpenMetadataReceiver(keys)) await PrepareRebaseReceiver(producer, receiver, original, ["a", "b", "c"]);
        var clock = new CompletionClock(DateTimeOffset.UtcNow); int passes = 0;
        for (; passes < 12; passes++)
        {
            await using var receiver = await FolderIndex.OpenAsync(MetadataDatabase, Token);
            var result = await RebaseWorker(receiver, clock).RunAsync(maximumMembers: 1, token: Token);
            if (!receiver.HasPendingDirectoryPublications()) { result.ChangedPaths.ShouldBe(["topic"]); break; }
            receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
            result.Deferred.ShouldBe(0);
        }
        passes.ShouldBeGreaterThanOrEqualTo(5); passes.ShouldBeLessThan(12);
    }

    [Fact]
    public async Task RebaseFinalEpochGuardRejectsAChangeAfterProofAndRecoversAfterRescan()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        var clock = new CompletionClock(DateTimeOffset.UtcNow);
        var first = new DirectoryPublicationWorker(MetadataRoot, receiver, keys, MetadataDevice, new FolderSettings(), clock)
        { Checkpoint = stage => { if (stage == "rebase-proven") MetadataSql("UPDATE local_files SET content_ready=0 WHERE path_key='TOPIC/A';"); } };
        (await first.RunAsync(token: Token)).Deferred.ShouldBe(1); receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        clock.AdvanceTo(receiver.ReadNextDirectoryPublicationTicks(receiver.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
        (await RebaseWorker(receiver, clock).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
    }

    [Theory]
    [InlineData("concurrent")]
    [InlineData("equal-different")]
    [InlineData("unsigned")]
    [InlineData("unready")]
    [InlineData("unsigned-deletion")]
    public async Task RebaseDoesNotAcceptUnprovenOriginalChildren(string reason)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        var child = receiver.LocalFiles.Find("topic/a")!;
        if (reason is "concurrent" or "equal-different")
        {
            var body = VersionView.Open(child.Record!, keys, MetadataDevice).ExportBody();
            if (reason == "concurrent") { body.Vv.Clear(); body.Vv.Add(new PeerSync.Protocol.Records.VvEntry { Device = 9, Counter = 1 }); body.ModifiedBy = Google.Protobuf.ByteString.CopyFrom(MetadataDevice); }
            else { body.Vv.Clear(); body.Vv.Add(new PeerSync.Protocol.Records.VvEntry { Device = 1, Counter = 1 }); body.ModifiedBy = Google.Protobuf.ByteString.CopyFrom(device); }
            var record = SignedRecordFactory.Create(body, keys);
            child = child with { Record = record, Vector = body.Vv.ToDictionary(entry => entry.Device, entry => entry.Counter) };
        }
        else if (reason == "unsigned") child = child with { Record = null };
        else if (reason == "unsigned-deletion") child = child with { Deleted = true };
        else child = child with { ContentReady = false };
        await receiver.LocalFiles.UpsertAsync([child], Token);
        var clock = new CompletionClock(DateTimeOffset.UtcNow); (await RebaseWorker(receiver, clock).RunAsync(token: Token)).Deferred.ShouldBe(1);
        receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray()); receiver.HasPendingDirectoryPublications().ShouldBeTrue();
    }

    [Fact]
    public async Task RebaseAcceptsADominatingSignedChildTombstoneWithoutResurrectingItsOldBytes()
    {
        WriteFiles("a", "b"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a", "b"]);
        File.Delete(Path.Combine(MetadataRoot, "topic", "b"));
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        receiver.LocalFiles.Find("topic/b")!.Deleted.ShouldBeTrue();
        (await RebaseWorker(receiver, TimeProvider.System).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        File.Exists(Path.Combine(MetadataRoot, "topic", "b")).ShouldBeFalse(); receiver.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebaseAcceptsDominatingChildKindChangesWithoutDemandingObsoleteDescendants(bool directoryToFile)
    {
        WriteFiles("a");
        if (directoryToFile) { Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "b"), "old nested bytes"); }
        else WriteFiles("nested");
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys);
        await PrepareRebaseReceiver(producer, receiver, original, directoryToFile ? ["nested", "nested/b", "a"] : ["nested", "a"]);
        string changed = Path.Combine(MetadataRoot, "topic", "nested");
        if (directoryToFile) { File.Delete(Path.Combine(changed, "b")); Directory.Delete(changed); File.WriteAllText(changed, "replacement file"); }
        else { File.Delete(changed); Directory.CreateDirectory(changed); File.WriteAllText(Path.Combine(changed, "new"), "replacement directory child"); }
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        var clock = new CompletionClock(DateTimeOffset.UtcNow);
        for (int pass = 0; pass < 4 && receiver.HasPendingDirectoryPublications(); pass++)
        {
            await RebaseWorker(receiver, clock).RunAsync(token: Token);
            clock.AdvanceTo(receiver.ReadNextDirectoryPublicationTicks(receiver.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
        }
        receiver.HasPendingDirectoryPublications().ShouldBeFalse();
        if (directoryToFile) File.ReadAllText(changed).ShouldBe("replacement file");
        else File.ReadAllText(Path.Combine(changed, "new")).ShouldBe("replacement directory child");
        VersionView.Open(receiver.LocalFiles.Find("topic")!.Record!, keys, MetadataDevice).ModifiedBy.ToArray().ShouldBe(MetadataDevice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebaseDefersIncompleteGraphsAndUnavailableOriginalMemberMetadata(bool missingMember)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        MetadataSql(missingMember ? "UPDATE directory_graph_members SET record_hash=randomblob(32) WHERE path_key='TOPIC/A';" : "UPDATE directory_graphs SET complete=0;");
        (await RebaseWorker(receiver, TimeProvider.System).RunAsync(token: Token)).Deferred.ShouldBe(1);
        receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray()); receiver.HasPendingDirectoryPublications().ShouldBeTrue();
    }

    private DirectoryPublicationWorker RebaseWorker(FolderIndex receiver, TimeProvider clock) => new(MetadataRoot, receiver, keys, MetadataDevice, new FolderSettings(), clock);
    private async Task PrepareRebaseReceiver(FolderIndex producer, FolderIndex receiver, SignedRecord original, string[] children)
    {
        var needed = new NeededSet(receiver, keys, MetadataDevice);
        await needed.ApplyIndexUpdateAsync(device, 0, 1, [original.WithLocalSequence(1)], Token);
        (await new DirectoryApplier(MetadataRoot, receiver, keys, MetadataDevice).ApplyAsync("topic", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        long sequence = 1; foreach (string child in children) await AdmitRebaseChild(producer, receiver, child, ++sequence);
        using var published = new PublishedBlocks(Root, producer);
        var metadata = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
            (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!));
        (await metadata.RunAsync(token: Token)).Deferred.ShouldBe(0);
        File.WriteAllText(Path.Combine(MetadataRoot, "topic", "a"), "local edit before reconstruction");
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        receiver.ReadDirectoryPublication("topic")!.ReconstructionOnly.ShouldBeTrue();
    }
    private async Task AdmitRebaseChild(FolderIndex producer, FolderIndex receiver, string name, long sequence)
    {
        var record = producer.LocalFiles.Find("topic/" + name)!.Record!;
        await new NeededSet(receiver, keys, MetadataDevice).ApplyIndexUpdateAsync(device, sequence - 1, sequence, [record.WithLocalSequence(checked((ulong)sequence))], Token);
        if (producer.LocalFiles.Find("topic/" + name)!.Type == 1)
        {
            (await new DirectoryApplier(MetadataRoot, receiver, keys, MetadataDevice).ApplyAsync("topic/" + name, Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
            return;
        }
        using var published = new PublishedBlocks(Root, producer);
        (await new FileApplier(MetadataRoot, receiver, keys, MetadataDevice).ApplyAsync("topic/" + name,
            (hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!), Token)).ShouldBe(FileApplyOutcome.Applied);
    }
}
