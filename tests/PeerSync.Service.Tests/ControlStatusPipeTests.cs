using System.Buffers.Binary;
using System.Security.Principal;
using System.IO.Pipes;
using System.Security.AccessControl;
using Grpc.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net;
using PeerSync.Net.InMemory;
using PeerSync.Service.Control;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class ControlStatusPipeTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ControlHost-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Pipe => "PeerSync.Control.HostTests-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ActualPipeReturnsUnavailableEmptyRuntimeAndCancellationReleasesEvents()
    {
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe; var runtime = new ControlStatusRuntime();
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token);
        using var client = Client(pipe, identity.User!); var first = await client.GetStatusAsync(Token); var second = await client.GetStatusAsync(Token);
        first.InstanceId.ShouldNotBe(Guid.Empty); second.InstanceId.ShouldBe(first.InstanceId); second.Sequence.ShouldBeGreaterThan(first.Sequence);
        first.ProductionSyncAvailable.ShouldBeFalse(); first.Clocks.ShouldBeEmpty(); first.ClockStatusTruncated.ShouldBeFalse();
        host.BoundAddresses.Count.ShouldBe(1); host.BoundAddresses.ShouldAllBe(address => address.Contains(pipe, StringComparison.Ordinal));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var stream = client.WatchEventsAsync(stop.Token).GetAsyncEnumerator(stop.Token); (await stream.MoveNextAsync()).ShouldBeTrue();
        host.ActiveSubscriptions.ShouldBe(1); stream.Current.Sequence.ShouldBeGreaterThan(second.Sequence);
        stop.Cancel(); await Should.ThrowAsync<RpcException>(() => stream.MoveNextAsync().AsTask());
        await Until(() => host.ActiveSubscriptions == 0);
    }

    [Fact]
    public async Task EveryRpcIdentifiesAndRejectsAnUnauthorizedCallerBeforeReadingRuntime()
    {
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe; var runtime = new ControlStatusRuntime(); var before = runtime.ReadStatus();
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [], cancellationToken: Token);
        using var client = Client(pipe, identity.User!);
        (await Should.ThrowAsync<RpcException>(() => client.GetStatusAsync(Token))).StatusCode.ShouldBe(StatusCode.PermissionDenied);
        await using var events = client.WatchEventsAsync(Token).GetAsyncEnumerator(Token);
        (await Should.ThrowAsync<RpcException>(() => events.MoveNextAsync().AsTask())).StatusCode.ShouldBe(StatusCode.PermissionDenied);
        host.ActiveSubscriptions.ShouldBe(0); runtime.ReadStatus().Sequence.ShouldBe(before.Sequence + 1);
    }

    [Fact]
    public async Task ActualEngineClockFailuresAndRecoveryReachTheControlClientWithDerivedFolderIdentity()
    {
        await using var a = await Node(1); var clock = new Clock(); clock.Jump(TimeSpan.FromMinutes(11)); await using var b = await Node(2, clock);
        var runtime = new ControlStatusRuntime(); using var registration = runtime.RegisterFolder(a.Engine);
        byte[] id = a.Engine.FolderId; id[0] ^= 0xFF;
        Should.Throw<InvalidOperationException>(() => runtime.RegisterFolder(a.Engine));
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token); using var client = Client(pipe, identity.User!);
        var network = new InMemoryNetwork(); var endpoint = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await Should.ThrowAsync<PeerClockException>(() => a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token));
        var skewed = await client.GetStatusAsync(Token); var row = skewed.Clocks.Single();
        row.FolderId.ShouldBe(keys.FolderId()); row.Session.RemoteDeviceId.ShouldBe(b.Id); row.Session.IsConnected.ShouldBeFalse(); row.Session.Clock.State.ShouldBe(PeerClockState.Skewed);
        row.Session.Clock.OffsetTicks!.Value.ShouldBeInRange(11 * TimeSpan.TicksPerMinute - TimeSpan.TicksPerSecond, 11 * TimeSpan.TicksPerMinute + TimeSpan.TicksPerSecond);
        a.Index.RemoteFiles.Count().ShouldBe(0); b.Index.RemoteFiles.Count().ShouldBe(0);
        clock.Jump(TimeSpan.FromMinutes(-11)); await using var connection = await a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
        var recovered = await client.GetStatusAsync(Token); recovered.Clocks.Single().Session.Clock.State.ShouldBe(PeerClockState.Accepted);
        recovered.Clocks.Single().Session.IsConnected.ShouldBeTrue(); row.Session.Clock.State.ShouldBe(PeerClockState.Skewed);
        await connection.DisposeAsync(); (await client.GetStatusAsync(Token)).Clocks.ShouldBeEmpty();
        registration.Dispose(); (await client.GetStatusAsync(Token)).Clocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task WatchEventsCarriesLiveClockFailureAndFreshHandshakeRecovery()
    {
        await using var a = await Node(1); var clock = new Clock(); clock.Jump(TimeSpan.FromMinutes(-11)); await using var b = await Node(2, clock);
        var runtime = new ControlStatusRuntime(); using var registration = runtime.RegisterFolder(a.Engine);
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token); using var client = Client(pipe, identity.User!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var events = client.WatchEventsAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        (await events.MoveNextAsync()).ShouldBeTrue(); events.Current.Clocks.ShouldBeEmpty(); var original = events.Current;
        var network = new InMemoryNetwork(); var endpoint = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await Should.ThrowAsync<PeerClockException>(() => a.Engine.ConnectAsync(endpoint.Connect(b.Id), deadline.Token));
        do { (await events.MoveNextAsync()).ShouldBeTrue(); } while (events.Current.Clocks.Count == 0);
        events.Current.InstanceId.ShouldBe(original.InstanceId); events.Current.Sequence.ShouldBeGreaterThan(original.Sequence);
        events.Current.Clocks.Single().Session.Clock.State.ShouldBe(PeerClockState.Skewed); events.Current.Clocks.Single().Session.Clock.OffsetTicks!.Value.ShouldBeLessThan(0L);
        clock.Jump(TimeSpan.FromMinutes(11)); await using var connected = await a.Engine.ConnectAsync(endpoint.Connect(b.Id), deadline.Token);
        do { (await events.MoveNextAsync()).ShouldBeTrue(); } while (events.Current.Clocks.Single().Session.Clock.State != PeerClockState.Accepted);
        events.Current.Clocks.Single().Session.IsConnected.ShouldBeTrue(); original.Clocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task NativeAclReservesInstanceCreationForOwnerAndDiagnosticsRedactCallerAndPeerData()
    {
        Directory.CreateDirectory(fixture); using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "control", debugEnabled: true);
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, new(), [group], journal, Token);
        await using (var connection = await ControlPipeConnector.ConnectVerifiedAsync(new(pipe, identity.User!, (uint)Environment.ProcessId), Token))
        {
            var security = ((NamedPipeClientStream)connection).GetAccessControl();
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            var owner = rules.Single(rule => rule.IdentityReference.Equals(identity.User));
            (owner.PipeAccessRights & PipeAccessRights.CreateNewInstance).ShouldBe(PipeAccessRights.CreateNewInstance);
            var client = rules.Single(rule => rule.IdentityReference.Equals(group));
            (client.PipeAccessRights & PipeAccessRights.CreateNewInstance).ShouldBe((PipeAccessRights)0);
            client.AccessControlType.ShouldBe(AccessControlType.Allow);
        }
        using var rejected = Client(pipe, identity.User!);
        (await Should.ThrowAsync<RpcException>(() => rejected.GetStatusAsync(Token))).StatusCode.ShouldBe(StatusCode.PermissionDenied);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldContain("control.caller_rejected");
        log.ShouldContain("sessionId"); log.ShouldNotContain(identity.User!.Value); log.ShouldNotContain(keys.ExportText()); log.ShouldNotContain(fixture);
    }

    [Fact]
    public async Task RowBoundAndTruncationSurviveActualProtobufTransport()
    {
        await using var node = await Node(1); var handlers = new List<IPeerRequestHandler>(); var runtime = new ControlStatusRuntime();
        using var registration = runtime.RegisterFolder(node.Engine);
        try
        {
            for (ulong i = 2; i < 1003; i++) handlers.Add(node.Engine.CreateHandler(Device(i)));
            using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
            await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token); using var client = Client(pipe, identity.User!);
            var status = await client.GetStatusAsync(Token); status.Clocks.Count.ShouldBe(1000); status.ClockStatusTruncated.ShouldBeTrue();
            status.Clocks.Select(row => row.Session.SessionId).Distinct().Count().ShouldBe(1000);
            status.Clocks.ShouldAllBe(row => row.Session.Clock.State == PeerClockState.AwaitingHello);
        }
        finally { foreach (var handler in handlers) await handler.DisposeAsync(); }
    }

    [Fact]
    public async Task EventSubscriptionLimitRejectsExcessAndCancellationFreesASlot()
    {
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, new(), [identity.User!], cancellationToken: Token); using var client = Client(pipe, identity.User!);
        var streams = new List<(CancellationTokenSource Stop, IAsyncEnumerator<ControlStatusSnapshot> Stream)>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.CancelAfter(TimeSpan.FromSeconds(20));
                var stream = client.WatchEventsAsync(stop.Token).GetAsyncEnumerator(stop.Token); streams.Add((stop, stream)); (await stream.MoveNextAsync()).ShouldBeTrue();
            }
            host.ActiveSubscriptions.ShouldBe(32);
            await using (var rejected = client.WatchEventsAsync(Token).GetAsyncEnumerator(Token))
                (await Should.ThrowAsync<RpcException>(() => rejected.MoveNextAsync().AsTask())).StatusCode.ShouldBe(StatusCode.ResourceExhausted);
            streams[0].Stop.Cancel(); await streams[0].Stream.DisposeAsync(); streams[0].Stop.Dispose(); streams.RemoveAt(0);
            await Until(() => host.ActiveSubscriptions == 31);
            using var replacementStop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            await using var replacement = client.WatchEventsAsync(replacementStop.Token).GetAsyncEnumerator(replacementStop.Token); (await replacement.MoveNextAsync()).ShouldBeTrue();
            host.ActiveSubscriptions.ShouldBe(32); replacementStop.Cancel();
        }
        finally { foreach (var entry in streams) { entry.Stop.Cancel(); await entry.Stream.DisposeAsync(); entry.Stop.Dispose(); } }
        await Until(() => host.ActiveSubscriptions == 0);
    }

    [Fact]
    public async Task ShutdownCancelsEventProducersAndAllDisposalCallersShareCompletion()
    {
        using var identity = WindowsIdentity.GetCurrent(); string pipe = Pipe;
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, new(), [identity.User!], cancellationToken: Token); using var client = Client(pipe, identity.User!);
        await using var stream = client.WatchEventsAsync(Token).GetAsyncEnumerator(Token); (await stream.MoveNextAsync()).ShouldBeTrue(); host.ActiveSubscriptions.ShouldBe(1);
        Task first = host.DisposeAsync().AsTask(), second = host.DisposeAsync().AsTask(); await Task.WhenAll(first, second);
        host.ActiveSubscriptions.ShouldBe(0);
    }

    private async Task<NodeState> Node(ulong value, TimeProvider? clock = null)
    {
        string directory = Path.Combine(fixture, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
        var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token); byte[] id = Device(value);
        var engine = new TransportFolder(root, index, keys, id, timeProvider: clock); await engine.RunPassAsync(Token); return new(index, engine, id);
    }
    private static ControlStatusClient Client(string pipe, SecurityIdentifier owner) => ControlStatusClient.CreateFixture(new(pipe, owner, (uint)Environment.ProcessId));
    private static byte[] Device(ulong value) { byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, value); return id; }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed record NodeState(FolderIndex Index, TransportFolder Engine, byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    private sealed class Clock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
        internal void Jump(TimeSpan duration) => Interlocked.Add(ref offset, duration.Ticks);
    }
    public void Dispose()
    {
        keys.Dispose(); if (!Directory.Exists(fixture)) return;
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ControlHost-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        Directory.Delete(fixture, true);
    }
}
