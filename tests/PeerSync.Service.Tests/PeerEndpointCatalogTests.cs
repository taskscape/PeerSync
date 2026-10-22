using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Connections;
using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class PeerEndpointCatalogTests
{
    [Fact]
    public void RefreshDeduplicatesAndKeepsTheHighestPrioritySource()
    {
        var clock = new AdjustableTimeProvider();
        var catalog = new PeerEndpointCatalog(clock);
        var address = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 47821);

        catalog.Remember("peer", address, ConnectionSource.Public);
        clock.Advance(TimeSpan.FromMinutes(1));
        catalog.Remember("peer", address, ConnectionSource.Lan);
        clock.Advance(TimeSpan.FromMinutes(1));
        catalog.Remember("peer", address, ConnectionSource.Public);

        var endpoint = catalog.Snapshot("peer").ShouldHaveSingleItem();
        endpoint.Source.ShouldBe(ConnectionSource.Lan);
        endpoint.LastSeenUtc.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public void ExpiredAddressesAreRemovedAndNoLongerSchedulePeers()
    {
        var clock = new AdjustableTimeProvider();
        var catalog = new PeerEndpointCatalog(clock, TimeSpan.FromMinutes(5));
        catalog.Remember("peer", new(IPAddress.Parse("192.168.1.10"), 47821), ConnectionSource.Lan);
        catalog.KnownPeerKeys().ShouldContain("peer");

        clock.Advance(TimeSpan.FromMinutes(5));

        catalog.Snapshot("peer").ShouldBeEmpty();
        catalog.KnownPeerKeys().ShouldBeEmpty();
    }

    [Fact]
    public void EndpointSetIsBoundedAndRetainsTheMostRecentlyObservedRoute()
    {
        var clock = new AdjustableTimeProvider();
        var catalog = new PeerEndpointCatalog(clock, maximumEndpointsPerPeer: 4);
        for (int octet = 1; octet <= 8; octet++)
        {
            catalog.Remember("peer", new(IPAddress.Parse($"10.0.0.{octet}"), 47821), ConnectionSource.Lan);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var retained = catalog.Snapshot("peer");
        retained.Count.ShouldBe(4);
        retained.Select(endpoint => endpoint.Address.Address.ToString())
            .ShouldBe(["10.0.0.8", "10.0.0.7", "10.0.0.6", "10.0.0.5"]);
        catalog.Contains("peer", new(IPAddress.Parse("10.0.0.8"), 47821)).ShouldBeTrue();
        catalog.Contains("peer", new(IPAddress.Parse("10.0.0.1"), 47821)).ShouldBeFalse();
    }

    [Fact]
    public void ExpirationAndCapacityDiagnosticsContainCountsWithoutPeerOrAddress()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-EndpointCatalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string log = Path.Combine(root, "debug.jsonl");
            var clock = new AdjustableTimeProvider();
            using var journal = new DiagnosticJournal(root, "endpoint_catalog", debugEnabled: true);
            var catalog = new PeerEndpointCatalog(clock, TimeSpan.FromMinutes(1), 1, journal);
            catalog.Remember("private-peer-id", new(IPAddress.Parse("192.168.1.10"), 47821), ConnectionSource.Lan);
            clock.Advance(TimeSpan.FromSeconds(1));
            catalog.Remember("private-peer-id", new(IPAddress.Parse("192.168.1.11"), 47821), ConnectionSource.Lan);
            clock.Advance(TimeSpan.FromMinutes(1));
            catalog.Snapshot("private-peer-id").ShouldBeEmpty();

            string text = File.ReadAllText(log);
            text.ShouldContain("service.peer_endpoint_limit_reached");
            text.ShouldContain("service.peer_endpoint_expired");
            text.ShouldNotContain("private-peer-id");
            text.ShouldNotContain("192.168.1.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
