using System.Text.Json;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Protocol.Handshake;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class InMemoryNetworkTests
{
    private static readonly byte[] A = Device(1), B = Device(2);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegisteredEndpointFreezesIdentityAndEachConnectionOwnsItsHandler()
    {
        var network = new InMemoryNetwork(); byte[] id = A.ToArray(); byte[]? observed = null; List<Handler> handlers = [];
        var a = network.Register(id, _ => new Handler()); id[0] = 99; a.DeviceId[0] = 88;
        network.Register(B, peer => { observed = peer.ToArray(); peer[0] = 77; var handler = new Handler(); handlers.Add(handler); return handler; });
        await using var first = a.Connect(B); await using var second = a.Connect(B);
        observed.ShouldBe(A); first.RemoteDeviceId[0] = 66; first.RemoteDeviceId.ShouldBe(B);
        await first.DisposeAsync(); handlers[0].Disposed.ShouldBeTrue(); handlers[1].Disposed.ShouldBeFalse();
        (await second.ReadIndexTargetAsync(Token)).ShouldBe(9);
        await Should.ThrowAsync<ObjectDisposedException>(() => first.ReadIndexTargetAsync(Token).AsTask());
        Should.Throw<InvalidOperationException>(() => network.Register(A, _ => new Handler()));
        Should.Throw<ArgumentException>(() => a.Connect(A)); Should.Throw<IOException>(() => a.Connect(Device(3)));
    }

    [Fact]
    public async Task HelloAndBlockBuffersAreFrozenAcrossDelayedHandoffs()
    {
        var handler = new Handler(); var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler);
        await using var peer = a.Connect(B);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Hello = async (hello, token) => { received.SetResult(); await release.Task.WaitAsync(token); hello.UtcTicks.ShouldBe(123); return handler.Response; };
        var hello = new Hello { UtcTicks = 123 }; var pending = peer.ExchangeHelloAsync(hello, Token).AsTask();
        await received.Task.WaitAsync(Token); hello.UtcTicks = 456; release.SetResult(); var response = await pending;
        handler.Response.UtcTicks = 999; response.UtcTicks.ShouldBe(321);
        byte[] hash = new byte[32]; hash[0] = 5; byte[] content = [1, 2, 3];
        handler.Block = (request, length, _) => { request.Span[0].ShouldBe((byte)5); length.ShouldBe(3); return ValueTask.FromResult<ReadOnlyMemory<byte>>(content); };
        var bytes = await peer.ReadBlockAsync(hash, 3, Token); content[0] = 99; bytes.ToArray().ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task DirectionalDropsAndPartitionsAffectBothRequestAndResponseAndHeal()
    {
        var handler = new Handler(); var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler);
        await using var peer = a.Connect(B);
        network.ConfigureLink(A, B, new(DropEvery: 2));
        (await peer.ReadIndexTargetAsync(Token)).ShouldBe(9);
        await Should.ThrowAsync<IOException>(() => peer.ReadIndexTargetAsync(Token).AsTask()); handler.TargetCalls.ShouldBe(1);
        network.ConfigureLink(A, B, new()); network.ConfigureLink(B, A, new(Partitioned: true));
        await Should.ThrowAsync<IOException>(() => peer.ReadIndexTargetAsync(Token).AsTask()); handler.TargetCalls.ShouldBe(2);
        network.ConfigureLink(B, A, new()); (await peer.ReadIndexTargetAsync(Token)).ShouldBe(9);
        network.ConfigureLink(A, B, new(Partitioned: true));
        await Should.ThrowAsync<IOException>(() => peer.ReadIndexTargetAsync(Token).AsTask()); handler.TargetCalls.ShouldBe(3);
    }

    [Fact]
    public async Task LatencyIsCancellableAndPartitionChangesAffectAlreadyQueuedRequests()
    {
        var handler = new Handler(); var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler);
        await using var peer = a.Connect(B); network.ConfigureLink(A, B, new(TimeSpan.FromSeconds(30)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = peer.ReadIndexTargetAsync(cancel.Token).AsTask(); pending.IsCompleted.ShouldBeFalse(); cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending); handler.TargetCalls.ShouldBe(0);
        network.ConfigureLink(A, B, new(TimeSpan.FromMilliseconds(100)));
        pending = peer.ReadIndexTargetAsync(Token).AsTask(); pending.IsCompleted.ShouldBeFalse(); network.ConfigureLink(A, B, new(Partitioned: true));
        await Should.ThrowAsync<IOException>(() => pending); handler.TargetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task IndexWaitCannotBlockChunkResponseAndOutstandingLimitIsEnforced()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Page = async (after, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return new(after, after, []); } };
        var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler); await using var peer = a.Connect(B);
        var pending = peer.ReadIndexAsync(0, cancellationToken: Token).AsTask(); await entered.Task.WaitAsync(Token);
        (await peer.ReadBlockAsync(new byte[32], 3, Token)).Length.ShouldBe(3); pending.IsCompleted.ShouldBeFalse();
        var others = Enumerable.Range(0, 63).Select(_ => peer.ReadIndexAsync(0, cancellationToken: Token).AsTask()).ToArray();
        await Should.ThrowAsync<IOException>(() => peer.ReadIndexTargetAsync(Token).AsTask());
        release.SetResult(); await Task.WhenAll(others.Append(pending)); (await peer.ReadIndexTargetAsync(Token)).ShouldBe(9);
    }

    [Fact]
    public async Task DisconnectCancelsAndDrainsBeforeHandlerDisposalAndIsIdempotent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Page = async (after, _, token) =>
        { entered.SetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(after, after, []); } finally { exited.SetResult(); } } };
        var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler); var peer = a.Connect(B);
        var pending = peer.ReadIndexAsync(0, cancellationToken: Token).AsTask(); await entered.Task.WaitAsync(Token);
        await Task.WhenAll(peer.DisposeAsync().AsTask(), peer.DisposeAsync().AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => pending); exited.Task.IsCompletedSuccessfully.ShouldBeTrue(); handler.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidRequestsAndResponsesAreRejectedWithoutCorruptingTheConnection()
    {
        var handler = new Handler(); var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler);
        await using var peer = a.Connect(B);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => peer.ReadIndexAsync(-1).AsTask());
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => peer.ReadIndexAsync(0, 1001).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => peer.ReadBlockAsync(new byte[31], 3).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => peer.ReadBlockAsync(new byte[32], 8 * 1024 * 1024 + 1).AsTask());
        handler.Page = (after, _, _) => ValueTask.FromResult(new PeerIndexPage(after + 1, after + 1, []));
        await Should.ThrowAsync<InvalidDataException>(() => peer.ReadIndexAsync(0, cancellationToken: Token).AsTask());
        handler.Block = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[1]);
        await Should.ThrowAsync<InvalidDataException>(() => peer.ReadBlockAsync(new byte[32], 3, Token).AsTask());
        (await peer.ReadIndexTargetAsync(Token)).ShouldBe(9);
    }

    [Fact]
    public async Task DiagnosticsCorrelateRequestsAndFailuresWithoutPayloadsOrDeviceIds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PeerSync-FabricLogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var journal = new DiagnosticJournal(directory, "fabric"))
            {
                var network = new InMemoryNetwork(journal: journal); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => new Handler());
                await using var peer = a.Connect(B); await peer.ExchangeHelloAsync(new Hello { DeviceName = "secret-device-name" }, Token);
                network.ConfigureLink(A, B, new(Partitioned: true)); await Should.ThrowAsync<IOException>(() => peer.ReadIndexTargetAsync(Token).AsTask());
            }
            string logs = string.Join("\n", Directory.GetFiles(directory, "*.jsonl").Select(File.ReadAllText));
            logs.ShouldContain("transport.request_complete"); logs.ShouldContain("transport.request_failed"); logs.ShouldContain("transport.disconnected");
            logs.ShouldNotContain("secret-device-name"); logs.ShouldNotContain(Convert.ToHexString(A)); logs.ShouldNotContain(Convert.ToHexString(B));
            var sessions = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sessionId").GetString()).Distinct().ToArray();
            sessions.Length.ShouldBe(1); Guid.TryParse(sessions[0], out _).ShouldBeTrue();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillDrainsAndDisposesTheHandler()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Page = async (after, _, token) =>
        {
            using var registration = token.Register(() => throw new IOException("Cancellation callback failure"));
            entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(after, after, []);
        } };
        var network = new InMemoryNetwork(); var a = network.Register(A, _ => new Handler()); network.Register(B, _ => handler); var peer = a.Connect(B);
        var pending = peer.ReadIndexAsync(0, cancellationToken: Token).AsTask(); await entered.Task.WaitAsync(Token);
        await Should.ThrowAsync<AggregateException>(() => peer.DisposeAsync().AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => pending); handler.DisposeCalls.ShouldBe(1);
    }

    private static byte[] Device(byte value) { byte[] id = new byte[32]; id[0] = value; return id; }
    private sealed class Handler : IPeerRequestHandler
    {
        public Hello Response { get; } = new() { UtcTicks = 321 };
        public Func<Hello, CancellationToken, ValueTask<Hello>>? Hello { get; set; }
        public Func<long, int, CancellationToken, ValueTask<PeerIndexPage>>? Page { get; set; }
        public Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Block { get; set; }
        public int TargetCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Disposed => DisposeCalls != 0;
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken) => Hello?.Invoke(hello, cancellationToken) ?? ValueTask.FromResult(Response);
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken) { TargetCalls++; return ValueTask.FromResult(9L); }
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken)
            => Page?.Invoke(afterSequence, limit, cancellationToken) ?? ValueTask.FromResult(new PeerIndexPage(afterSequence, afterSequence, []));
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken)
            => Block?.Invoke(hash, length, cancellationToken) ?? ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[length]);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
