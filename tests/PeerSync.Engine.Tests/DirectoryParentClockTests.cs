using System.Buffers.Binary;
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

    private async Task RequireParentClockClosure(FolderIndex index, string root, byte[] own, CompletionClock clock)
    {
        var queued = index.ReadDirectoryPublication("topic")!; var prior = index.LocalFiles.Find("topic")!;
        var original = VersionView.Open(prior.Record!, keys, own); var merged = original.ExportBody();
        ulong ownShort = BinaryPrimitives.ReadUInt64LittleEndian(own);
        var counter = merged.Vv.SingleOrDefault(entry => entry.Device == ownShort);
        if (counter is null) merged.Vv.Add(new VvEntry { Device = ownShort, Counter = 100 }); else counter.Counter = 100;
        merged.Vv.Add(new VvEntry { Device = 77, Counter = 5 });
        byte[] peer = [42, .. new byte[31]]; var receipt = SignedRecordFactory.Create(merged, keys, 1);
        var needed = new NeededSet(index, keys, own); await needed.ApplyIndexUpdateAsync(peer, 0, 1, [receipt], Token);
        index.ReadDirectoryPublication("topic")!.Version.ShouldBe(queued.Version);
        (await new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        var current = index.LocalFiles.Find("topic")!; var published = VersionView.Open(current.Record!, keys, own);
        published.Vector[ownShort].ShouldBe(101ul); published.Vector[77].ShouldBe(5ul);
        VersionVector.Compare(published.Vector, VersionView.Open(receipt, keys, peer).Vector).ShouldBe(VvOrder.Dominates);
        needed.Decide(new("TOPIC", current, index.RemotesForPath("topic").ToArray())).RequiresApply.ShouldBeFalse();
        File.ReadAllText(Path.Combine(root, "topic", "a")).ShouldBe("local edit before reconstruction");
        index.HasPendingDirectoryPublications().ShouldBeFalse();
    }
}
