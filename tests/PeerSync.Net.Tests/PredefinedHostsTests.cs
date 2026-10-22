using System.Net;
using PeerSync.Net.Discovery;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class PredefinedHostsTests
{
    [Fact]
    public async Task ResolvesAndDialsDistinctAddressesForConfiguredHostPorts()
    {
        var configured = new[] { "peer.example:47821", "PEER.EXAMPLE:47821", "[::1]:47822" };
        var resolvedNames = new List<string>();
        var dialed = new List<IPEndPoint>();
        var discovery = new PredefinedHosts(() => configured,
            (endpoint, _) => { dialed.Add(endpoint); return ValueTask.CompletedTask; },
            resolveAsync: (host, _) =>
            {
                resolvedNames.Add(host);
                IPAddress[] result = host == "peer.example"
                    ? [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.10")]
                    : [IPAddress.IPv6Loopback];
                return Task.FromResult(result);
            });

        await discovery.ResolveAndDialOnceAsync(CancellationToken.None);

        resolvedNames.ShouldBe(["peer.example", "::1"]);
        dialed.Select(endpoint => endpoint.ToString()).ShouldBe([
            "192.0.2.10:47821", "192.0.2.11:47821", "[::1]:47822"]);
    }

    [Fact]
    public async Task InvalidHostEntriesAreSkippedWithoutResolutionOrDial()
    {
        bool resolved = false;
        var discovery = new PredefinedHosts(() => ["user@peer.example:47821", "peer.example:0", "peer.example:47821/path"],
            (_, _) => throw new InvalidOperationException("invalid endpoints must not dial"),
            resolveAsync: (_, _) => { resolved = true; return Task.FromResult(Array.Empty<IPAddress>()); });

        await discovery.ResolveAndDialOnceAsync(CancellationToken.None);

        resolved.ShouldBeFalse();
    }

    [Fact]
    public void RefreshIntervalIsSixtySeconds()
        => PredefinedHosts.RefreshInterval.ShouldBe(TimeSpan.FromSeconds(60));
}
