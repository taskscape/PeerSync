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

public sealed class PeerClockStatusSourceTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-ClockStatus-"+Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys=FolderKeys.CreateNew();
    private static CancellationToken Token=>TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public async Task BothEndpointsRetainLocalClosedReasonsAndFreshConnectionClearsWarnings(int localJumpMinutes)
    {
        var time=new Clock(); time.Jump(TimeSpan.FromMinutes(localJumpMinutes));
        await using var a=await NodeAsync(1,time); await using var b=await NodeAsync(2,new Clock());
        var network=new InMemoryNetwork(); var endpoint=network.Register(a.Id,a.Engine.CreateHandler); network.Register(b.Id,b.Engine.CreateHandler);
        var error=await Should.ThrowAsync<PeerClockException>(()=>a.Engine.ConnectAsync(endpoint.Connect(b.Id),Token));
        error.Status.OffsetTicks.ShouldBe(-localJumpMinutes*TimeSpan.TicksPerMinute);
        var local=a.Engine.ReadClockStatuses().Sessions.Single(); var remote=b.Engine.ReadClockStatuses().Sessions.Single();
        local.IsConnected.ShouldBeFalse(); remote.IsConnected.ShouldBeFalse();
        local.RemoteDeviceId.ShouldBe(b.Id); remote.RemoteDeviceId.ShouldBe(a.Id); local.SessionId.ShouldNotBe(remote.SessionId);
        local.Clock.OffsetTicks.ShouldBe(-localJumpMinutes*TimeSpan.TicksPerMinute); remote.Clock.OffsetTicks.ShouldBe(localJumpMinutes*TimeSpan.TicksPerMinute);
        a.Index.RemoteFiles.Count().ShouldBe(0); b.Index.RemoteFiles.Count().ShouldBe(0);
        time.Jump(TimeSpan.FromMinutes(-localJumpMinutes));
        var connected=await a.Engine.ConnectAsync(endpoint.Connect(b.Id),Token);
        a.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(PeerClockState.Accepted);
        b.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(PeerClockState.Accepted);
        local.Clock.State.ShouldBe(PeerClockState.Skewed); local.IsConnected.ShouldBeFalse();
        await connected.DisposeAsync(); a.Engine.ReadClockStatuses().Sessions.ShouldBeEmpty(); b.Engine.ReadClockStatuses().Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task HelloExchangesTimeWhileMissingAndSkewedSessionsStillCannotReadIndexOrBlocks()
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time); var peer=Device(2);
        var handler=node.Engine.CreateHandler(peer);
        node.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(PeerClockState.AwaitingHello);
        await Should.ThrowAsync<PeerClockException>(()=>handler.ReadIndexTargetAsync(Token).AsTask());
        var response=await handler.ExchangeHelloAsync(Hello(peer,time.GetUtcNow().UtcTicks+TimeSpan.FromMinutes(11).Ticks),Token);
        response.UtcTicks.ShouldBe(time.GetUtcNow().UtcTicks);
        await Should.ThrowAsync<PeerClockException>(()=>handler.ReadIndexAsync(0,1000,Token).AsTask());
        await Should.ThrowAsync<PeerClockException>(()=>handler.ReadBlockAsync(new byte[32],1,Token).AsTask());
        await handler.DisposeAsync(); var status=node.Engine.ReadClockStatuses().Sessions.Single();
        status.IsConnected.ShouldBeFalse(); status.Clock.State.ShouldBe(PeerClockState.Skewed);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("nonce")]
    [InlineData("protocol")]
    public async Task ForgedHelloNeverBecomesAnAuthenticatedClockSample(string field)
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time); var peer=Device(2); var handler=node.Engine.CreateHandler(peer);
        var hello=Hello(peer,time.GetUtcNow().UtcTicks);
        if(field=="identity") hello.DeviceId=ByteString.CopyFrom(Device(99));
        if(field=="nonce") hello.Nonce=ByteString.Empty;
        if(field=="protocol") hello.ProtocolVersion=999;
        await Should.ThrowAsync<InvalidDataException>(()=>handler.ExchangeHelloAsync(hello,Token).AsTask());
        var status=node.Engine.ReadClockStatuses().Sessions.Single(); status.RemoteDeviceId.ShouldBe(peer); status.Clock.State.ShouldBe(PeerClockState.AwaitingHello);
        await handler.DisposeAsync(); node.Engine.ReadClockStatuses().Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task StatusPollingDetectsLocalClockJumpAndRecoveryWithoutIndexTraffic()
    {
        var time=new Clock(); await using var a=await NodeAsync(1,time); await using var b=await NodeAsync(2,new Clock());
        var network=new InMemoryNetwork(); var endpoint=network.Register(a.Id,a.Engine.CreateHandler); network.Register(b.Id,b.Engine.CreateHandler);
        await using var connection=await a.Engine.ConnectAsync(endpoint.Connect(b.Id),Token);
        var accepted=a.Engine.ReadClockStatuses().Sessions.Single(); time.Jump(TimeSpan.FromMinutes(12));
        var changed=a.Engine.ReadClockStatuses().Sessions.Single(); changed.Clock.State.ShouldBe(PeerClockState.Skewed);
        changed.Clock.OffsetTicks.ShouldBe(-12*TimeSpan.TicksPerMinute); changed.IsConnected.ShouldBeTrue();
        accepted.Clock.State.ShouldBe(PeerClockState.Accepted); accepted.Clock.OffsetTicks.ShouldBe(0);
        time.Jump(TimeSpan.FromMinutes(-12)); a.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(PeerClockState.Accepted);
        a.Index.ReadIndexHighWater().ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSampleReasonsSurviveDisconnectWithoutGrantingAdmission(bool invalidElapsed)
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time); var peer=Device(2); var handler=node.Engine.CreateHandler(peer);
        await handler.ExchangeHelloAsync(Hello(peer,invalidElapsed?time.GetUtcNow().UtcTicks:0),Token);
        if(invalidElapsed) time.Elapsed(TimeSpan.FromSeconds(-1));
        var expected=invalidElapsed?PeerClockState.InvalidElapsed:PeerClockState.InvalidTimestamp;
        node.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(expected);
        await Should.ThrowAsync<PeerClockException>(()=>handler.ReadIndexTargetAsync(Token).AsTask());
        await handler.DisposeAsync(); var closed=node.Engine.ReadClockStatuses().Sessions.Single();
        closed.Clock.State.ShouldBe(expected); closed.Clock.OffsetTicks.ShouldBeNull(); closed.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task RecentClosedHistoryAndReadPagesHaveIndependentBounds()
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time);
        for(ulong device=2;device<72;device++)
        {
            var peer=Device(device); var handler=node.Engine.CreateHandler(peer);
            await handler.ExchangeHelloAsync(Hello(peer,time.GetUtcNow().UtcTicks+11*TimeSpan.TicksPerMinute),Token); await handler.DisposeAsync();
        }
        var full=node.Engine.ReadClockStatuses(); full.Sessions.Count.ShouldBe(64); full.Truncated.ShouldBeFalse();
        full.Sessions.ShouldNotContain(status=>status.RemoteDeviceId.SequenceEqual(Device(2)));
        var small=node.Engine.ReadClockStatuses(3); small.Sessions.Count.ShouldBe(3); small.Truncated.ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(()=>node.Engine.ReadClockStatuses(0));
        Should.Throw<ArgumentOutOfRangeException>(()=>node.Engine.ReadClockStatuses(1001));
    }

    [Fact]
    public async Task LateOlderCloseCannotReplaceNewerClosedReasonAndValidSessionSuppressesOldWarning()
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time); var peer=Device(2);
        var older=node.Engine.CreateHandler(peer); await older.ExchangeHelloAsync(Hello(peer,time.GetUtcNow().UtcTicks+11*TimeSpan.TicksPerMinute),Token);
        var newer=node.Engine.CreateHandler(peer); await newer.ExchangeHelloAsync(Hello(peer,time.GetUtcNow().UtcTicks-12*TimeSpan.TicksPerMinute),Token);
        await newer.DisposeAsync(); await older.DisposeAsync();
        node.Engine.ReadClockStatuses().Sessions.Single().Clock.OffsetTicks.ShouldBe(-12*TimeSpan.TicksPerMinute);
        var healthy=node.Engine.CreateHandler(peer); await healthy.ExchangeHelloAsync(Hello(peer,time.GetUtcNow().UtcTicks),Token);
        node.Engine.ReadClockStatuses().Sessions.Single().Clock.State.ShouldBe(PeerClockState.Accepted);
        await healthy.DisposeAsync(); node.Engine.ReadClockStatuses().Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ActivePagesReportTruncationAndRemainFrozenAfterLifecycleChanges()
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time); var handlers=new List<IPeerRequestHandler>();
        try
        {
            for(ulong id=2;id<1003;id++) handlers.Add(node.Engine.CreateHandler(Device(id)));
            var page=node.Engine.ReadClockStatuses(); page.Sessions.Count.ShouldBe(1000); page.Truncated.ShouldBeTrue();
            await handlers[0].ExchangeHelloAsync(Hello(Device(2),time.GetUtcNow().UtcTicks),Token);
            page.Sessions[0].Clock.State.ShouldBe(PeerClockState.AwaitingHello);
        }
        finally { foreach(var handler in handlers) await handler.DisposeAsync(); }
        await node.Engine.DisposeAsync(); Should.Throw<ObjectDisposedException>(()=>node.Engine.ReadClockStatuses());
    }

    [Fact]
    public async Task ClosedDisplayReasonDoesNotReviveFacadeAdmissionAndFreshHelloReopensOnlyFreshWork()
    {
        var time=new Clock(); await using var node=await NodeAsync(1,time);
        using var puller=new Puller(node.Root,node.Index,keys,node.Id,(_,_,_,_)=>throw new InvalidOperationException("No blocks"),timeProvider:time);
        var session=puller.OpenPeerIndexSession(Device(2)); session.ObserveHello(Hello(Device(2),time.GetUtcNow().UtcTicks+11*TimeSpan.TicksPerMinute));
        session.Disconnect(); session.Disconnect();
        session.ClockStatus.State.ShouldBe(PeerClockState.AwaitingHello);
        session.ReadDisplayClockStatus().Clock.State.ShouldBe(PeerClockState.Skewed); session.ReadDisplayClockStatus().IsConnected.ShouldBeFalse();
        Should.Throw<PeerClockException>(()=>session.ReadIndexTarget());
        session.ObserveHello(Hello(Device(2),time.GetUtcNow().UtcTicks));
        session.ReadDisplayClockStatus().Clock.State.ShouldBe(PeerClockState.Accepted); session.ReadDisplayClockStatus().IsConnected.ShouldBeTrue();
        session.ReadIndexTarget().ShouldBe(0);
    }

    private async Task<Node> NodeAsync(ulong value,Clock clock)
    {
        string root=Path.Combine(fixture,value.ToString(System.Globalization.CultureInfo.InvariantCulture),"tree"); Directory.CreateDirectory(root);
        var index=await FolderIndex.OpenAsync(Path.Combine(Path.GetDirectoryName(root)!,"index.db"),Token); byte[] id=Device(value);
        var engine=new TransportFolder(root,index,keys,id,timeProvider:clock); await engine.RunPassAsync(Token); return new(root,index,engine,id);
    }
    private static byte[] Device(ulong value) { byte[] id=new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id,value); return id; }
    private static Hello Hello(byte[] device,long utc)=>new() { ProtocolVersion=1,DeviceId=ByteString.CopyFrom(device),Nonce=ByteString.CopyFrom(new byte[32]),UtcTicks=utc };
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc=new(2026,10,19,12,0,0,TimeSpan.Zero); private long elapsed;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>elapsed;
        public override DateTimeOffset GetUtcNow()=>utc;
        public void Jump(TimeSpan delta)=>utc+=delta;
        public void Elapsed(TimeSpan delta)=>elapsed+=delta.Ticks;
    }
    private sealed record Node(string Root,FolderIndex Index,TransportFolder Engine,byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    public void Dispose() { keys.Dispose(); if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
}
