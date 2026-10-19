using PeerSync.Core.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class PeerSessionClockStatusTests
{
    [Fact]
    public void StatusIdentityAndPageAreFrozenAndStringRepresentationsArePrivate()
    {
        byte[] device=Enumerable.Repeat((byte)0xAB,32).ToArray(); Guid session=Guid.NewGuid();
        var status=new PeerSessionClockStatus(session,device,new(PeerClockState.Skewed,-660*TimeSpan.TicksPerSecond),false);
        var rows=new List<PeerSessionClockStatus>{status}; var page=new PeerClockStatusPage(rows,false);
        device[0]=1; status.RemoteDeviceId[0]=2; rows.Clear();
        page.Sessions.Single().RemoteDeviceId.ShouldBe(Enumerable.Repeat((byte)0xAB,32).ToArray());
        page.Sessions.Single().SessionId.ShouldBe(session); page.Sessions.Single().IsConnected.ShouldBeFalse();
        status.Clock.DisplayMessage.ShouldBe("clock differs by 11 minutes");
        status.ToString().ShouldNotContain("ABAB"); page.ToString().ShouldNotContain(session.ToString());
        Should.Throw<NotSupportedException>(()=>((IList<PeerSessionClockStatus>)page.Sessions).Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void InvalidIdentitySizeIsRejected(int size)=>Should.Throw<ArgumentException>(()=>new PeerSessionClockStatus(Guid.NewGuid(),new byte[size],new(PeerClockState.AwaitingHello),true));

    [Fact]
    public void InvalidSessionStateAndPageRowsAreRejected()
    {
        var good=new PeerSessionClockStatus(Guid.NewGuid(),new byte[32],new(PeerClockState.AwaitingHello),true);
        Should.Throw<ArgumentException>(()=>new PeerSessionClockStatus(Guid.Empty,new byte[32],good.Clock,true));
        Should.Throw<ArgumentException>(()=>new PeerSessionClockStatus(Guid.NewGuid(),new byte[32],new((PeerClockState)99),true));
        Should.Throw<ArgumentNullException>(()=>new PeerSessionClockStatus(Guid.NewGuid(),null!,good.Clock,true));
        Should.Throw<ArgumentNullException>(()=>new PeerSessionClockStatus(Guid.NewGuid(),new byte[32],null!,true));
        Should.Throw<ArgumentException>(()=>new PeerClockStatusPage([good,good],false));
        Should.Throw<ArgumentException>(()=>new PeerClockStatusPage([null!],false));
        Should.Throw<ArgumentException>(()=>new PeerClockStatusPage(Enumerable.Repeat(good,1001).ToArray(),false));
        new PeerClockStatusPage([],true).Truncated.ShouldBeTrue();
    }
}
