using Google.Protobuf;
using PeerSync.Protocol.Handshake;
using Shouldly;
using Xunit;

namespace PeerSync.Protocol.Tests;

public sealed class HelloTests
{
    [Fact]
    public void UtcHelloRoundTripsEveryHandshakeFieldWithoutChangingTimestamp()
    {
        var hello=new Hello { ProtocolVersion=1,DeviceId=ByteString.CopyFrom(new byte[32]),DeviceName="test device",ClientVersion="1.0",
            Capabilities=3,Nonce=ByteString.CopyFrom(new byte[32]),UtcTicks=DateTime.MaxValue.Ticks };
        Hello.Parser.ParseFrom(hello.ToByteArray()).ShouldBe(hello);
    }
    [Fact]
    public void MissingTimeStaysZeroForAdmissionRejectionAndInvalidWireValuesAreNotSilentlyClamped()
    {
        Hello.Parser.ParseFrom(Array.Empty<byte>()).UtcTicks.ShouldBe(0);
        var invalid=new Hello { UtcTicks=long.MinValue };
        Hello.Parser.ParseFrom(invalid.ToByteArray()).UtcTicks.ShouldBe(long.MinValue);
    }
}
