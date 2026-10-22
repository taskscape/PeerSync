using System.Net;
using Google.Protobuf;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Tracker;
using Shouldly;
using Xunit;
using Endpoint = PeerSync.Rendezvous.Contracts.V1.Endpoint;

namespace PeerSync.Rendezvous.Tests;

public sealed class PresenceRegistryTests
{
    [Fact]
    public void PeerListsContainOnlyDevicesWithTheSameFolder()
    {
        var clock = new ManualClock();
        var registry = new PresenceRegistry(clock);
        byte[] common = Id(10), privateFolder = Id(11);
        PresenceSession first = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession second = registry.Open(Id(2), Remote("203.0.113.2"));
        PresenceSession unrelated = registry.Open(Id(3), Remote("203.0.113.3"));

        registry.Announce(first, Announcement(common, 41001));
        registry.Announce(second, Announcement(common, 41002));
        registry.Announce(unrelated, Announcement(privateFolder, 41003));

        PeerList firstList = LastList(first);
        firstList.FolderId.Span.SequenceEqual(common).ShouldBeTrue();
        firstList.Peers.Count.ShouldBe(1);
        firstList.Peers[0].DeviceId.Span.SequenceEqual(Id(2)).ShouldBeTrue();
        firstList.Peers[0].Endpoints.Single().Address.ShouldBe("203.0.113.2");
        firstList.Peers[0].Endpoints.Single().Port.ShouldBe(41002u);
        PeerList secondList = LastList(second);
        secondList.Peers.Count.ShouldBe(1);
        secondList.Peers[0].DeviceId.Span.SequenceEqual(Id(1)).ShouldBeTrue();
        LastList(unrelated).Peers.ShouldBeEmpty();
        registry.TryGetShared(first, unrelated.DeviceId, common, out _).ShouldBeFalse();
    }

    [Fact]
    public void ExpiryRemovesOnlyStalePresenceAndPushesAnEmptyPeerList()
    {
        var clock = new ManualClock();
        var registry = new PresenceRegistry(clock);
        byte[] folder = Id(10);
        PresenceSession first = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession second = registry.Open(Id(2), Remote("203.0.113.2"));
        registry.Announce(first, Announcement(folder, 41001));
        registry.Announce(second, Announcement(folder, 41002));
        clock.Advance(TimeSpan.FromMinutes(10));
        registry.Announce(first, Announcement(folder, 41001));
        clock.Advance(TimeSpan.FromMinutes(6));

        registry.Expire().ShouldBe(1);
        LastList(first).Peers.ShouldBeEmpty();
        registry.TryGetShared(first, second.DeviceId, folder, out _).ShouldBeFalse();
    }

    [Fact]
    public void DeviceRequestLimitSurvivesSessionReplacement()
    {
        var clock = new ManualClock();
        var registry = new PresenceRegistry(clock);
        PresenceSession first = registry.Open(Id(1), Remote("203.0.113.1"));
        for (int i = 0; i < 60; i++) registry.TryTakeRequest(first).ShouldBeTrue();
        registry.TryTakeRequest(first).ShouldBeFalse();
        PresenceSession replacement = registry.Open(Id(1), Remote("203.0.113.1"));
        registry.TryTakeRequest(first).ShouldBeFalse();
        registry.TryTakeRequest(replacement).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(1));
        registry.TryTakeRequest(replacement).ShouldBeTrue();
    }

    [Fact]
    public void AnnouncementsRejectDuplicateFoldersAndInvalidEndpoints()
    {
        var registry = new PresenceRegistry();
        PresenceSession session = registry.Open(Id(1), Remote("203.0.113.1"));
        Announce duplicate = Announcement(Id(10), 41001);
        duplicate.FolderIds.Add(ByteString.CopyFrom(Id(10)));
        Should.Throw<ArgumentException>(() => registry.Announce(session, duplicate));
        Announce invalidEndpoint = Announcement(Id(10), 41001);
        invalidEndpoint.LocalEndpoints.Add(new Endpoint { Address = "0.0.0.0", Port = 41001 });
        Should.Throw<ArgumentException>(() => registry.Announce(session, invalidEndpoint));
        Announce tooMany = Announcement(Id(10), 41001);
        for (int i = 0; i < 500; i++) tooMany.FolderIds.Add(ByteString.CopyFrom(Id((byte)(i + 20))));
        Should.Throw<ArgumentException>(() => registry.Announce(session, tooMany));
    }

    [Fact]
    public void PunchIntroductionIsRestrictedToSharedFolderAndStartsHalfSecondAhead()
    {
        var clock = new ManualClock();
        var registry = new PresenceRegistry(clock);
        byte[] folder = Id(10);
        PresenceSession first = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession second = registry.Open(Id(2), Remote("203.0.113.2"));
        registry.Announce(first, Announcement(folder, 41001));
        registry.Announce(second, Announcement(folder, 41002));
        Drain(first); Drain(second);

        registry.RequestPunch(first, second.DeviceId, Id(11)).ShouldBeFalse();
        registry.RequestPunch(first, second.DeviceId, folder).ShouldBeTrue();
        ServerMessage toFirst = Read(first);
        ServerMessage toSecond = Read(second);
        toFirst.PunchNow.StartUtcTicks.ShouldBe(clock.GetUtcNow().AddMilliseconds(500).UtcTicks);
        toFirst.PunchNow.DeviceId.Span.SequenceEqual(second.DeviceId).ShouldBeTrue();
        toSecond.PunchNow.DeviceId.Span.SequenceEqual(first.DeviceId).ShouldBeTrue();
    }

    private static byte[] Id(byte marker)
    {
        byte[] id = new byte[32];
        id[0] = marker;
        return id;
    }

    private static IPEndPoint Remote(string address) => new(IPAddress.Parse(address), 52000);

    private static Announce Announcement(byte[] folder, uint port)
    {
        var announce = new Announce { ListenPort = port };
        announce.FolderIds.Add(ByteString.CopyFrom(folder));
        return announce;
    }

    private static ServerMessage Read(PresenceSession session)
    {
        session.Outgoing.Reader.TryRead(out ServerMessage? message).ShouldBeTrue();
        return message!;
    }

    private static void Drain(PresenceSession session)
    {
        while (session.Outgoing.Reader.TryRead(out _)) { }
    }

    private static PeerList LastList(PresenceSession session)
    {
        PeerList? latest = null;
        while (session.Outgoing.Reader.TryRead(out ServerMessage? message))
            if (message.PeerList is { } list) latest = list;
        return latest ?? throw new InvalidOperationException("No peer list was pushed.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        internal void Advance(TimeSpan amount) => utcNow += amount;
    }
}
