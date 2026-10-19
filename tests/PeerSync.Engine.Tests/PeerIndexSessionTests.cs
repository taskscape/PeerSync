using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class PeerIndexSessionTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-ClockSession-"+Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys=FolderKeys.CreateNew();
    private static readonly byte[] Own=Device(1),Peer=Device(2);
    private string Root=>Path.Combine(fixture,"tree");
    private string Database=>Path.Combine(fixture,"index.db");
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    public PeerIndexSessionTests()=>Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(601)]
    [InlineData(-601)]
    public async Task SkewedPeerCannotReadReceiveOrAnnounceIndex(int offsetSeconds)
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks+offsetSeconds*TimeSpan.TicksPerSecond });
        Should.Throw<PeerClockException>(()=>peer.ReadIndex(0));
        await Should.ThrowAsync<PeerClockException>(()=>peer.ReceiveAsync(new(0,1,[Record()]),Token));
        await Should.ThrowAsync<PeerClockException>(()=>peer.AnnounceAsync(1,Token));
        index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task NoHelloAndDisconnectDenyExchangeAndFreshHelloRestoresIt()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer);
        await Should.ThrowAsync<PeerClockException>(()=>peer.ReceiveAsync(new(0,1,[Record()]),Token));
        peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); await peer.AnnounceAsync(1,Token); peer.Disconnect();
        await Should.ThrowAsync<PeerClockException>(()=>peer.ReceiveAsync(new(0,1,[Record()]),Token));
        peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); (await peer.ReceiveAsync(new(0,1,[Record()]),Token)).Applied.ShouldBeTrue();
        index.ReadRemoteIndexRange(Peer)!.Complete.ShouldBeTrue(); index.Needed.Count().ShouldBe(1);
    }

    [Fact]
    public async Task SessionIdentityIsFrozenFromAuthenticatedContextAndNeverTakenFromHello()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        byte[] caller=Peer.ToArray(); var peer=puller.OpenPeerIndexSession(caller); caller[0]=99; peer.RemoteDeviceId[0]=88;
        peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks,DeviceId=ByteString.CopyFrom(Device(9)) });
        await peer.ReceiveAsync(new(0,1,[Record()]),Token);
        index.RemoteFiles.ReadAll().Single().DeviceId.ShouldBe(Peer); index.Devices.Find(Device(9)).ShouldBeNull(); peer.RemoteDeviceId.ShouldBe(Peer);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public async Task ClockJumpWhileWriterIsBusyCannotCommitQueuedIndexOrRange(bool announce,bool empty)
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        using var release=new ManualResetEventSlim(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking=index.RecomputeNeededAsync(["blocker"],_=>{ entered.TrySetResult(); if(!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException(); return null; },Token);
        Task? pending=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10),Token);
            pending=announce?peer.AnnounceAsync(1,Token):peer.ReceiveAsync(new(0,1,empty?[]:[Record()]),Token);
            pending.IsCompleted.ShouldBeFalse(); clock.Jump(TimeSpan.FromMinutes(11));
        }
        finally { release.Set(); }
        await blocking; await Should.ThrowAsync<PeerClockException>(()=>pending!);
        index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task DisconnectWhileReferenceGateIsBusyDeniesTheQueuedOperation()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); Task<RemoteIndexResult> pending;
        using(await FolderReferenceGate.AcquireAsync(index,Token))
        {
            pending=peer.ReceiveAsync(new(0,1,[Record()]),Token); pending.IsCompleted.ShouldBeFalse(); peer.Disconnect();
        }
        await Should.ThrowAsync<PeerSessionExpiredException>(()=>pending); index.Devices.Count().ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshHelloCannotReviveOperationQueuedBeforeDisconnect(bool announce)
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); Task pending;
        using(await FolderReferenceGate.AcquireAsync(index,Token))
        {
            pending=announce?peer.AnnounceAsync(1,Token):peer.ReceiveAsync(new(0,1,[Record()]),Token);
            pending.IsCompleted.ShouldBeFalse(); peer.Disconnect(); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        }
        await Should.ThrowAsync<IOException>(()=>pending); index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0);
        await peer.ReceiveAsync(new(0,1,[Record()]),Token); index.RemoteFiles.Count().ShouldBe(1);
    }

    [Fact]
    public async Task OutgoingPagesKeepExactPublishedSignaturesAndRecheckClockBeforeHandoff()
    {
        File.WriteAllText(Path.Combine(Root,"local"),"published local bytes"); await using var index=await FolderIndex.OpenAsync(Database,Token);
        var clock=new SessionClock(); using var puller=Coordinator(index,clock); await puller.RunPassAsync(Token);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        var page=peer.ReadIndex(0); var original=index.LocalFiles.Find("local")!.Record!;
        page.Records.Single().Body.ToArray().ShouldBe(original.Body.ToArray()); page.Records.Single().Signature.ToArray().ShouldBe(original.Signature.ToArray());
        peer.ReadIndex(page.LastSequence).Records.ShouldBeEmpty();
        int reads=0; clock.OnUtcRead=()=>{ if(++reads==2) clock.Jump(TimeSpan.FromMinutes(11)); };
        Should.Throw<PeerClockException>(()=>peer.ReadIndex(0)); reads.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshHelloCannotReviveOperationQueuedInWriterBeforeDisconnect(bool announce)
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        using var release=new ManualResetEventSlim(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker=index.RecomputeNeededAsync(["blocker"],_=>{ entered.SetResult(); if(!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); return null; },Token);
        Task? pending=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10),Token);
            pending=announce?peer.AnnounceAsync(1,Token):peer.ReceiveAsync(new(0,1,[Record()]),Token);
            pending.IsCompleted.ShouldBeFalse(); peer.Disconnect(); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        }
        finally { release.Set(); }
        await blocker; await Should.ThrowAsync<PeerSessionExpiredException>(()=>pending!);
        index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0);
        await peer.ReceiveAsync(new(0,1,[Record()]),Token); index.RemoteFiles.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutgoingHandoffCannotSurviveDisconnectAndFreshHello(bool targetOnly)
    {
        File.WriteAllText(Path.Combine(Root,"local"),"published"); await using var index=await FolderIndex.OpenAsync(Database,Token);
        var clock=new SessionClock(); using var puller=Coordinator(index,clock); await puller.RunPassAsync(Token);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); int reads=0;
        clock.OnUtcRead=()=>
        {
            if(++reads==2) { clock.OnUtcRead=null; peer.Disconnect(); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); }
        };
        if(targetOnly) Should.Throw<PeerSessionExpiredException>(()=>peer.ReadIndexTarget());
        else Should.Throw<PeerSessionExpiredException>(()=>peer.ReadIndex(0));
        peer.ReadIndex(0).Records.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ClockAdmissionDoesNotBypassRecordAuthenticationOrCancellation()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); using var puller=Coordinator(index,clock);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        var valid=Record(); var invalid=new SignedRecord(valid.Body,new byte[64],1);
        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(()=>peer.ReceiveAsync(new(0,1,[invalid]),Token));
        using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(()=>peer.ReceiveAsync(new(0,1,[valid]),cancelled.Token));
        index.Devices.Count().ShouldBe(0); index.RemoteFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task PeerClockAndIndexDiagnosticsCorrelateWithoutPeerNamesIdsOrPayloads()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new SessionClock(); string directory=Path.Combine(fixture,"logs");
        using var journal=new DiagnosticJournal(directory,"peer"); using var puller=new Puller(Root,index,keys,Own,(_,_,_,_)=>throw new InvalidOperationException(),timeProvider:clock,journal:journal);
        var peer=puller.OpenPeerIndexSession(Peer); peer.ObserveHello(new Hello { UtcTicks=clock.UtcTicks,DeviceName="private device" });
        await peer.ReceiveAsync(new(0,1,[Record()]),Token); peer.ReadIndex(0);
        var entries=File.ReadAllLines(Path.Combine(directory,"debug.jsonl")).Select(line=>System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            var accepted=entries.Single(doc=>doc.RootElement.GetProperty("eventName").GetString()=="clock.accepted").RootElement;
            var peerReceive=entries.Single(doc=>doc.RootElement.GetProperty("eventName").GetString()=="peer.index_receive_complete").RootElement;
            var indexReceive=entries.Single(doc=>doc.RootElement.GetProperty("eventName").GetString()=="index.receive_complete").RootElement;
            peerReceive.GetProperty("sessionId").GetString().ShouldBe(accepted.GetProperty("sessionId").GetString());
            peerReceive.GetProperty("traceId").GetString().ShouldBe(indexReceive.GetProperty("traceId").GetString());
            peerReceive.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
            string text=File.ReadAllText(Path.Combine(directory,"debug.jsonl")); text.ShouldNotContain("private device"); text.ShouldNotContain("payload");
            text.ShouldNotContain(keys.ExportText()); text.ShouldNotContain(Convert.ToHexString(Peer));
        }
        finally { foreach(var entry in entries) entry.Dispose(); }
    }

    private Puller Coordinator(FolderIndex index,TimeProvider clock)=>new(Root,index,keys,Own,(_,_,_,_)=>throw new InvalidOperationException("No download"),timeProvider:clock);
    private SignedRecord Record()=>SignedRecordFactory.Create(ConflictResolverTests.Body(2,1,DateTime.UtcNow.Ticks,DateTime.UtcNow.Ticks,"payload",false),keys,1);
    private static byte[] Device(ulong value) { byte[] bytes=new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(bytes,value); return bytes; }
    public void Dispose()
    {
        keys.Dispose(); foreach(string file in Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
        Directory.Delete(fixture,recursive:true);
    }
    private sealed class SessionClock : TimeProvider
    {
        private long ticks=DateTime.UtcNow.Ticks;
        public long UtcTicks=>Interlocked.Read(ref ticks);
        public Action? OnUtcRead { get; set; }
        public void Jump(TimeSpan delta)=>Interlocked.Add(ref ticks,delta.Ticks);
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>0;
        public override DateTimeOffset GetUtcNow() { OnUtcRead?.Invoke(); return new(UtcTicks,TimeSpan.Zero); }
    }
}
