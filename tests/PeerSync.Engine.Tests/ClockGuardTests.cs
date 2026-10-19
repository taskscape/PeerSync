using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Handshake;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ClockGuardTests : IDisposable
{
    private readonly string logs=Path.Combine(Path.GetTempPath(),"PeerSync-Clock-"+Guid.NewGuid().ToString("N"));
    private const long Limit=600*TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData(0L,true)]
    [InlineData(Limit,true)]
    [InlineData(-Limit,true)]
    [InlineData(Limit+1,false)]
    [InlineData(-Limit-1,false)]
    public void BothOffsetSignsPermitExactTenMinuteBoundary(long offset,bool allowed)
    {
        var clock=new SampleClock(); var guard=new ClockGuard(clock);
        var status=guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks+offset });
        status.OffsetTicks.ShouldBe(offset); status.IndexExchangeAllowed.ShouldBe(allowed);
        if(allowed) guard.RequireIndexExchangeAllowed();
        else Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed).Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(3155378976000000000L)]
    public void MissingOrUnrepresentableRemoteTimeBlocksIndexExchange(long ticks)
    {
        var guard=new ClockGuard(new SampleClock()); var status=guard.ObserveHello(new Hello { UtcTicks=ticks });
        status.State.ShouldBe(PeerClockState.InvalidTimestamp); status.OffsetTicks.ShouldBeNull();
        Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed);
    }

    [Fact]
    public void HelloIsFrozenAndPeerUtcAdvancesWithMonotonicTime()
    {
        var clock=new SampleClock(); var guard=new ClockGuard(clock); var hello=new Hello { UtcTicks=clock.UtcTicks };
        guard.ObserveHello(hello); hello.UtcTicks=0; clock.Advance(TimeSpan.FromHours(12),TimeSpan.FromHours(12));
        guard.GetStatus().ShouldBe(new(PeerClockState.Accepted,0)); guard.RequireIndexExchangeAllowed();
    }

    [Fact]
    public void LocalClockJumpBlocksAnExistingSessionUntilFreshSampleRestoresIt()
    {
        var clock=new SampleClock(); var guard=new ClockGuard(clock); guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        clock.Advance(TimeSpan.FromSeconds(1),TimeSpan.FromMinutes(12)+TimeSpan.FromSeconds(1));
        var status=guard.GetStatus(); status.State.ShouldBe(PeerClockState.Skewed); status.OffsetTicks.ShouldBe(-12*TimeSpan.TicksPerMinute);
        status.DisplayMessage.ShouldBe("clock differs by 12 minutes"); Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed);
        guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); guard.RequireIndexExchangeAllowed();
    }

    [Fact]
    public void BackwardMonotonicOrOverflowingEstimatedUtcBlocksExchange()
    {
        var clock=new SampleClock(); var guard=new ClockGuard(clock); guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks });
        clock.Advance(TimeSpan.FromSeconds(-1),TimeSpan.Zero); guard.GetStatus().State.ShouldBe(PeerClockState.InvalidElapsed);
        var extreme=new SampleClock { UtcTicks=DateTime.MaxValue.Ticks-1 }; var far=new ClockGuard(extreme);
        far.ObserveHello(new Hello { UtcTicks=DateTime.MaxValue.Ticks }).IndexExchangeAllowed.ShouldBeTrue();
        extreme.Advance(TimeSpan.FromTicks(2),TimeSpan.Zero); far.GetStatus().State.ShouldBe(PeerClockState.InvalidElapsed);
        Should.Throw<PeerClockException>(far.RequireIndexExchangeAllowed);
    }

    [Fact]
    public void DisconnectAndNoHelloAlwaysDenyAdmission()
    {
        var clock=new SampleClock(); var guard=new ClockGuard(clock); guard.GetStatus().State.ShouldBe(PeerClockState.AwaitingHello);
        Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed);
        guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks }); guard.RequireIndexExchangeAllowed(); guard.Reset();
        guard.GetStatus().State.ShouldBe(PeerClockState.AwaitingHello); Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed);
    }

    [Fact]
    public void TransitionDiagnosticsCarryOnlySessionAndNumericClockMetadata()
    {
        var clock=new SampleClock(); using var journal=new DiagnosticJournal(logs,"clock"); Guid session=Guid.NewGuid();
        var guard=new ClockGuard(clock,journal,session); guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks,DeviceName="private device",Nonce=ByteString.CopyFrom(new byte[32]),DeviceId=ByteString.CopyFrom(new byte[32]) });
        for(int i=0;i<100;i++) guard.RequireIndexExchangeAllowed();
        clock.Advance(TimeSpan.Zero,TimeSpan.FromMinutes(11)); guard.GetStatus(); guard.GetStatus();
        string[] entries=File.ReadAllLines(Path.Combine(logs,"debug.jsonl")); entries.Length.ShouldBe(2);
        string log=string.Join('\n',entries); log.ShouldContain("clock.accepted"); log.ShouldContain("clock.blocked");
        log.ShouldContain(session.ToString()); log.ShouldContain("\"clockSkewSeconds\":-660");
        log.ShouldNotContain("private device"); log.ShouldNotContain(Convert.ToHexString(new byte[32]));
    }

    [Fact]
    public void UnavailableDiagnosticsCannotChangeTheClockDecisionOrItsOriginalException()
    {
        var clock=new SampleClock(); using var journal=new DiagnosticJournal(logs,"clock"); journal.Dispose();
        var guard=new ClockGuard(clock,journal); var status=guard.ObserveHello(new Hello { UtcTicks=clock.UtcTicks+Limit+1 });
        status.IndexExchangeAllowed.ShouldBeFalse(); var error=Should.Throw<PeerClockException>(guard.RequireIndexExchangeAllowed);
        error.Status.ShouldBe(status); journal.DroppedEvents.ShouldBe(1);
    }

    [Fact]
    public void StatusPresentationIsBoundedArithmeticForExtremeReportedOffsets()
    {
        new PeerClockStatus(PeerClockState.Skewed,long.MinValue).DisplayMessage.ShouldStartWith("clock differs by ");
        new PeerClockStatus(PeerClockState.Skewed,long.MaxValue).IndexExchangeAllowed.ShouldBeFalse();
        new PeerClockStatus((PeerClockState)999).IndexExchangeAllowed.ShouldBeFalse();
    }

    [Fact]
    public void ElapsedTimeProviderOverflowCannotAuthorizeIndexExchange()
    {
        var clock=new OverflowClock(); var guard=new ClockGuard(clock); guard.ObserveHello(new Hello { UtcTicks=DateTime.UtcNow.Ticks });
        clock.Stamp=long.MaxValue; guard.GetStatus().State.ShouldBe(PeerClockState.InvalidElapsed);
    }

    public void Dispose() { if(Directory.Exists(logs)) Directory.Delete(logs,recursive:true); }
    private sealed class SampleClock : TimeProvider
    {
        private long utcTicks=DateTime.UtcNow.Ticks, stamp;
        public long UtcTicks { get=>Interlocked.Read(ref utcTicks); set=>Interlocked.Exchange(ref utcTicks,value); }
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>Interlocked.Read(ref stamp);
        public override DateTimeOffset GetUtcNow()=>new(UtcTicks,TimeSpan.Zero);
        public void Advance(TimeSpan monotonic,TimeSpan wall) { Interlocked.Add(ref stamp,monotonic.Ticks); Interlocked.Add(ref utcTicks,wall.Ticks); }
    }
    private sealed class OverflowClock : TimeProvider
    {
        public long Stamp { get; set; }
        public override long TimestampFrequency=>1;
        public override long GetTimestamp()=>Stamp;
    }
}
