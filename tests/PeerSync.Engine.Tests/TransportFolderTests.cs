using System.Buffers.Binary;
using System.Security.Authentication;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Ignore;
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
        long aUploaded = 0, aDownloaded = 0, bUploaded = 0, cDownloaded = 0;
        a.Engine.BytesTransferred += (bytes, download) =>
        { if (download) Interlocked.Add(ref aDownloaded, bytes); else Interlocked.Add(ref aUploaded, bytes); };
        b.Engine.BytesTransferred += (bytes, download) =>
        { if (!download) Interlocked.Add(ref bUploaded, bytes); };
        c.Engine.BytesTransferred += (bytes, download) =>
        { if (download) Interlocked.Add(ref cDownloaded, bytes); };
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
        aDownloaded.ShouldBeGreaterThanOrEqualTo(data.Length);
        bUploaded.ShouldBeGreaterThanOrEqualTo(data.Length);
        aUploaded.ShouldBeGreaterThanOrEqualTo(data.Length);
        cDownloaded.ShouldBeGreaterThanOrEqualTo(data.Length);
    }

    [Fact]
    public async Task ChunkRequestsRotateAcrossAuthenticatedPeersAndVerifyFallbackContent()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        byte[] data = new byte[768 * 1024]; new Random(7041).NextBytes(data);
        File.WriteAllBytes(Path.Combine(b.Root, "shared.bin"), data); File.WriteAllBytes(Path.Combine(c.Root, "shared.bin"), data);
        await Scan(b); await Scan(c);
        int bRequests = 0, cRequests = 0;
        var network = new InMemoryNetwork(); var endpoint = network.Register(a.Id, a.Engine.CreateHandler);
        network.Register(b.Id, id =>
        {
            IPeerRequestHandler inner = b.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            { Block = async (hash, length, token) => { Interlocked.Increment(ref bRequests); return await inner.ReadBlockAsync(hash, length, token); } };
        });
        network.Register(c.Id, id =>
        {
            IPeerRequestHandler inner = c.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            {
                Block = async (hash, length, token) =>
                {
                    Interlocked.Increment(ref cRequests);
                    // The first response is deliberately corrupt. The scheduler must
                    // reject it by hash and obtain the same content from another peer.
                    if (Volatile.Read(ref cRequests) == 1) return new byte[length];
                    return await inner.ReadBlockAsync(hash, length, token);
                }
            };
        });
        await using var fromB = await a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
        await using var fromC = await a.Engine.ConnectAsync(endpoint.Connect(c.Id), Token);
        await fromB.SynchronizeAsync(cancellationToken: Token);
        PullPassResult result = await a.Engine.RunPassAsync(Token);
        result.Applied.ShouldBe(1); result.Failed.ShouldBe(0);
        File.ReadAllBytes(Path.Combine(a.Root, "shared.bin")).ShouldBe(data);
        cRequests.ShouldBeGreaterThan(0); bRequests.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FreshPeerStopsProbingAnotherUnseededPeerForEverySmallFile()
    {
        await using var receiver = await NodeAsync(71); await using var source = await NodeAsync(72);
        await using var unseeded = await NodeAsync(73);
        for (int i = 0; i < 32; i++)
            File.WriteAllBytes(Path.Combine(source.Root, $"small-{i:D2}.bin"), BitConverter.GetBytes(i));
        await Scan(source);

        int missingRequests = 0;
        var network = new InMemoryNetwork(); var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        network.Register(source.Id, source.Engine.CreateHandler);
        network.Register(unseeded.Id, id => new ControlledHandler(unseeded.Engine.CreateHandler(id))
        {
            Block = (_, _, _) =>
            {
                Interlocked.Increment(ref missingRequests);
                return ValueTask.FromException<ReadOnlyMemory<byte>>(
                    new PeerBlockException(PeerSync.Protocol.Wire.BlockError.NotFound));
            }
        });
        await using var fromSource = await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id), Token);
        await using var fromUnseeded = await receiver.Engine.ConnectAsync(endpoint.Connect(unseeded.Id), Token);
        await fromSource.SynchronizeAsync(cancellationToken: Token);
        PullPassResult result = await receiver.Engine.RunPassAsync(Token);

        result.Failed.ShouldBe(0);
        for (int i = 0; i < 32; i++)
            File.ReadAllBytes(Path.Combine(receiver.Root, $"small-{i:D2}.bin")).ShouldBe(BitConverter.GetBytes(i));
        missingRequests.ShouldBeGreaterThan(0);
        missingRequests.ShouldBeLessThan(4);
    }

    [Fact]
    public async Task TransientPeerAuthenticationFailureDefersFileWithoutStoppingTheFolder()
    {
        var time = new NodeClock();
        await using var receiver = await NodeAsync(77, time); await using var source = await NodeAsync(78, time);
        byte[] contents = "authenticated retry"u8.ToArray();
        File.WriteAllBytes(Path.Combine(source.Root, "retry.txt"), contents);
        await Scan(source);

        int requests = 0;
        var network = new InMemoryNetwork(); var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        network.Register(source.Id, id =>
        {
            IPeerRequestHandler inner = source.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            {
                Block = (hash, length, token) => Interlocked.Increment(ref requests) == 1
                    ? ValueTask.FromException<ReadOnlyMemory<byte>>(new AuthenticationException("Transient TLS session failure."))
                    : inner.ReadBlockAsync(hash, length, token)
            };
        });
        await using var fromSource = await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id), Token);
        await fromSource.SynchronizeAsync(cancellationToken: Token);

        PullPassResult first = await receiver.Engine.RunPassAsync(Token);
        first.Failed.ShouldBe(1);
        File.Exists(Path.Combine(receiver.Root, "retry.txt")).ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(11));
        PullPassResult second = await receiver.Engine.RunPassAsync(Token);
        second.Failed.ShouldBe(0); second.Applied.ShouldBe(1);
        File.ReadAllBytes(Path.Combine(receiver.Root, "retry.txt")).ShouldBe(contents);
        requests.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ReplicaIsUsedAgainWhenItBeginsServingAnAuthorsBlocks()
    {
        await using var receiver = await NodeAsync(74); await using var source = await NodeAsync(75);
        await using var replica = await NodeAsync(76);
        for (int i = 0; i < 128; i++)
        {
            byte[] data = BitConverter.GetBytes(i);
            File.WriteAllBytes(Path.Combine(source.Root, $"shared-{i:D3}.bin"), data);
            File.WriteAllBytes(Path.Combine(replica.Root, $"shared-{i:D3}.bin"), data);
        }
        await Scan(source); await Scan(replica);

        int sourceRequests = 0, replicaRequests = 0;
        var network = new InMemoryNetwork(); var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        network.Register(source.Id, id =>
        {
            IPeerRequestHandler inner = source.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            { Block = async (hash, length, token) =>
                { Interlocked.Increment(ref sourceRequests); return await inner.ReadBlockAsync(hash, length, token); } };
        });
        network.Register(replica.Id, id =>
        {
            IPeerRequestHandler inner = replica.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            { Block = async (hash, length, token) =>
                { Interlocked.Increment(ref replicaRequests); return await inner.ReadBlockAsync(hash, length, token); } };
        });
        await using var fromSource = await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id), Token);
        await using var fromReplica = await receiver.Engine.ConnectAsync(endpoint.Connect(replica.Id), Token);
        await fromSource.SynchronizeAsync(cancellationToken: Token);
        PullPassResult result = await receiver.Engine.RunPassAsync(Token);

        result.Failed.ShouldBe(0);
        for (int i = 0; i < 128; i++)
            File.ReadAllBytes(Path.Combine(receiver.Root, $"shared-{i:D3}.bin")).ShouldBe(BitConverter.GetBytes(i));
        sourceRequests.ShouldBeGreaterThan(10);
        replicaRequests.ShouldBeGreaterThan(10);
        (sourceRequests + replicaRequests).ShouldBeLessThan(160);
    }

    [Fact]
    public async Task FinalChunkEndgameUsesAnotherPeerAndCancelsTheSlowerDuplicate()
    {
        await using var receiver = await NodeAsync(11); await using var slow = await NodeAsync(12); await using var fast = await NodeAsync(13);
        byte[] data = new byte[2 * 1024 * 1024 + 37]; new Random(2104).NextBytes(data);
        File.WriteAllBytes(Path.Combine(slow.Root, "endgame.bin"), data);
        File.WriteAllBytes(Path.Combine(fast.Root, "endgame.bin"), data);
        await Scan(slow); await Scan(fast);
        var version = VersionView.Open(slow.Index.LocalFiles.Find("endgame.bin")!.Record!, keys, slow.Id);
        var chunks = version.ExportBody().Chunks;
        chunks.Count.ShouldBeGreaterThan(1);
        byte[] finalHash = chunks[^1].Hash.ToByteArray();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int fastRequests = 0;
        var network = new InMemoryNetwork();
        var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        network.Register(slow.Id, id =>
        {
            IPeerRequestHandler inner = slow.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            {
                Block = async (hash, length, token) =>
                {
                    if (hash.Span.SequenceEqual(finalHash))
                    {
                        slowStarted.TrySetResult();
                        try { await Task.Delay(TimeSpan.FromSeconds(20), token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        { slowCanceled.TrySetResult(); throw; }
                    }
                    return await inner.ReadBlockAsync(hash, length, token);
                }
            };
        });
        network.Register(fast.Id, id =>
        {
            IPeerRequestHandler inner = fast.Engine.CreateHandler(id);
            return new ControlledHandler(inner)
            {
                Block = async (hash, length, token) =>
                {
                    Interlocked.Increment(ref fastRequests);
                    await Task.Delay(TimeSpan.FromMilliseconds(25), token);
                    return await inner.ReadBlockAsync(hash, length, token);
                }
            };
        });
        await using var slowPeer = await receiver.Engine.ConnectAsync(endpoint.Connect(slow.Id), Token);
        await using var fastPeer = await receiver.Engine.ConnectAsync(endpoint.Connect(fast.Id), Token);
        await slowPeer.SynchronizeAsync(cancellationToken: Token);
        await fastPeer.SynchronizeAsync(cancellationToken: Token);

        Task<PullPassResult> pass = receiver.Engine.RunPassAsync(Token);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        PullPassResult result = await pass.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await slowCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2), Token);

        result.Applied.ShouldBe(1); result.Failed.ShouldBe(0);
        fastRequests.ShouldBeGreaterThan(0);
        File.ReadAllBytes(Path.Combine(receiver.Root, "endgame.bin")).ShouldBe(data);
    }

    [Fact]
    public async Task BlockServerRejectsPathsIgnoredByItsCurrentFolderPolicy()
    {
        var includeAll = new IgnoreMatcher(includeDefaults: false);
        await using var source = await NodeAsync(21, ignore: includeAll);
        await using var requester = await NodeAsync(22);
        byte[] data = "content from a now-ignored path"u8.ToArray();
        File.WriteAllBytes(Path.Combine(source.Root, "private.tmp"), data);
        await Scan(source);
        byte[] hash = Blake3.Hasher.Hash(data).AsSpan().ToArray();
        source.Index.FindChunks(hash).Any().ShouldBeTrue();

        await source.Engine.DisposeAsync();
        var ignorePath = new IgnoreMatcher("private.tmp", includeDefaults: false);
        await using var policyEngine = new TransportFolder(source.Root, source.Index, keys, source.Id,
            settings: null, timeProvider: null, journal: null, ignore: ignorePath);
        await using IPeerRequestHandler handler = policyEngine.CreateHandler(requester.Id);
        await handler.ExchangeHelloAsync(requester.Engine.CreateHello(), Token);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => handler.ReadBlockAsync(hash, data.Length, Token).AsTask());
        await Should.ThrowAsync<FileNotFoundException>(() => handler.ReadBlockAsync(new byte[32], data.Length, Token).AsTask());
    }

    [Fact]
    public async Task LocalIndexAdvancesArePublishedToAuthenticatedPushCapablePeers()
    {
        await using var source = await NodeAsync(23); await using var receiver = await NodeAsync(24);
        var transport = new PushTransport(source.Engine.CreateHandler(receiver.Id), source.Id);
        await receiver.Engine.ConnectAsync(transport, Token);

        File.WriteAllText(Path.Combine(receiver.Root, "pushed.txt"), "live index update");
        await Scan(receiver);
        long target = receiver.Index.ReadIndexHighWater();
        (await transport.PublishedTargets.ReadAsync(Token)).ShouldBe(target);
    }

    [Fact]
    public async Task AuthenticatedIndexPushTriggersIndexExchangeWithoutExplicitSynchronization()
    {
        await using var source = await NodeAsync(25); await using var receiver = await NodeAsync(26);
        var transport = new PushTransport(source.Engine.CreateHandler(receiver.Id), source.Id);
        await receiver.Engine.ConnectAsync(transport, Token);

        File.WriteAllText(Path.Combine(source.Root, "pushed.txt"), "remote index update");
        await Scan(source);
        await transport.PushReceivedTargetAsync(source.Index.ReadIndexHighWater(), Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!receiver.Index.RemotesForPath("pushed.txt").Any()) await Task.Delay(10, timeout.Token);
        receiver.Index.RemotesForPath("pushed.txt").ShouldContain(row => row.DeviceId.SequenceEqual(source.Id));
    }

    [Fact]
    public async Task SharedUploadThrottleGovernsBlockResponsesAcrossTheFolderEngine()
    {
        using var uploadThrottle = new TransferThrottle(128 * 1024);
        await using var source = await NodeAsync(41, uploadThrottle: uploadThrottle);
        await using var receiver = await NodeAsync(42);
        byte[] data = new byte[256 * 1024]; new Random(9017).NextBytes(data);
        File.WriteAllBytes(Path.Combine(source.Root, "limited.bin"), data); await Scan(source);
        var network = new InMemoryNetwork(); var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        network.Register(source.Id, source.Engine.CreateHandler);
        await using var peer = await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id), Token);
        await peer.SynchronizeAsync(cancellationToken: Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        PullPassResult result = await receiver.Engine.RunPassAsync(Token);
        result.Applied.ShouldBe(1); result.Failed.ShouldBe(0);
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1200));
        File.ReadAllBytes(Path.Combine(receiver.Root, "limited.bin")).ShouldBe(data);
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

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public async Task SkewedHandshakeReportsTheCallersOwnSignedOffset(int localJumpMinutes)
    {
        var localTime=new NodeClock(); localTime.Jump(TimeSpan.FromMinutes(localJumpMinutes));
        await using var a=await NodeAsync(1,localTime); await using var b=await NodeAsync(2,new NodeClock());
        var network=new InMemoryNetwork(); var endpoint=network.Register(a.Id,a.Engine.CreateHandler); network.Register(b.Id,b.Engine.CreateHandler);
        var failure=await Should.ThrowAsync<PeerClockException>(()=>a.Engine.ConnectAsync(endpoint.Connect(b.Id),Token));
        failure.Status.OffsetTicks.ShouldBe(-localJumpMinutes*TimeSpan.TicksPerMinute);
        a.Index.RemoteFiles.Count().ShouldBe(0); b.Index.RemoteFiles.Count().ShouldBe(0);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FreshNodeConvergesNestedTombstoneWhenAncestorIsMissingOrAFile(bool fileAncestor, bool directoryChild)
    {
        var time = new NodeClock(); await using var a = await NodeAsync(1, time); await using var b = await NodeAsync(2, new NodeClock());
        Directory.CreateDirectory(Path.Combine(b.Root, "parent"));
        string child=Path.Combine(b.Root,"parent","child");
        if(directoryChild) { Directory.CreateDirectory(child); child=Path.Combine(child,"keep"); }
        File.WriteAllText(child, "old child bytes"); await Scan(b);
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

    [Fact]
    public async Task GrowingSenderTargetIsRereadBeforeBoundedPageCompletesRange()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        File.WriteAllBytes(Path.Combine(b.Root, "item-0000"), []); await Scan(b); int targets = 0;
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler);
        network.Register(b.Id, id =>
        {
            var inner = b.Engine.CreateHandler(id);
            return new ControlledHandler(inner) { Target = async token =>
            {
                long target = await inner.ReadIndexTargetAsync(token);
                if (++targets == 1)
                {
                    for (int i = 1; i < 1500; i++) File.WriteAllBytes(Path.Combine(b.Root, "item-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), []);
                    await Scan(b);
                }
                return target;
            } };
        });
        await using var peer = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        var first = await peer.SynchronizeAsync(1, Token); first.LastSequence.ShouldBe(1000); first.Complete.ShouldBeFalse();
        var range = a.Index.ReadRemoteIndexRange(b.Id)!; range.TargetSequence.ShouldBe(1500); range.ReceivedSequence.ShouldBe(1000); range.Complete.ShouldBeFalse();
        (await peer.SynchronizeAsync(1, Token)).Complete.ShouldBeTrue(); a.Index.RemoteFiles.Count().ShouldBe(1500);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PeerFailureDuringReadRetriesItemAndPreservesIndependentWorkAndCallerCancellation(bool disposedError, bool callerCancellation)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        File.WriteAllText(Path.Combine(b.Root, "first"), "blocked peer bytes"); await Scan(b);
        File.WriteAllText(Path.Combine(c.Root, "second"), "independent peer bytes"); await Scan(c);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler); network.Register(c.Id, c.Engine.CreateHandler);
        network.Register(b.Id, id => new ControlledHandler(b.Engine.CreateHandler(id)) { Block = async (_, _, token) =>
        { entered.SetResult(); ObjectDisposedException.ThrowIf(disposedError, "peer"); await Task.Delay(Timeout.InfiniteTimeSpan, token); return ReadOnlyMemory<byte>.Empty; } });
        await using var ab = await a.Engine.ConnectAsync(ea.Connect(b.Id), Token); await using var ac = await a.Engine.ConnectAsync(ea.Connect(c.Id), Token);
        await ab.SynchronizeAsync(cancellationToken: Token); await ac.SynchronizeAsync(cancellationToken: Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); var pass = a.Engine.RunPassAsync(cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (callerCancellation) cancel.Cancel(); else await ab.DisposeAsync();
            if (callerCancellation) await Should.ThrowAsync<OperationCanceledException>(() => pass);
            else
            {
                var result = await pass; result.Failed.ShouldBe(1); result.Applied.ShouldBe(1); a.Index.Needed.Count().ShouldBe(1);
                File.ReadAllText(Path.Combine(a.Root, "second")).ShouldBe("independent peer bytes");
            }
            File.Exists(Path.Combine(a.Root, "first")).ShouldBeFalse();
        }
        finally { cancel.Cancel(); try { await pass; } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFolderDisposalCallerAwaitsDrainAndObservesFailure(bool failDisposal)
    {
        var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new InMemoryNetwork(); var ea = network.Register(a.Id, a.Engine.CreateHandler);
        network.Register(b.Id, id =>
        {
            var inner = b.Engine.CreateHandler(id);
            return new ControlledHandler(inner) { Close = async () =>
            { entered.SetResult(); await release.Task; await inner.DisposeAsync(); if (failDisposal) throw new IOException("Handler teardown failure"); } };
        });
        await a.Engine.ConnectAsync(ea.Connect(b.Id), Token);
        try
        {
            var first = a.Engine.DisposeAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var second = a.Engine.DisposeAsync().AsTask();
            try { second.IsCompleted.ShouldBeFalse(); } finally { release.TrySetResult(); }
            if (failDisposal)
            {
                var firstError = await Should.ThrowAsync<IOException>(() => first); var secondError = await Should.ThrowAsync<IOException>(() => second);
                ReferenceEquals(firstError, secondError).ShouldBeTrue();
            }
            else await Task.WhenAll(first, second);
        }
        finally
        {
            release.TrySetResult(); try { await a.Engine.DisposeAsync(); } catch (IOException) when (failDisposal) { }
            await a.Index.DisposeAsync();
        }
    }

    private sealed class ControlledHandler(IPeerRequestHandler inner) : IPeerRequestHandler
    {
        public Func<CancellationToken, ValueTask<long>>? Target { get; init; }
        public Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Block { get; init; }
        public Func<ValueTask>? Close { get; init; }
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken) => inner.ExchangeHelloAsync(hello, cancellationToken);
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken) => Target?.Invoke(cancellationToken) ?? inner.ReadIndexTargetAsync(cancellationToken);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken) => inner.ReadIndexAsync(afterSequence, limit, cancellationToken);
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken)
            => Block?.Invoke(hash, length, cancellationToken) ?? inner.ReadBlockAsync(hash, length, cancellationToken);
        public ValueTask DisposeAsync() => Close?.Invoke() ?? inner.DisposeAsync();
    }

    private sealed class PushTransport(IPeerRequestHandler handler, byte[] remoteDevice) : IPeerTransport, IPeerIndexPushTransport
    {
        private readonly Channel<long> targets = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        private readonly Channel<long> published = Channel.CreateUnbounded<long>();
        public byte[] RemoteDeviceId => remoteDevice.ToArray();
        public ChannelReader<long> IndexTargets => targets.Reader;
        public ChannelReader<long> PublishedTargets => published.Reader;
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default)
            => handler.ExchangeHelloAsync(hello, cancellationToken);
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default)
            => handler.ReadIndexTargetAsync(cancellationToken);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000, CancellationToken cancellationToken = default)
            => handler.ReadIndexAsync(afterSequence, limit, cancellationToken);
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken = default)
            => handler.ReadBlockAsync(hash, length, cancellationToken);
        public ValueTask PublishIndexTargetAsync(long highestSequence, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return published.Writer.WriteAsync(highestSequence, cancellationToken); }
        public ValueTask PushReceivedTargetAsync(long highestSequence, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return targets.Writer.WriteAsync(highestSequence, cancellationToken); }
        public async ValueTask DisposeAsync()
        { targets.Writer.TryComplete(); published.Writer.TryComplete(); await handler.DisposeAsync(); }
    }

    private async Task<Node> NodeAsync(ulong identity, TimeProvider? time = null,
        TransferThrottle? uploadThrottle = null, TransferThrottle? downloadThrottle = null, IgnoreMatcher? ignore = null)
    {
        string directory = Path.Combine(fixture, identity.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
        var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token); byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, identity);
        TransportFolder engine = ignore is null
            ? new TransportFolder(root, index, keys, id, settings: null, timeProvider: time, journal: null,
                uploadThrottle: uploadThrottle, downloadThrottle: downloadThrottle)
            : new TransportFolder(root, index, keys, id, settings: null, timeProvider: time, journal: null, ignore: ignore);
        await engine.RunPassAsync(Token); return new(root, index, engine, id);
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
