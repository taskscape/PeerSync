using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Protocol.Handshake;
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
    public async Task ReviewReceivedDirectoryMetadataEditsSurviveReconstructionWithoutRewritingTheOriginal(bool changeCase)
    {
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys);
        await new NeededSet(receiver, keys, MetadataDevice).ApplyIndexUpdateAsync(device, 0, 1, [original.WithLocalSequence(1)], Token);
        (await new DirectoryApplier(MetadataRoot, receiver, keys, MetadataDevice).ApplyAsync("topic", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        string name = changeCase ? "TOPIC" : "topic", path = Path.Combine(MetadataRoot, "topic");
        if (changeCase) { Directory.Move(path, Path.Combine(MetadataRoot, name)); path = Path.Combine(MetadataRoot, name); }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        using var scanner = new Scanner(MetadataRoot, receiver, keys, MetadataDevice) { StageDirectorySnapshots = true }; await scanner.ScanAsync(Token);
        var queued = receiver.ReadDirectoryPublication(name)!;
        queued.Template.Body.ToArray().ShouldBe(original.Body.ToArray()); (queued.Attributes & (uint)FileAttributes.Hidden).ShouldNotBe(0u);
        using var published = new PublishedBlocks(Root, producer);
        var metadata = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
            (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!));
        (await metadata.RunAsync(token: Token)).Completed.ShouldBe(1);
        var worker = new DirectoryPublicationWorker(MetadataRoot, receiver, keys, MetadataDevice, new FolderSettings(), TimeProvider.System);
        (await worker.RunAsync(token: Token)).ChangedPaths.ShouldBe([name]); receiver.HasPendingDirectoryPublications().ShouldBeFalse();
        var current = VersionView.Open(receiver.LocalFiles.Find(name)!.Record!, keys, MetadataDevice);
        current.Path.ShouldBe(name); current.Attributes.ShouldBe((uint)FileAttributes.Hidden); current.ModifiedBy.ToArray().ShouldBe(MetadataDevice);
        current.MtimeTicks.ShouldBe(VersionView.Open(original, keys, device).MtimeTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewUnavailableDirectoryCaseSourceDefersDurablyWithoutStoppingIndependentPulls(bool corruptSnapshot)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        var body = VersionView.Open(original, keys, device).ExportBody();
        if (!corruptSnapshot) { body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0; }
        var source = SignedRecordFactory.Create(body, keys); byte[] peer = [2, .. new byte[31]], content = "independent payload"u8.ToArray();
        Directory.CreateDirectory(MetadataRoot); await using var receiver = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        using var puller = new Puller(MetadataRoot, receiver, keys, MetadataDevice, (_, hash, length, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(hash.Span.SequenceEqual(Blake3.Hasher.Hash(content).AsSpan()) ? content : new byte[length]));
        await puller.RunPassAsync(Token);
        await puller.ReceiveIndexAsync(device, 0, 1, [source.WithLocalSequence(1)], Token);
        var file = new FileRecordBody { Path = "TOPIC", Type = 0, Size = (ulong)content.Length, FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(content).AsSpan()),
            ModifiedBy = ByteString.CopyFrom(peer), MtimeTicks = DateTime.UtcNow.AddMinutes(1).Ticks, ChangedTicks = DateTime.UtcNow.Ticks };
        file.Chunks.Add(new Chunk { Hash = file.FileHash, Length = (uint)content.Length }); file.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var winner = SignedRecordFactory.Create(file, keys, 1); file.Path = "good"; var good = SignedRecordFactory.Create(file, keys, 2);
        await puller.ReceiveIndexAsync(peer, 0, 2, [winner, good], Token);
        var result = await puller.RunPassAsync(Token); result.Failed.ShouldBe(0); File.ReadAllBytes(Path.Combine(MetadataRoot, "good")).ShouldBe(content);
        var pending = receiver.ReadPendingCasePreservations().Single(); pending.DestinationPath.ShouldBeNull();
        receiver.ReadCasePreservationRetry(pending.Source.SourceId)!.Value.Attempts.ShouldBe(1);
        File.Exists(Path.Combine(MetadataRoot, "TOPIC")).ShouldBeFalse(); receiver.Needed.Find("TOPIC").ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewSnapshotIndexCanArriveBeforeTheFirstCoordinatorPass(bool sessionReceive)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        Directory.CreateDirectory(MetadataRoot); await using var receiver = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        using var puller = new Puller(MetadataRoot, receiver, keys, MetadataDevice, (_, _, _, _) => throw new IOException("No fetch during receipt."));
        receiver.Meta.Find("_root-identity").ShouldBeNull();
        if (sessionReceive)
        {
            var session = puller.OpenPeerIndexSession(device); session.ObserveHello(new Hello { ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(device),
                Nonce = ByteString.CopyFrom(new byte[32]), UtcTicks = DateTime.UtcNow.Ticks });
            (await session.ReceiveAsync(new(0, 1, [original.WithLocalSequence(1)]), Token)).Applied.ShouldBeTrue();
        }
        else (await puller.ReceiveIndexAsync(device, 0, 1, [original.WithLocalSequence(1)], Token)).Applied.ShouldBeTrue();
        receiver.Meta.Find("_root-identity")!.Value!.Length.ShouldBe(24); receiver.RemotesForPath("topic").Single().Record.Body.ToArray().ShouldBe(original.Body.ToArray());
        receiver.ReadDirectoryMetadataSource(CaseConflictPolicy.SourceId(VersionView.Open(original, keys, device))).ShouldNotBeNull();
    }

    [Fact]
    public async Task ReviewConnectedSnapshotReceiptInitializesAFreshReceiverBeforeItsFirstPass()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); await PrepareProjection(producer);
        Directory.CreateDirectory(MetadataRoot); await using var receiver = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        await using var sender = new TransportFolder(Root, producer, keys, device); await using var fresh = new TransportFolder(MetadataRoot, receiver, keys, MetadataDevice);
        var network = new InMemoryNetwork(); network.Register(device, sender.CreateHandler); var endpoint = network.Register(MetadataDevice, fresh.CreateHandler);
        await using var connection = await fresh.ConnectAsync(endpoint.Connect(device), Token); await connection.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
        receiver.Meta.Find("_root-identity").ShouldNotBeNull();
        for (int pass = 0; pass < 4; pass++) await fresh.RunPassAsync(Token);
        File.ReadAllBytes(Path.Combine(MetadataRoot, "topic", "a")).ShouldBe(File.ReadAllBytes(Path.Combine(Root, "topic", "a")));
        receiver.Needed.Count().ShouldBe(0); receiver.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    [Fact]
    public async Task ReviewReceiptRejectsAReplacedPhysicalRootBeforeCommittingThePage()
    {
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        await using var receiver = await OpenMetadataReceiver(keys); byte[] old = receiver.Meta.Find("_root-identity")!.Value!.ToArray();
        Directory.Move(MetadataRoot, Path.Combine(fixture, "old-receiver")); Directory.CreateDirectory(MetadataRoot);
        using var puller = new Puller(MetadataRoot, receiver, keys, MetadataDevice, (_, _, _, _) => throw new IOException("No fetch."));
        await Should.ThrowAsync<IOException>(() => puller.ReceiveIndexAsync(device, 0, 1, [original.WithLocalSequence(1)], Token));
        receiver.RemotesForPath("topic").ShouldBeEmpty(); receiver.Meta.Find("_root-identity")!.Value.ShouldBe(old);
    }
}
