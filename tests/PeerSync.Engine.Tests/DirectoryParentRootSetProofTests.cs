using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData(false, 1000, false)]
    [InlineData(true, 1000, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1000, true)]
    [InlineData(true, 1, true)]
    public Task DirectoryParentRootSetMustProveBothOlderTreesBeforeClosingTheirClocks(bool reconstruct, int maximumWork, bool late)
        => ExerciseParentRoots(reconstruct, maximumWork, late, null);

    [Theory]
    [InlineData("incomplete")]
    [InlineData("retiring")]
    [InlineData("member")]
    public Task UnprovenParentGraphsCannotAuthorizeClockClosure(string damage) => ExerciseParentRoots(true, 1000, false, damage);

    [Theory]
    [InlineData("changed-ticks")]
    public Task DirectoryParentRootsProveCausalProvenanceVariantsBeforeClosingTheirClocks(string provenance)
        => ExerciseParentRoots(true, 1000, false, provenance);

    [Fact]
    public Task DirectoryParentRootsKeepDifferentAuthorsOutsideClockClosure()
        => ExerciseParentRoots(true, 1000, false, "author");

    [Theory]
    [InlineData("changed-ticks:incomplete")]
    [InlineData("changed-ticks:retiring")]
    [InlineData("changed-ticks:member")]
    public Task UnprovenProvenanceParentGraphsCannotAuthorizeClockClosure(string damage)
        => ExerciseParentRoots(true, 1000, false, damage);

    private async Task ExerciseParentRoots(bool reconstruct, int maximumWork, bool late, string? damage)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        using var initial = new Scanner(Root, producer, keys, device); await initial.ScanAsync(Token);
        var olderChild = producer.LocalFiles.Find("topic/a")!.Record!;
        File.WriteAllText(Path.Combine(Root, "topic", "a"), "producer second version"); await initial.ScanAsync(Token);
        var original = await PrepareProjection(producer);
        using var blocks = new DirectorySnapshotBlocks(Root, producer, keys, device);
        var olderTree = await blocks.CaptureDirectoryAsync("topic", [olderChild], Token); await blocks.FlushAsync(Token);
        var clock = new CompletionClock(DateTimeOffset.UtcNow);
        if (reconstruct)
        {
            await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
            await RequireTwoParentRoots(producer, receiver, MetadataRoot, MetadataDevice, olderTree, clock, maximumWork, late, damage);
        }
        else
        {
            File.WriteAllText(Path.Combine(Root, "topic", "a"), "local edit before reconstruction");
            using var scanner = new Scanner(Root, producer, keys, device) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
            await RequireTwoParentRoots(producer, producer, Root, device, olderTree, clock, maximumWork, late, damage);
        }
    }

    private async Task RequireTwoParentRoots(FolderIndex producer, FolderIndex index, string root, byte[] own,
        PeerSync.Protocol.Directories.DirectorySnapshotLink olderTree, CompletionClock clock, int maximumWork, bool late, string? damage)
    {
        var original = VersionView.Open(index.LocalFiles.Find("topic")!.Record!, keys, own); var alternate = original.ExportBody();
        alternate.DirectorySnapshotHash.Span.SequenceEqual(olderTree.Hash).ShouldBeFalse();
        alternate.DirectorySnapshotHash = ByteString.CopyFrom(olderTree.Hash); alternate.DirectorySnapshotLength = (uint)olderTree.EncodedLength;
        ulong shortOwn = BinaryPrimitives.ReadUInt64LittleEndian(own); var counter = alternate.Vv.SingleOrDefault(entry => entry.Device == shortOwn);
        if (counter is null) alternate.Vv.Add(new VvEntry { Device = shortOwn, Counter = 100 }); else counter.Counter = 100;
        byte[] peer = [42, .. new byte[31]];
        int separator = damage?.IndexOf(':') ?? -1;
        string? provenance = separator >= 0 ? damage![..separator] : damage;
        if (provenance == "changed-ticks") alternate.ChangedTicks++;
        else if (provenance == "author")
        { alternate.ModifiedBy = ByteString.CopyFrom(peer); alternate.Vv.Add(new VvEntry { Device = 42, Counter = 7 }); }
        if (provenance is "changed-ticks" or "author") damage = separator >= 0 ? damage![(separator + 1)..] : null;
        var record = SignedRecordFactory.Create(alternate, keys, 1);
        var needed = new NeededSet(index, keys, own);
        if (late)
        {
            bool admitted = false;
            var racing = new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock) { Checkpoint = stage =>
            {
                if (stage != "before-queue-publication" || admitted) return;
                admitted = true; needed.ApplyIndexUpdateAsync(peer, 0, 1, [record], Token).GetAwaiter().GetResult();
            } };
            (await racing.RunAsync(token: Token)).Deferred.ShouldBe(1); admitted.ShouldBeTrue();
            index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Record.Body.ToArray());
            clock.AdvanceTo(index.ReadNextDirectoryPublicationTicks(index.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks)!.Value);
        }
        else await needed.ApplyIndexUpdateAsync(peer, 0, 1, [record], Token);
        using var published = new PublishedBlocks(Root, producer);
        var metadata = new DirectoryMetadataWorker(root, index, keys, own, clock, (_, hash, length, token)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token) ?? throw new IOException("Generated parent graph missing.")));
        for (int pass = 0; pass < 4; pass++) await metadata.RunAsync(token: Token);
        byte[] graphId = CaseConflictPolicy.SourceId(VersionView.Open(record, keys, peer));
        index.ReadDirectoryGraph(graphId)!.Complete.ShouldBeTrue();
        if (damage is not null)
        {
            string id = "X'" + Convert.ToHexString(graphId) + "'";
            MetadataSql(damage switch
            {
                "incomplete" => "UPDATE directory_graphs SET complete=0 WHERE id=" + id + ";",
                "retiring" => "UPDATE directory_graphs SET retiring=1 WHERE id=" + id + ";",
                _ => "UPDATE directory_graph_members SET record_hash=randomblob(32) WHERE graph_id=" + id + ";"
            });
            var rejected = await new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock).RunAsync(token: Token);
            rejected.Deferred.ShouldBe(1); rejected.ChangedPaths.ShouldBeEmpty();
            index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Record.Body.ToArray()); return;
        }
        int passes = 0;
        for (; passes < 64 && index.HasPendingDirectoryPublications(); passes++)
        {
            await new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock).RunAsync(maximumMembers: maximumWork, token: Token);
            clock.AdvanceTo(index.ReadNextDirectoryPublicationTicks(index.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
        }
        passes.ShouldBeLessThan(64); index.HasPendingDirectoryPublications().ShouldBeFalse();
        var current = index.LocalFiles.Find("topic")!; var view = VersionView.Open(current.Record!, keys, own);
        if (provenance == "author")
        {
            view.Vector[shortOwn].ShouldBeLessThan(100ul);
            view.ExportBody().ModifiedBy.Span.SequenceEqual(own).ShouldBeTrue();
            view.MtimeTicks.ShouldBe(original.MtimeTicks); return;
        }
        view.Vector[shortOwn].ShouldBe(101ul);
        view.MtimeTicks.ShouldBe(original.MtimeTicks); view.ExportBody().Attributes.ShouldBe(original.ExportBody().Attributes);
        needed.Decide(new("TOPIC", current, index.RemotesForPath("topic").ToArray())).RequiresApply.ShouldBeFalse();
        File.ReadAllText(Path.Combine(root, "topic", "a")).ShouldBe("local edit before reconstruction");
    }

    [Fact]
    public async Task UnprovenConcurrentRootWaitsForOrdinaryChildReceiptBeforeClosingParentHistory()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        using var initial = new Scanner(Root, producer, keys, device); await initial.ScanAsync(Token);
        File.WriteAllText(Path.Combine(Root, "topic", "a"), "producer second version"); await initial.ScanAsync(Token);
        var original = await PrepareProjection(producer);
        var future = VersionView.Open(producer.LocalFiles.Find("topic/a")!.Record!, keys, device).ExportBody();
        future.Vv.Single(entry => entry.Device == 1).Counter++; future.ChangedTicks++;
        var unseenChild = SignedRecordFactory.Create(future, keys, 2);
        using var blocks = new DirectorySnapshotBlocks(Root, producer, keys, device);
        var unseenRoot = await blocks.CaptureDirectoryAsync("topic", [unseenChild], Token); await blocks.FlushAsync(Token);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        var parent = VersionView.Open(original, keys, device).ExportBody();
        parent.DirectorySnapshotHash = ByteString.CopyFrom(unseenRoot.Hash); parent.DirectorySnapshotLength = (uint)unseenRoot.EncodedLength;
        ulong own = BinaryPrimitives.ReadUInt64LittleEndian(MetadataDevice); parent.Vv.Add(new VvEntry { Device = own, Counter = 100 });
        byte[] peer = [42, .. new byte[31]]; var needed = new NeededSet(receiver, keys, MetadataDevice);
        await needed.ApplyIndexUpdateAsync(peer, 0, 1, [SignedRecordFactory.Create(parent, keys, 1)], Token);
        using var published = new PublishedBlocks(Root, producer); var clock = new CompletionClock(DateTimeOffset.UtcNow);
        var metadata = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, clock, (_, hash, length, token)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!));
        for (int pass = 0; pass < 4; pass++) await metadata.RunAsync(token: Token);
        var blocked = await RebaseWorker(receiver, clock).RunAsync(token: Token);
        blocked.Deferred.ShouldBe(1); blocked.ChangedPaths.ShouldBeEmpty(); receiver.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
        await needed.ApplyIndexUpdateAsync(peer, 1, 2, [unseenChild], Token);
        var applied = await new FileApplier(MetadataRoot, receiver, keys, MetadataDevice).ApplyAsync("topic/a",
            (hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!), Token);
        applied.ShouldBe(FileApplyOutcome.Applied);
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        for (int pass = 0; pass < 16 && receiver.HasPendingDirectoryPublications(); pass++)
        {
            clock.AdvanceTo(receiver.ReadNextDirectoryPublicationTicks(receiver.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks) ?? clock.GetUtcNow().UtcTicks);
            await RebaseWorker(receiver, clock).RunAsync(token: Token);
        }
        receiver.HasPendingDirectoryPublications().ShouldBeFalse();
        VersionView.Open(receiver.LocalFiles.Find("topic")!.Record!, keys, MetadataDevice).Vector[own].ShouldBe(101ul);
        File.ReadAllText(Path.Combine(MetadataRoot, "topic", "a")).ShouldBe("local edit before reconstruction");
    }
}
