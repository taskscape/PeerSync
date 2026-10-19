using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net;
using PeerSync.Net.InMemory;
using PeerSync.Protocol.Handshake;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class TransportFolderTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-TransportFolder-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealSignedIndexAndVerifiedChunksTransferThroughInterfaceAndForwardToThirdNode()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        byte[] data = new byte[2 * 1024 * 1024 + 37]; new Random(813).NextBytes(data);
        Directory.CreateDirectory(Path.Combine(b.Root, "nested")); File.WriteAllBytes(Path.Combine(b.Root, "nested", "file.bin"), data);
        File.WriteAllText(Path.Combine(b.Root, "empty"), ""); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler); var ec = network.Register(c.Id, c.Engine.CreateHandler);
        await using var ab = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        (await ab.SynchronizeAsync(cancellationToken: Token)).Complete.ShouldBeTrue();
        var applied = await a.Engine.RunPassAsync(Token); applied.Failed.ShouldBe(0); applied.Applied.ShouldBe(3);
        File.ReadAllBytes(Path.Combine(a.Root, "nested", "file.bin")).ShouldBe(data); File.ReadAllBytes(Path.Combine(a.Root, "empty")).ShouldBeEmpty();
        var original = b.Index.LocalFiles.Find("nested/file.bin")!.Record!;
        a.Index.LocalFiles.Find("nested/file.bin")!.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
        await using var ca = await c.Engine.ConnectAsync(ec.Connect(a.Id), Token); await ca.SynchronizeAsync(cancellationToken: Token); await c.Engine.RunPassAsync(Token);
        File.ReadAllBytes(Path.Combine(c.Root, "nested", "file.bin")).ShouldBe(data);
    }

    [Fact]
    public async Task PartitionDoesNotAdvanceCursorAndHealingTransfersNewVersionAndKeepsOldVersion()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        File.WriteAllText(Path.Combine(b.Root, "file"), "first version"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        await peer.SynchronizeAsync(cancellationToken: Token); await a.Engine.RunPassAsync(Token); long before = a.Index.ReadRemoteIndexRange(b.Id)!.ReceivedSequence;
        File.WriteAllText(Path.Combine(b.Root, "file"), "second version is longer"); await Scan(b);
        network.ConfigureLink(b.Id, a.Id, new(Partitioned: true));
        await Should.ThrowAsync<IOException>(() => peer.SynchronizeAsync(cancellationToken: Token));
        a.Index.ReadRemoteIndexRange(b.Id)!.ReceivedSequence.ShouldBe(before); File.ReadAllText(Path.Combine(a.Root, "file")).ShouldBe("first version");
        network.ConfigureLink(b.Id, a.Id, new()); await peer.SynchronizeAsync(cancellationToken: Token); await a.Engine.RunPassAsync(Token);
        File.ReadAllText(Path.Combine(a.Root, "file")).ShouldBe("second version is longer");
        a.Index.Archive.ReadAll().Any(row => File.ReadAllText(Path.Combine(a.Root, row.ArchivePath.Replace('/', Path.DirectorySeparatorChar))) == "first version").ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectUsesDurableCursorAndFreshClockSession()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        File.WriteAllText(Path.Combine(b.Root, "first"), "one"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var first = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); await first.SynchronizeAsync(cancellationToken: Token); await a.Engine.RunPassAsync(Token);
        long cursor = a.Index.ReadRemoteIndexRange(b.Id)!.ReceivedSequence; await first.DisposeAsync(); first.ClockStatus.State.ShouldBe(PeerClockState.AwaitingHello);
        File.WriteAllText(Path.Combine(b.Root, "second"), "two"); await Scan(b);
        await using var second = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); second.ClockStatus.IndexExchangeAllowed.ShouldBeTrue();
        var result = await second.SynchronizeAsync(cancellationToken: Token); result.Complete.ShouldBeTrue(); result.LastSequence.ShouldBeGreaterThan(cursor);
        await a.Engine.RunPassAsync(Token); File.ReadAllText(Path.Combine(a.Root, "second")).ShouldBe("two");
        await Should.ThrowAsync<ObjectDisposedException>(() => first.SynchronizeAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task SkewedHelloPreventsIndexAdmissionAndFreshCorrectedConnectionSucceeds()
    {
        var timeA = new NodeClock(); var timeB = new NodeClock(); timeB.Jump(TimeSpan.FromMinutes(11));
        await using var a = await NodeAsync(1, timeA); await using var b = await NodeAsync(2, timeB);
        File.WriteAllText(Path.Combine(b.Root, "file"), "clock guarded bytes"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await Should.ThrowAsync<PeerClockException>(() => a.Engine.ConnectAsync(ea.Connect(b.Id), Token));
        a.Index.RemoteFiles.Count().ShouldBe(0); a.Index.Devices.Count().ShouldBe(0);
        timeB.Jump(TimeSpan.FromMinutes(-11)); await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        await peer.SynchronizeAsync(cancellationToken: Token); await a.Engine.RunPassAsync(Token); File.ReadAllText(Path.Combine(a.Root, "file")).ShouldBe("clock guarded bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockJumpBeforeIndexOrBlockHandoffCannotApplyRemoteBytes(bool jumpServer)
    {
        var timeA = new NodeClock(); var timeB = new NodeClock();
        await using var a = await NodeAsync(1, timeA); await using var b = await NodeAsync(2, timeB);
        File.WriteAllText(Path.Combine(b.Root, "file"), "guarded source"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); await peer.SynchronizeAsync(cancellationToken: Token);
        (jumpServer ? timeB : timeA).Jump(TimeSpan.FromMinutes(11));
        var result = await a.Engine.RunPassAsync(Token); result.Failed.ShouldBe(1); File.Exists(Path.Combine(a.Root, "file")).ShouldBeFalse();
        await Should.ThrowAsync<PeerClockException>(() => peer.SynchronizeAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task LostBlockResponseRetainsNeededWorkAndDurableRetrySucceedsAfterHealing()
    {
        var time = new NodeClock(); await using var a = await NodeAsync(1, time); await using var b = await NodeAsync(2, new NodeClock());
        File.WriteAllText(Path.Combine(b.Root, "file"), "retry through transport"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); await peer.SynchronizeAsync(cancellationToken: Token);
        network.ConfigureLink(b.Id, a.Id, new(DropEvery: 1)); (await a.Engine.RunPassAsync(Token)).Failed.ShouldBe(1);
        File.Exists(Path.Combine(a.Root, "file")).ShouldBeFalse(); a.Index.Needed.Count().ShouldBe(1);
        network.ConfigureLink(b.Id, a.Id, new()); time.Advance(TimeSpan.FromSeconds(11));
        (await a.Engine.RunPassAsync(Token)).Failed.ShouldBe(0); File.ReadAllText(Path.Combine(a.Root, "file")).ShouldBe("retry through transport");
    }

    [Fact]
    public async Task ForgedHelloIdentityIsRejectedAndFailedConnectionDoesNotReservePeer()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler);
        bool forge = true; network.Register(b.Id, identity => new HelloDecorator(b.Engine.CreateHandler(identity), () => forge));
        await Should.ThrowAsync<InvalidDataException>(() => a.Engine.ConnectAsync(ea.Connect(b.Id), Token));
        forge = false; await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); peer.RemoteDeviceId.ShouldBe(b.Id);
    }

    [Fact]
    public async Task BoundedIndexPassResumesFromDurableCursorAndCompletesOnlyAfterFinalPage()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        for (int i = 0; i < 1001; i++) File.WriteAllBytes(Path.Combine(b.Root, "item-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), []);
        await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        var first = await peer.SynchronizeAsync(1, Token); first.Pages.ShouldBe(1); first.Complete.ShouldBeFalse();
        a.Index.RemoteFiles.Count().ShouldBe(1000); a.Index.ReadRemoteIndexRange(b.Id)!.Complete.ShouldBeFalse();
        var second = await peer.SynchronizeAsync(1, Token); second.Pages.ShouldBe(1); second.Complete.ShouldBeTrue();
        a.Index.RemoteFiles.Count().ShouldBe(1001); a.Index.ReadRemoteIndexRange(b.Id)!.Complete.ShouldBeTrue();
        (await peer.SynchronizeAsync(1, Token)).Pages.ShouldBe(0);
    }

    [Fact]
    public async Task DisconnectCancelsQueuedIndexAdmissionBeforeReturningAndReconnectIsUsable()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        File.WriteAllText(Path.Combine(b.Root, "file"), "cancel queued admission"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        using (await FolderReferenceGate.AcquireAsync(a.Index, Token))
        {
            var pending = peer.SynchronizeAsync(cancellationToken: Token); pending.IsCompleted.ShouldBeFalse();
            await Task.WhenAll(peer.DisposeAsync().AsTask(), peer.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Should.ThrowAsync<OperationCanceledException>(() => pending); a.Index.Devices.Count().ShouldBe(0);
        }
        await using var fresh = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        (await fresh.SynchronizeAsync(cancellationToken: Token)).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task DuplicateConnectionFailureDoesNotRemoveExistingUsablePeer()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        File.WriteAllText(Path.Combine(b.Root, "file"), "existing peer stays connected"); await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var first = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        await Should.ThrowAsync<InvalidOperationException>(() => a.Engine.ConnectAsync(ea.Connect(b.Id), Token));
        await first.SynchronizeAsync(cancellationToken: Token); (await a.Engine.RunPassAsync(Token)).Failed.ShouldBe(0);
        File.ReadAllText(Path.Combine(a.Root, "file")).ShouldBe("existing peer stays connected");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshNodeConvergesNestedTombstoneWhenAncestorIsMissingOrAFile(bool fileAncestor)
    {
        var time = new NodeClock(); await using var a = await NodeAsync(1, time); await using var b = await NodeAsync(2, new NodeClock());
        Directory.CreateDirectory(Path.Combine(b.Root, "parent")); File.WriteAllText(Path.Combine(b.Root, "parent", "child"), "old child bytes"); await Scan(b);
        Directory.Delete(Path.Combine(b.Root, "parent"), true);
        if (fileAncestor) File.WriteAllText(Path.Combine(b.Root, "parent"), "new ancestor file");
        await Scan(b);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); await peer.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 3; pass++) { time.Advance(TimeSpan.FromSeconds(601)); await a.Engine.RunPassAsync(Token); }
        a.Index.Needed.Count().ShouldBe(0); a.Index.LocalFiles.Find("parent/child")!.Deleted.ShouldBeTrue();
        File.Exists(Path.Combine(a.Root, "parent")).ShouldBe(fileAncestor); Directory.Exists(Path.Combine(a.Root, "parent")).ShouldBeFalse();
        if (fileAncestor) File.ReadAllText(Path.Combine(a.Root, "parent")).ShouldBe("new ancestor file");
        b.Index.Archive.ReadAll().Any(row => File.ReadAllText(Path.Combine(b.Root, row.ArchivePath.Replace('/', Path.DirectorySeparatorChar))) == "old child bytes").ShouldBeTrue();
    }

    private async Task<Node> NodeAsync(ulong identity, TimeProvider? time = null)
    {
        string directory = Path.Combine(fixture, identity.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
        var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token); byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, identity);
        var engine = new TransportFolder(root, index, keys, id, timeProvider: time); await engine.RunPassAsync(Token); return new(root, index, engine, id);
    }
    private static async Task Scan(Node node) { node.Engine.RequestScan(); await node.Engine.RunPassAsync(Token); }
    public void Dispose() { keys.Dispose(); if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    private sealed record Node(string Root, FolderIndex Index, TransportFolder Engine, byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    private sealed class NodeClock : TimeProvider
    {
        private long utc = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
        public void Jump(TimeSpan amount) => utc += amount.Ticks;
        public void Advance(TimeSpan amount) { utc += amount.Ticks; elapsed += amount.Ticks; }
    }
    private sealed class HelloDecorator(IPeerRequestHandler inner, Func<bool> forge) : IPeerRequestHandler
    {
        public async ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken)
        { var response = await inner.ExchangeHelloAsync(hello, cancellationToken); if (forge()) response.DeviceId = ByteString.CopyFrom(new byte[32]); return response; }
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken) => inner.ReadIndexTargetAsync(cancellationToken);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken) => inner.ReadIndexAsync(afterSequence, limit, cancellationToken);
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken) => inner.ReadBlockAsync(hash, length, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
