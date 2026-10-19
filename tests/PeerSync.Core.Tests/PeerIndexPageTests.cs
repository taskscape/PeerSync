using PeerSync.Core.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class PeerIndexPageTests
{
    [Fact]
    public void PageFreezesCallerListAndPreservesExactRecordBytes()
    {
        var original=Record(1); SignedRecord[] caller=[original]; var page=new PeerIndexPage(0,1,caller); caller[0]=Record(2);
        page.Records.Single().ShouldBeSameAs(original); page.Records.Single().Body.ToArray().ShouldBe([7]);
        page.ToString().ShouldBe("Peer index page (redacted)");
    }
    [Fact]
    public void GapsCanAdvanceAnEmptyPageButRecordsMustBeStrictlyOrderedInsideItsCursorRange()
    {
        new PeerIndexPage(5,9,[]).LastSequence.ShouldBe(9);
        Should.Throw<ArgumentException>(()=>new PeerIndexPage(1,2,[Record(1)]));
        Should.Throw<ArgumentException>(()=>new PeerIndexPage(0,2,[Record(2),Record(1)]));
        Should.Throw<ArgumentException>(()=>new PeerIndexPage(0,2,[Record(1),Record(1)]));
        Should.Throw<ArgumentException>(()=>new PeerIndexPage(0,long.MaxValue,[Record(ulong.MaxValue)]));
    }
    [Fact]
    public void InvalidCursorCountAndNullEntriesAreRejectedBeforeHandoff()
    {
        Should.Throw<ArgumentOutOfRangeException>(()=>new PeerIndexPage(-1,0,[]));
        Should.Throw<ArgumentOutOfRangeException>(()=>new PeerIndexPage(2,1,[]));
        Should.Throw<ArgumentOutOfRangeException>(()=>new PeerIndexPage(0,1001,Enumerable.Range(1,1001).Select(i=>Record((ulong)i)).ToArray()));
        Should.Throw<ArgumentNullException>(()=>new PeerIndexPage(0,1,[null!]));
    }
    private static SignedRecord Record(ulong sequence)=>new([7],new byte[64],sequence);
}
