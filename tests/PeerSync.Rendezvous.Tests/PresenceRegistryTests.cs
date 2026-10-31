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

        PeerList firstList = LastList(registry, first);
        firstList.FolderId.Span.SequenceEqual(common).ShouldBeTrue();
        firstList.Peers.Count.ShouldBe(1);
        firstList.Peers[0].DeviceId.Span.SequenceEqual(Id(2)).ShouldBeTrue();
        firstList.Peers[0].Endpoints.Single().Address.ShouldBe("203.0.113.2");
        firstList.Peers[0].Endpoints.Single().Port.ShouldBe(41002u);
        PeerList secondList = LastList(registry, second);
        secondList.Peers.Count.ShouldBe(1);
        secondList.Peers[0].DeviceId.Span.SequenceEqual(Id(1)).ShouldBeTrue();
        LastList(registry, unrelated).Peers.ShouldBeEmpty();
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
        LastList(registry, first).Peers.ShouldBeEmpty();
        registry.TryGetShared(first, second.DeviceId, folder, out _).ShouldBeFalse();
    }

    [Fact]
    public void UnannouncedAndExpiredSessionsCancelTheirStreams()
    {
        var clock = new ManualClock();
        var registry = new PresenceRegistry(clock);
        PresenceSession idle = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession active = registry.Open(Id(2), Remote("203.0.113.2"));
        registry.Announce(active, Announcement(Id(10), 41002));
        clock.Advance(TimeSpan.FromSeconds(31));
        registry.Expire().ShouldBe(1);
        idle.Lifetime.IsCancellationRequested.ShouldBeTrue();
        active.Lifetime.IsCancellationRequested.ShouldBeFalse();
        PresenceSession replacement = registry.Open(Id(2), Remote("203.0.113.2"));
        active.Lifetime.IsCancellationRequested.ShouldBeTrue();
        registry.Close(replacement);
        replacement.Lifetime.IsCancellationRequested.ShouldBeTrue();
        PresenceSession shutdown = registry.Open(Id(3), Remote("203.0.113.3"));
        registry.CloseAll();
        shutdown.Lifetime.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void FolderLimitKeepsPeerListsWithinGrpcMessageBudget()
    {
        var registry = new PresenceRegistry();
        byte[] folder = Id(10);
        PresenceSession? last = null;
        for (byte marker = 1; marker <= PresenceRegistry.MaximumPeersPerFolder; marker++)
        {
            PresenceSession session = registry.Open(Id(marker), Remote("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"));
            Announce announce = Announcement(folder, 41001);
            for (int endpoint = 0; endpoint < 8; endpoint++)
                announce.LocalEndpoints.Add(new Endpoint
                { Address = "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", Port = (uint)(41002 + endpoint) });
            registry.Announce(session, announce);
            last = session;
        }
        PeerList list = LastList(registry, last!);
        list.Peers.Count.ShouldBe(PresenceRegistry.MaximumPeersPerFolder - 1);
        new ServerMessage { PeerList = list }.CalculateSize().ShouldBeLessThan(256 * 1024);
        PresenceSession overflow = registry.Open(Id(65), Remote("203.0.113.65"));
        Should.Throw<ArgumentException>(() => registry.Announce(overflow, Announcement(folder, 41065)));
    }

    [Fact]
    public void RepeatedAnnouncementsQueueOnlyTheLatestListPerFolder()
    {
        var registry = new PresenceRegistry();
        PresenceSession reader = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession sender = registry.Open(Id(2), Remote("203.0.113.2"));
        Announce allFolders = Announcement(FolderId(0), 41001);
        for (int index = 1; index < 500; index++)
            allFolders.FolderIds.Add(ByteString.CopyFrom(FolderId(index)));
        registry.Announce(reader, allFolders);
        Drain(registry, reader);

        for (uint port = 41002; port <= 41012; port++)
        {
            Announce repeated = allFolders.Clone();
            repeated.ListenPort = port;
            registry.Announce(sender, repeated);
        }

        reader.Outgoing.Reader.Count.ShouldBe(500);
        reader.PendingPeerLists.Count.ShouldBe(500);
        reader.Outgoing.Reader.TryPeek(out ServerMessage? queued).ShouldBeTrue();
        queued!.PeerList.Peers.ShouldBeEmpty();
        var receivedFolders = new HashSet<string>(StringComparer.Ordinal);
        while (registry.TryReadOutgoing(reader, out ServerMessage? message))
        {
            PeerList list = message!.PeerList;
            receivedFolders.Add(Convert.ToHexString(list.FolderId.Span)).ShouldBeTrue();
            list.Peers.Single().Endpoints.Single().Port.ShouldBe(41012u);
        }
        receivedFolders.Count.ShouldBe(500);
        reader.PendingPeerLists.ShouldBeEmpty();
    }

    [Fact]
    public void FullOutputQueuePreservesOlderMessagesAndAllowsPresenceRetry()
    {
        var registry = new PresenceRegistry();
        byte[] folder = Id(10);
        PresenceSession reader = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession sender = registry.Open(Id(2), Remote("203.0.113.2"));
        registry.Announce(reader, Announcement(folder, 41001));
        Drain(registry, reader);
        for (int index = 0; index < 2048; index++)
            reader.Outgoing.Writer.TryWrite(new ServerMessage()).ShouldBeTrue();

        registry.Announce(sender, Announcement(folder, 41002));
        reader.Outgoing.Reader.Count.ShouldBe(2048);
        reader.PendingPeerLists.ShouldBeEmpty();

        Drain(registry, reader);
        registry.Announce(sender, Announcement(folder, 41003));
        LastList(registry, reader).Peers.Single().Endpoints.Single().Port.ShouldBe(41003u);
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
        foreach (string invalidAddress in new[]
            { "0.0.0.1", "239.1.2.3", "255.255.255.255", "::ffff:239.1.2.3", "ff02::1", "fe80::1" })
        {
            Announce invalidUnicast = Announcement(Id(10), 41001);
            invalidUnicast.LocalEndpoints.Add(new Endpoint { Address = invalidAddress, Port = 41001 });
            Should.Throw<ArgumentException>(() => registry.Announce(session, invalidUnicast));
        }
        Announce tooMany = Announcement(Id(10), 41001);
        for (int i = 0; i < 500; i++) tooMany.FolderIds.Add(ByteString.CopyFrom(Id((byte)(i + 20))));
        Should.Throw<ArgumentException>(() => registry.Announce(session, tooMany));
    }

    [Fact]
    public void MappedIpv4EndpointIsPublishedAsCanonicalUnicast()
    {
        var registry = new PresenceRegistry();
        byte[] folder = Id(10);
        PresenceSession reader = registry.Open(Id(1), Remote("203.0.113.1"));
        PresenceSession sender = registry.Open(Id(2), Remote("203.0.113.2"));
        registry.Announce(reader, Announcement(folder, 41001));
        Announce announce = Announcement(folder, 41002);
        announce.LocalEndpoints.Add(new Endpoint { Address = "::ffff:100.72.94.33", Port = 47821 });

        registry.Announce(sender, announce);

        PeerList list = LastList(registry, reader);
        list.Peers.Single().Endpoints.Select(endpoint => endpoint.Address)
            .ShouldContain("100.72.94.33");
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
        Drain(registry, first); Drain(registry, second);

        registry.RequestPunch(first, second.DeviceId, Id(11)).ShouldBeFalse();
        registry.RequestPunch(first, second.DeviceId, folder).ShouldBeTrue();
        ServerMessage toFirst = Read(registry, first);
        ServerMessage toSecond = Read(registry, second);
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

    private static byte[] FolderId(int index)
    {
        byte[] id = new byte[32];
        id[0] = (byte)index;
        id[1] = (byte)(index >> 8);
        return id;
    }

    private static IPEndPoint Remote(string address) => new(IPAddress.Parse(address), 52000);

    private static Announce Announcement(byte[] folder, uint port)
    {
        var announce = new Announce { ListenPort = port };
        announce.FolderIds.Add(ByteString.CopyFrom(folder));
        return announce;
    }

    private static ServerMessage Read(PresenceRegistry registry, PresenceSession session)
    {
        registry.TryReadOutgoing(session, out ServerMessage? message).ShouldBeTrue();
        return message!;
    }

    private static void Drain(PresenceRegistry registry, PresenceSession session)
    {
        while (registry.TryReadOutgoing(session, out _)) { }
    }

    private static PeerList LastList(PresenceRegistry registry, PresenceSession session)
    {
        PeerList? latest = null;
        while (registry.TryReadOutgoing(session, out ServerMessage? message))
            if (message!.PeerList is { } list) latest = list;
        return latest ?? throw new InvalidOperationException("No peer list was pushed.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        internal void Advance(TimeSpan amount) => utcNow += amount;
    }
}
