using Google.Protobuf;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using Shouldly;
using Xunit;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Control.Contracts.Tests;

public sealed class ControlStatusWireTests
{
    [Theory]
    [InlineData(PeerClockState.AwaitingHello,null)]
    [InlineData(PeerClockState.Accepted,0L)]
    [InlineData(PeerClockState.Accepted,6000000000L)]
    [InlineData(PeerClockState.Accepted,-6000000000L)]
    [InlineData(PeerClockState.Skewed,6600000000L)]
    [InlineData(PeerClockState.Skewed,-6600000000L)]
    [InlineData(PeerClockState.InvalidTimestamp,null)]
    [InlineData(PeerClockState.InvalidElapsed,null)]
    public void StateAndOptionalSignedOffsetSurviveActualProtobufRoundTrip(PeerClockState state,long? offset)
    {
        var source=Snapshot(state,offset); var encoded=ControlStatusWire.Encode(source);
        encoded.Clocks.Single().HasOffsetTicks.ShouldBe(offset.HasValue);
        var decoded=ControlStatusWire.Decode(Wire.ServiceStatus.Parser.ParseFrom(encoded.ToByteArray()));
        decoded.InstanceId.ShouldBe(source.InstanceId); decoded.Sequence.ShouldBe(7UL); decoded.ProductionSyncAvailable.ShouldBeFalse(); decoded.ClockStatusTruncated.ShouldBeTrue();
        var actual=decoded.Clocks.Single(); var expected=source.Clocks.Single();
        actual.FolderId.ShouldBe(expected.FolderId); actual.Session.RemoteDeviceId.ShouldBe(expected.Session.RemoteDeviceId);
        actual.Session.SessionId.ShouldBe(expected.Session.SessionId); actual.Session.IsConnected.ShouldBeFalse();
        actual.Session.Clock.State.ShouldBe(state); actual.Session.Clock.OffsetTicks.ShouldBe(offset);
    }

    [Fact]
    public void MutableRowsAndGeneratedMessagesCannotChangeDeliveredStatus()
    {
        byte[] folder=Enumerable.Repeat((byte)0xAD,32).ToArray(); var session=new PeerSessionClockStatus(Guid.NewGuid(),new byte[32],new(PeerClockState.Accepted,0),true);
        var row=new FolderPeerClockStatus(folder,session); var rows=new List<FolderPeerClockStatus>{row};
        var snapshot=new ControlStatusSnapshot(Guid.NewGuid(),1,false,rows,false); rows.Clear(); folder[0]=0; row.FolderId[0]=0;
        snapshot.Clocks.Single().FolderId[0].ShouldBe((byte)0xAD);
        var message=ControlStatusWire.Encode(snapshot); var decoded=ControlStatusWire.Decode(message);
        message.Clocks[0].OffsetTicks=999; message.Clocks[0].FolderId=ByteString.Empty; message.Clocks.Clear();
        decoded.Clocks.Single().Session.Clock.OffsetTicks.ShouldBe(0); decoded.Clocks.Single().FolderId[0].ShouldBe((byte)0xAD);
        Should.Throw<NotSupportedException>(()=>((IList<FolderPeerClockStatus>)decoded.Clocks).Clear());
        snapshot.ToString().ShouldNotContain(snapshot.InstanceId.ToString("N")); row.ToString().ShouldNotContain("ADAD");
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("empty-instance")]
    [InlineData("sequence")]
    [InlineData("folder")]
    [InlineData("device")]
    [InlineData("session")]
    [InlineData("state")]
    [InlineData("duplicate")]
    public void InvalidEnvelopeAndIdentityDoNotReachTheUi(string failure)
    {
        var message=ControlStatusWire.Encode(Snapshot(PeerClockState.Accepted,0));
        switch(failure)
        {
            case "instance": message.InstanceId="not-a-guid"; break;
            case "empty-instance": message.InstanceId=Guid.Empty.ToString("N"); break;
            case "sequence": message.Sequence=0; break;
            case "folder": message.Clocks[0].FolderId=ByteString.CopyFrom(new byte[31]); break;
            case "device": message.Clocks[0].DeviceId=ByteString.CopyFrom(new byte[33]); break;
            case "session": message.Clocks[0].SessionId=Guid.Empty.ToString("N"); break;
            case "state": message.Clocks[0].State=(Wire.ClockState)99; break;
            case "duplicate": message.Clocks.Add(message.Clocks[0].Clone()); break;
        }
        Should.Throw<InvalidDataException>(()=>ControlStatusWire.Decode(message));
    }

