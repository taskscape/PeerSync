using System.Net;
using Mono.Nat;
using PeerSync.Net.Nat;
using Shouldly;
using Xunit;
using NatTransport = Mono.Nat.Protocol;

namespace PeerSync.Net.Tests;

public sealed class PortMapperTests
{
    [Fact]
    public async Task MappingLeasesExpireAndStopRemovesBothProtocols()
    {
        var clock = new MutableClock();
        var gateway = new Gateway { LeaseSeconds = 60 };
        var mapper = new PortMapper(47821, timeProvider: clock);

        await mapper.ObserveDeviceAsync(gateway);
        mapper.PublicEndpoints.Single().ShouldBe(new IPEndPoint(gateway.External, 47821));
        gateway.UdpCreates.ShouldBe(1);
        gateway.TcpCreates.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(61));
        mapper.PublicEndpoints.ShouldBeEmpty();
        await mapper.DisposeAsync();
        gateway.Deleted.Count.ShouldBe(2);
        gateway.Deleted.Select(mapping => mapping.Protocol).ShouldContain(NatTransport.Udp);
        gateway.Deleted.Select(mapping => mapping.Protocol).ShouldContain(NatTransport.Tcp);
    }

    [Fact]
    public async Task FailedExternalLookupWithdrawsStaleEndpointUntilRecovery()
    {
        var clock = new MutableClock();
        var gateway = new Gateway();
        await using var mapper = new PortMapper(47821, timeProvider: clock);
        await mapper.ObserveDeviceAsync(gateway);
        mapper.PublicEndpoints.Single().Address.ShouldBe(gateway.External);

        gateway.FailExternal = true;
        clock.Advance(TimeSpan.FromMinutes(30));
        await mapper.ObserveDeviceAsync(gateway);
        mapper.PublicEndpoints.ShouldBeEmpty();

        gateway.FailExternal = false;
        gateway.External = IPAddress.Parse("203.0.113.8");
        clock.Advance(TimeSpan.FromSeconds(30));
        await mapper.ObserveDeviceAsync(gateway);
        mapper.PublicEndpoints.Single().Address.ShouldBe(gateway.External);
    }

    [Fact]
    public async Task ShortGatewayLeaseRenewsBeforeExpiryWithoutPollingGatewayEarly()
    {
        var clock = new MutableClock();
        var gateway = new Gateway { LeaseSeconds = 60 };
        await using var mapper = new PortMapper(47821, timeProvider: clock);
        await mapper.ObserveDeviceAsync(gateway);

        clock.Advance(TimeSpan.FromSeconds(29));
        await mapper.RenewDueAsync(TestContext.Current.CancellationToken);
        gateway.UdpCreates.ShouldBe(1);
        gateway.TcpCreates.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await mapper.RenewDueAsync(TestContext.Current.CancellationToken);
        gateway.UdpCreates.ShouldBe(2);
        gateway.TcpCreates.ShouldBe(2);
        clock.Advance(TimeSpan.FromSeconds(31));
        mapper.PublicEndpoints.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task FailedRenewalWithdrawsExpiredProtocolWithoutTouchingHealthyMapping()
    {
        var clock = new MutableClock();
        var gateway = new Gateway { UdpLeaseSeconds = 60, UdpPublicPort = 47822 };
        await using var mapper = new PortMapper(47821, timeProvider: clock);
        await mapper.ObserveDeviceAsync(gateway);
        mapper.PublicEndpoints.Count.ShouldBe(2);
        int announcements = 0;
        mapper.MappingsChanged += () => announcements++;

        gateway.FailUdp = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        await mapper.RenewDueAsync(TestContext.Current.CancellationToken);
        announcements.ShouldBe(0);
        gateway.TcpCreates.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        await mapper.RenewDueAsync(TestContext.Current.CancellationToken);
        announcements.ShouldBe(1);
        mapper.PublicEndpoints.Single().Port.ShouldBe(47821);
        gateway.TcpCreates.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        await mapper.RenewDueAsync(TestContext.Current.CancellationToken);
        announcements.ShouldBe(1);
    }

    [Fact]
    public async Task RepeatedDiscoverySharesTheActiveAttemptAndOneProtocolCanFail()
    {
        var clock = new MutableClock();
        var gateway = new Gateway { FailUdp = true };
        var pending = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.PendingExternal = pending;
        await using var mapper = new PortMapper(47821, timeProvider: clock);

        Task first = mapper.ObserveDeviceAsync(gateway);
        Task duplicate = mapper.ObserveDeviceAsync(gateway);
        ReferenceEquals(first, duplicate).ShouldBeTrue();
        pending.SetResult(gateway.External);
        await first;
        gateway.UdpCreates.ShouldBe(1);
        gateway.TcpCreates.ShouldBe(1);
        mapper.PublicEndpoints.Single().Port.ShouldBe(47821);

        gateway.PendingExternal = null;
        gateway.FailUdp = false;
        await mapper.ObserveDeviceAsync(gateway);
        gateway.UdpCreates.ShouldBe(1);
        gateway.TcpCreates.ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await mapper.ObserveDeviceAsync(gateway);
        gateway.UdpCreates.ShouldBe(2);
        gateway.TcpCreates.ShouldBe(1);
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Gateway : INatDevice
    {
        internal IPAddress External { get; set; } = IPAddress.Parse("203.0.113.7");
        internal TaskCompletionSource<IPAddress>? PendingExternal { get; set; }
        internal bool FailExternal { get; set; }
        internal bool FailUdp { get; set; }
        internal int LeaseSeconds { get; set; } = 3600;
        internal int? UdpLeaseSeconds { get; set; }
        internal int? UdpPublicPort { get; set; }
        internal int UdpCreates { get; private set; }
        internal int TcpCreates { get; private set; }
        internal List<Mapping> Deleted { get; } = [];
        public IPEndPoint DeviceEndpoint { get; } = new(IPAddress.Parse("192.0.2.1"), 1900);
        public DateTime LastSeen => DateTime.UtcNow;
        public NatProtocol NatProtocol => NatProtocol.Upnp;

        public Task<IPAddress> GetExternalIPAsync() => FailExternal
            ? Task.FromException<IPAddress>(new IOException("gateway unavailable"))
            : PendingExternal?.Task ?? Task.FromResult(External);

        public Task<Mapping> CreatePortMapAsync(Mapping mapping)
        {
            if (mapping.Protocol == NatTransport.Udp) UdpCreates++; else TcpCreates++;
            if (mapping.Protocol == NatTransport.Udp && FailUdp)
                return Task.FromException<Mapping>(new IOException("UDP mapping refused"));
            return Task.FromResult(new Mapping(mapping.Protocol, mapping.PrivatePort,
                mapping.Protocol == NatTransport.Udp ? UdpPublicPort ?? mapping.PublicPort : mapping.PublicPort,
                mapping.Protocol == NatTransport.Udp ? UdpLeaseSeconds ?? LeaseSeconds : LeaseSeconds,
                mapping.Description));
        }

        public Task<Mapping> DeletePortMapAsync(Mapping mapping)
        { Deleted.Add(mapping); return Task.FromResult(mapping); }
        public Task<Mapping[]> GetAllMappingsAsync() => Task.FromResult(Array.Empty<Mapping>());
        public Task<Mapping> GetSpecificMappingAsync(NatTransport protocol, int publicPort) =>
            Task.FromResult(new Mapping(protocol, publicPort, publicPort));
    }
}
