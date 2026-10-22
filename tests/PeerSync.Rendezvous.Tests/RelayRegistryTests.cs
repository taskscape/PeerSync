using System.Net;
using Google.Protobuf;
using Grpc.Core;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;
using PeerSync.Rendezvous.Relay;
using PeerSync.Rendezvous.Tracker;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class RelayRegistryTests
{
    [Fact]
    public async Task OneTimeOfferBindsBothCertificateIdentitiesAndCancelsBothLegsOnLeave()
    {
        using var registry = new RelayRegistry(new RendezvousOptions());
        PresenceSession first = Session(1), second = Session(2);
        registry.TryOffer(first, second).ShouldBeTrue();
        RelayOffer firstOffer = Offer(first), secondOffer = Offer(second);
        firstOffer.Token.ShouldBe(secondOffer.Token);
        firstOffer.Initiator.ShouldBeTrue();
        secondOffer.Initiator.ShouldBeFalse();
        firstOffer.PeerDeviceId.Span.SequenceEqual(second.DeviceId).ShouldBeTrue();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstWriter = new TestWriter();
        var secondWriter = new TestWriter();
        await Should.ThrowAsync<RpcException>(async () =>
            await registry.JoinAsync(firstOffer.Token.Memory, Id(3), new TestWriter(), timeout.Token));
        Task<RelayRegistry.RelayJoin> firstJoin = registry.JoinAsync(firstOffer.Token.Memory,
            first.DeviceId, firstWriter, timeout.Token).AsTask();
        await Should.ThrowAsync<RpcException>(async () =>
            await registry.JoinAsync(firstOffer.Token.Memory, first.DeviceId, new TestWriter(), timeout.Token));
        RelayRegistry.RelayJoin secondJoin = await registry.JoinAsync(firstOffer.Token.Memory,
            second.DeviceId, secondWriter, timeout.Token);
        RelayRegistry.RelayJoin connectedFirst = await firstJoin;
        connectedFirst.PartnerWriter.ShouldBe(secondWriter);
        secondJoin.PartnerWriter.ShouldBe(firstWriter);
        await registry.ThrottleAsync(connectedFirst, 1024, timeout.Token);
        registry.Leave(connectedFirst);
        secondJoin.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        await Should.ThrowAsync<RpcException>(async () =>
            await registry.JoinAsync(firstOffer.Token.Memory, second.DeviceId, new TestWriter(), timeout.Token));
    }

    [Fact]
    public void PendingOffersRespectPerDeviceCapAndExpireAfterThirtySeconds()
    {
        var clock = new ManualClock();
        using var registry = new RelayRegistry(new RendezvousOptions
        { MaximumRelaySessionsPerDevice = 1 }, clock);
        PresenceSession first = Session(1), second = Session(2), third = Session(3);
        registry.TryOffer(first, second).ShouldBeTrue();
        registry.TryOffer(first, third).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(31));
        registry.Expire().ShouldBe(1);
        registry.TryOffer(first, third).ShouldBeTrue();
    }

    private static PresenceSession Session(byte marker)
        => new(Id(marker), new IPEndPoint(IPAddress.Parse("203.0.113.1"), 52000));

    private static byte[] Id(byte marker)
    {
        byte[] id = new byte[32];
        id[0] = marker;
        return id;
    }

    private static RelayOffer Offer(PresenceSession session)
    {
        session.Outgoing.Reader.TryRead(out ServerMessage? message).ShouldBeTrue();
        return message!.RelayOffer ?? throw new InvalidOperationException("Relay offer missing.");
    }

    private sealed class TestWriter : IServerStreamWriter<RelayChunk>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(RelayChunk message) => Task.CompletedTask;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        internal void Advance(TimeSpan amount) => utcNow += amount;
    }
}