    [Theory]
    [InlineData(PeerClockState.Accepted,null)]
    [InlineData(PeerClockState.Accepted,6000000001L)]
    [InlineData(PeerClockState.Skewed,0L)]
    [InlineData(PeerClockState.Skewed,6000000000L)]
    [InlineData(PeerClockState.Skewed,null)]
    [InlineData(PeerClockState.InvalidTimestamp,0L)]
    [InlineData(PeerClockState.AwaitingHello,0L)]
    [InlineData(PeerClockState.Skewed,long.MinValue)]
    [InlineData(PeerClockState.Skewed,long.MaxValue)]
    public void ContradictoryOrUnrepresentableClockSampleIsRejectedInBothDirections(PeerClockState state,long? offset)
    {
        Should.Throw<InvalidDataException>(()=>ControlStatusWire.Encode(Snapshot(state,offset)));
        var message=ControlStatusWire.Encode(Snapshot(PeerClockState.Accepted,0));
        message.Clocks[0].State=(Wire.ClockState)((int)state+1);
        if(offset is { } value) message.Clocks[0].OffsetTicks=value; else message.Clocks[0].ClearOffsetTicks();
        Should.Throw<InvalidDataException>(()=>ControlStatusWire.Decode(message));
    }

    [Fact]
    public void RowAndEncodedByteLimitsIncludeUnknownFields()
    {
        var message=ControlStatusWire.Encode(Snapshot(PeerClockState.Accepted,0));
        var row=message.Clocks[0];
        for(int i=0;i<1000;i++) { var copy=row.Clone(); copy.SessionId=Guid.NewGuid().ToString("N"); message.Clocks.Add(copy); }
        Should.Throw<InvalidDataException>(()=>ControlStatusWire.Decode(message));
        var ordinary=ControlStatusWire.Encode(Snapshot(PeerClockState.Accepted,0));
        using var bytes=new MemoryStream(); using(var writer=new CodedOutputStream(bytes,leaveOpen:true))
        {
            ordinary.WriteTo(writer); writer.WriteTag(100,WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(new byte[ControlStatusSnapshot.MaximumMessageBytes])); writer.Flush();
        }
        var oversized=Wire.ServiceStatus.Parser.ParseFrom(bytes.ToArray());
        Should.Throw<InvalidDataException>(()=>ControlStatusWire.Decode(oversized));
    }

    [Fact]
    public void EmptySnapshotIsExplicitlyUnavailableAndTypedInputBoundsAreChecked()
    {
        var empty=new ControlStatusSnapshot(Guid.NewGuid(),1,false,[],false);
        ControlStatusWire.Decode(ControlStatusWire.Encode(empty)).Clocks.ShouldBeEmpty();
        Should.Throw<ArgumentException>(()=>new ControlStatusSnapshot(Guid.Empty,1,false,[],false));
        Should.Throw<ArgumentException>(()=>new ControlStatusSnapshot(Guid.NewGuid(),0,false,[],false));
        var row=Snapshot(PeerClockState.Accepted,0).Clocks.Single();
        Should.Throw<ArgumentException>(()=>new ControlStatusSnapshot(Guid.NewGuid(),1,false,[row,row],false));
        Should.Throw<ArgumentException>(()=>new FolderPeerClockStatus(new byte[31],row.Session));
    }

    private static ControlStatusSnapshot Snapshot(PeerClockState state,long? offset)=>new(Guid.NewGuid(),7,false,
        [new(new byte[32],new(Guid.NewGuid(),Enumerable.Repeat((byte)2,32).ToArray(),new(state,offset),false))],true);
}
