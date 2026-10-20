using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryPublicationAbsorbsNewerSameOriginalParentClocksBeforeIncrementing(bool reconstruct)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        var clock = new CompletionClock(DateTimeOffset.UtcNow);
        if (reconstruct)
        {
            var original = await PrepareProjection(producer);
            await using var receiver = await OpenMetadataReceiver(keys);
            await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
            await RequireParentClockClosure(receiver, MetadataRoot, MetadataDevice, clock);
        }
        else
        {
            using var scanner = new Scanner(Root, producer, keys, device) { StageDirectorySnapshots = true };
            await scanner.ScanAsync(Token); await new DirectoryPublicationWorker(Root, producer, keys, device, new FolderSettings(), clock).RunAsync(token: Token);
            File.WriteAllText(Path.Combine(Root, "topic", "a"), "local edit before reconstruction");
            await scanner.ScanAsync(Token); await RequireParentClockClosure(producer, Root, device, clock);
        }
    }

    [Fact]
    public async Task DirectoryParentClockRejectsALateSameBodyReceiptAtFinalCommitAndRetries()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        await RequireParentClockClosure(receiver, MetadataRoot, MetadataDevice, new CompletionClock(DateTimeOffset.UtcNow), late: true);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("attributes")]
    [InlineData("mtime")]
    public async Task DirectoryParentClockDoesNotAbsorbAChangedOriginalTreeOrMetadata(string difference)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); await PrepareRebaseReceiver(producer, receiver, original, ["a"]);
        await RequireParentClockClosure(receiver, MetadataRoot, MetadataDevice, new CompletionClock(DateTimeOffset.UtcNow), difference: difference);
    }

    private async Task RequireParentClockClosure(FolderIndex index, string root, byte[] own, CompletionClock clock, bool late = false, string? difference = null)
    {
        var queued = index.ReadDirectoryPublication("topic")!; var prior = index.LocalFiles.Find("topic")!;
        var original = VersionView.Open(prior.Record!, keys, own); var merged = original.ExportBody();
        ulong ownShort = BinaryPrimitives.ReadUInt64LittleEndian(own);
        var counter = merged.Vv.SingleOrDefault(entry => entry.Device == ownShort);
        if (counter is null) merged.Vv.Add(new VvEntry { Device = ownShort, Counter = 100 }); else counter.Counter = 100;
        merged.Vv.Add(new VvEntry { Device = 77, Counter = 5 });
        if (difference == "snapshot") merged.DirectorySnapshotHash = ByteString.CopyFrom(new byte[32]);
        else if (difference == "attributes") merged.Attributes ^= (uint)FileAttributes.Hidden;
        else if (difference == "mtime") merged.MtimeTicks++;
        byte[] peer = [42, .. new byte[31]]; var receipt = SignedRecordFactory.Create(merged, keys, 1);
        var needed = new NeededSet(index, keys, own);
        if (!late) await needed.ApplyIndexUpdateAsync(peer, 0, 1, [receipt], Token);
        index.ReadDirectoryPublication("topic")!.Version.ShouldBe(queued.Version);
        if (late)
        {
            bool admitted = false;
            var raced = new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock) { Checkpoint = stage =>
            {
                if (stage != "before-queue-publication" || admitted) return;
                admitted = true; needed.ApplyIndexUpdateAsync(peer, 0, 1, [receipt], Token).GetAwaiter().GetResult();
            } };
            (await raced.RunAsync(token: Token)).Deferred.ShouldBe(1); admitted.ShouldBeTrue();
            index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(prior.Record!.Body.ToArray());
            index.ReadApplyIntents().ShouldBeEmpty();
            clock.AdvanceTo(index.ReadNextDirectoryPublicationTicks(index.Meta.Find("_root-identity")!.Value!, clock.GetUtcNow().UtcTicks)!.Value);
        }
        var result = await new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock).RunAsync(token: Token);
        if (difference == "snapshot")
        {
            result.Deferred.ShouldBe(1); result.ChangedPaths.ShouldBeEmpty();
            index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(prior.Record!.Body.ToArray()); return;
        }
        result.ChangedPaths.ShouldBe(["topic"]);
        var current = index.LocalFiles.Find("topic")!; var published = VersionView.Open(current.Record!, keys, own);
        if (difference is not null)
        {
            published.Vector[ownShort].ShouldBeLessThan(100ul); published.Vector.ShouldNotContainKey(77ul);
            return; // Different trees/metadata retain their independent conflict decision.
        }
        published.Vector[ownShort].ShouldBe(101ul); published.Vector[77].ShouldBe(5ul);
        VersionVector.Compare(published.Vector, VersionView.Open(receipt, keys, peer).Vector).ShouldBe(VvOrder.Dominates);
        needed.Decide(new("TOPIC", current, index.RemotesForPath("topic").ToArray())).RequiresApply.ShouldBeFalse();
        File.ReadAllText(Path.Combine(root, "topic", "a")).ShouldBe("local edit before reconstruction");
        index.HasPendingDirectoryPublications().ShouldBeFalse();
    }
}
