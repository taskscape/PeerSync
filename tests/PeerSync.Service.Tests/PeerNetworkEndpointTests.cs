using System.Net;
using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class PeerNetworkEndpointTests
{
    [Theory]
    [InlineData("192.168.0.183", true)]
    [InlineData("100.72.94.33", true)]
    [InlineData("203.0.113.8", true)]
    [InlineData("::ffff:203.0.113.8", true)]
    [InlineData("2001:db8::8", true)]
    [InlineData("0.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("239.1.2.3", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::ffff:239.1.2.3", false)]
    [InlineData("ff02::1", false)]
    [InlineData("fe80::1", false)]
    public void OnlyUnicastPeerAddressesAreDialCandidates(string value, bool expected)
    {
        PeerNetworkRuntime.IsDialableUnicast(IPAddress.Parse(value)).ShouldBe(expected);
    }
}
