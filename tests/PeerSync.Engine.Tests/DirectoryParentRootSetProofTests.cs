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
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryParentRootSetMustProveBothOlderTreesBeforeClosingTheirClocks(bool reconstruct)
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
            await RequireTwoParentRoots(producer, receiver, MetadataRoot, MetadataDevice, olderTree, clock);
        }
        else
        {
            File.WriteAllText(Path.Combine(Root, "topic", "a"), "local edit before reconstruction");
            using var scanner = new Scanner(Root, producer, keys, device) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
            await RequireTwoParentRoots(producer, producer, Root, device, olderTree, clock);
        }
    }

    private async Task RequireTwoParentRoots(FolderIndex producer, FolderIndex index, string root, byte[] own,
        PeerSync.Protocol.Directories.DirectorySnapshotLink olderTree, CompletionClock clock)
    {
        var original = VersionView.Open(index.LocalFiles.Find("topic")!.Record!, keys, own); var alternate = original.ExportBody();
        alternate.DirectorySnapshotHash.Span.SequenceEqual(olderTree.Hash).ShouldBeFalse();
        alternate.DirectorySnapshotHash = ByteString.CopyFrom(olderTree.Hash); alternate.DirectorySnapshotLength = (uint)olderTree.EncodedLength;
        ulong shortOwn = BinaryPrimitives.ReadUInt64LittleEndian(own); var counter = alternate.Vv.SingleOrDefault(entry => entry.Device == shortOwn);
        if (counter is null) alternate.Vv.Add(new VvEntry { Device = shortOwn, Counter = 100 }); else counter.Counter = 100;
        byte[] peer = [42, .. new byte[31]]; var record = SignedRecordFactory.Create(alternate, keys, 1);
        var needed = new NeededSet(index, keys, own); await needed.ApplyIndexUpdateAsync(peer, 0, 1, [record], Token);
        using var published = new PublishedBlocks(Root, producer);
        var metadata = new DirectoryMetadataWorker(root, index, keys, own, clock, (_, hash, length, token)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token) ?? throw new IOException("Generated parent graph missing.")));
        for (int pass = 0; pass < 4; pass++) await metadata.RunAsync(token: Token);
        index.ReadDirectoryGraph(CaseConflictPolicy.SourceId(VersionView.Open(record, keys, peer)))!.Complete.ShouldBeTrue();
        (await new DirectoryPublicationWorker(root, index, keys, own, new FolderSettings(), clock).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        var current = index.LocalFiles.Find("topic")!; var view = VersionView.Open(current.Record!, keys, own);
        view.Vector[shortOwn].ShouldBe(101ul);
        needed.Decide(new("TOPIC", current, index.RemotesForPath("topic").ToArray())).RequiresApply.ShouldBeFalse();
        File.ReadAllText(Path.Combine(root, "topic", "a")).ShouldBe("local edit before reconstruction");
    }
}
