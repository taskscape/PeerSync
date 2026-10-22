using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Discovery;
using Shouldly;
using Xunit;
using System.Security.Cryptography;

namespace PeerSync.Net.Tests;

public sealed class LanDiscoveryPacketTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void AnnouncementMatchesSharedFolderAndExposesOnlyRotatingTags()
    {
        byte[] device = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] folder = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        byte[] otherFolder = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        byte[] packet = LanDiscoveryPacket.Create(device, 47821,
            [folder, folder, otherFolder], Epoch);

        packet.Length.ShouldBe(48 + 2 * LanDiscoveryPacket.TagLength);
        packet.AsSpan().IndexOf(folder).ShouldBe(-1);
        LanDiscoveryPacket.TryDecode(packet, out var decoded).ShouldBeTrue();
        decoded.ShouldNotBeNull();
        decoded!.DeviceId.ShouldBe(device);
        decoded.ListenPort.ShouldBe(47821);
        decoded.Hour.ShouldBe(Epoch.ToUnixTimeSeconds() / 3600);
        decoded.FolderTags.Count.ShouldBe(2);
        LanDiscoveryPacket.MatchesAnyFolder(decoded, [folder], Epoch).ShouldBeTrue();
        LanDiscoveryPacket.MatchesAnyFolder(decoded, [Enumerable.Repeat((byte)1, 32).ToArray()], Epoch).ShouldBeFalse();
    }

    [Fact]
    public void RotatingTagAcceptsAdjacentHourButRejectsOlderAnnouncements()
    {
        byte[] device = new byte[32];
        byte[] folder = Enumerable.Repeat((byte)0x81, 32).ToArray();
        DateTimeOffset now = Epoch.AddMinutes(2);
        byte[] previousHour = LanDiscoveryPacket.Create(device, 47821, [folder], Epoch);
        LanDiscoveryPacket.TryDecode(previousHour, out var decoded).ShouldBeTrue();
        LanDiscoveryPacket.MatchesAnyFolder(decoded!, [folder], now).ShouldBeTrue();
        LanDiscoveryPacket.MatchesAnyFolder(decoded!, [folder], now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void DecoderRejectsTruncatedOversizedAndInvalidPortPackets()
    {
        byte[] valid = LanDiscoveryPacket.Create(new byte[32], 47821, [], Epoch);
        LanDiscoveryPacket.TryDecode(valid.AsSpan(0, valid.Length - 1), out _).ShouldBeFalse();
        LanDiscoveryPacket.TryDecode(new byte[LanDiscoveryPacket.MaximumPacketBytes + 1], out _).ShouldBeFalse();
        valid[13] = 0;
        valid[14] = 0;
        LanDiscoveryPacket.TryDecode(valid, out _).ShouldBeFalse();
    }

    [Fact]
    public void EncoderRejectsInvalidIdentityPortAndTooManyFolders()
    {
        Should.Throw<ArgumentException>(() => LanDiscoveryPacket.Create(new byte[31], 47821, [], Epoch));
        Should.Throw<ArgumentOutOfRangeException>(() => LanDiscoveryPacket.Create(new byte[32], 0, [], Epoch));
        var folders = Enumerable.Range(0, LanDiscoveryPacket.MaximumFolderTags + 1)
            .Select(index => (ReadOnlyMemory<byte>)Enumerable.Repeat((byte)index, 32).ToArray());
        Should.Throw<ArgumentException>(() => LanDiscoveryPacket.Create(new byte[32], 47821, folders, Epoch));
    }

    [Fact]
    public async Task LiveMulticastDiscoversBothPeersOnAnActiveNonLoopbackInterfaceWithinFiveSeconds()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] folder = RandomNumberGenerator.GetBytes(32);
        byte[] firstDevice = RandomNumberGenerator.GetBytes(32);
        byte[] secondDevice = RandomNumberGenerator.GetBytes(32);
        var firstFound = new TaskCompletionSource<DiscoveredLanPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFound = new TaskCompletionSource<DiscoveredLanPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new LanDiscovery(firstDevice, 47821, () => [(ReadOnlyMemory<byte>)folder]);
        await using var second = new LanDiscovery(secondDevice, 47822, () => [(ReadOnlyMemory<byte>)folder]);
        first.PeerDiscovered += peer =>
        {
            if (peer.DeviceId.AsSpan().SequenceEqual(secondDevice)) firstFound.TrySetResult(peer);
        };
        second.PeerDiscovered += peer =>
        {
            if (peer.DeviceId.AsSpan().SequenceEqual(firstDevice)) secondFound.TrySetResult(peer);
        };

        first.Start();
        second.Start();
        Task<DiscoveredLanPeer> firstResult = firstFound.Task.WaitAsync(timeout.Token);
        Task<DiscoveredLanPeer> secondResult = secondFound.Task.WaitAsync(timeout.Token);
        await Task.WhenAll(firstResult, secondResult);

        firstResult.Result.DeviceId.ShouldBe(secondDevice);
        firstResult.Result.Port.ShouldBe(47822);
        secondResult.Result.DeviceId.ShouldBe(firstDevice);
        secondResult.Result.Port.ShouldBe(47821);
        IPAddress.IsLoopback(firstResult.Result.Address).ShouldBeFalse();
        IPAddress.IsLoopback(secondResult.Result.Address).ShouldBeFalse();
    }

    [Fact]
    public async Task ShutdownDuringLanAnnouncementDoesNotLogDisposedSocketAsFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-LanShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string log = Path.Combine(root, "debug.jsonl");
            using var journal = new DiagnosticJournal(root, "lan_shutdown", debugEnabled: true);
            var discovery = new LanDiscovery(RandomNumberGenerator.GetBytes(32), 47821,
                static () => [], journal);
            discovery.Start();
            await discovery.DisposeAsync();

            string records = File.ReadAllText(log);
            records.ShouldContain("discovery.lan_stopped");
            records.ShouldNotContain("discovery.lan_announce_failed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
