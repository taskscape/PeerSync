using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace PeerSync.Net.Discovery;

/// <summary>Bounded wire representation for one hourly rotating LAN presence announcement.</summary>
public static class LanDiscoveryPacket
{
    public const int MaximumFolderTags = 64;
    public const int TagLength = 16;
    public const int MaximumPacketBytes = 1200;
    private const int HeaderLength = 48;
    private const byte Version = 1;
    private static ReadOnlySpan<byte> Magic => "PSPD"u8;
    private static ReadOnlySpan<byte> TagLabel => "psp1 lan"u8;

    public sealed record Announcement(byte[] DeviceId, int ListenPort, long Hour, IReadOnlyList<byte[]> FolderTags);

    public static byte[] Create(ReadOnlySpan<byte> deviceId, int listenPort,
        IEnumerable<ReadOnlyMemory<byte>> folderIds, DateTimeOffset? now = null)
    {
        if (deviceId.Length != 32) throw new ArgumentException("DeviceId must be 32 bytes.", nameof(deviceId));
        if (listenPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(listenPort));
        ArgumentNullException.ThrowIfNull(folderIds);
        long hour = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 3600;
        var tags = new List<byte[]>();
        foreach (ReadOnlyMemory<byte> folderId in folderIds)
        {
            if (folderId.Length != 32) throw new ArgumentException("Folder identifiers must be 32 bytes.", nameof(folderIds));
            byte[] tag = CreateTag(folderId.Span, deviceId, hour);
            if (!tags.Any(existing => CryptographicOperations.FixedTimeEquals(existing, tag))) tags.Add(tag);
            if (tags.Count > MaximumFolderTags) throw new ArgumentException("Too many folder memberships for one announcement.", nameof(folderIds));
        }
        tags.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[] packet = new byte[HeaderLength + tags.Count * TagLength];
        Magic.CopyTo(packet);
        packet[4] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(5), hour);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(13), (ushort)listenPort);
        deviceId.CopyTo(packet.AsSpan(15, 32));
        packet[47] = (byte)tags.Count;
        for (int i = 0; i < tags.Count; i++) tags[i].CopyTo(packet, HeaderLength + (i * TagLength));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out Announcement? announcement)
    {
        announcement = null;
        if (packet.Length < HeaderLength || packet.Length > MaximumPacketBytes
            || !packet[..4].SequenceEqual(Magic) || packet[4] != Version) return false;
        int count = packet[47];
        if (count > MaximumFolderTags || packet.Length != HeaderLength + count * TagLength) return false;
        int port = BinaryPrimitives.ReadUInt16BigEndian(packet[13..]);
        if (port == 0) return false;
        var tags = new byte[count][];
        for (int i = 0; i < count; i++) tags[i] = packet.Slice(HeaderLength + i * TagLength, TagLength).ToArray();
        announcement = new(packet.Slice(15, 32).ToArray(), port,
            BinaryPrimitives.ReadInt64LittleEndian(packet[5..]), tags);
        return true;
    }

    public static bool MatchesAnyFolder(Announcement announcement,
        IEnumerable<ReadOnlyMemory<byte>> folderIds, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(folderIds);
        long currentHour = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 3600;
        if (announcement.Hour < currentHour - 1 || announcement.Hour > currentHour + 1) return false;
        foreach (ReadOnlyMemory<byte> folderId in folderIds)
        {
            if (folderId.Length != 32) continue;
            for (long hour = currentHour - 1; hour <= currentHour + 1; hour++)
            {
                byte[] expected = CreateTag(folderId.Span, announcement.DeviceId, hour);
                foreach (byte[] actual in announcement.FolderTags)
                    if (actual.Length == TagLength && CryptographicOperations.FixedTimeEquals(expected, actual)) return true;
            }
        }
        return false;
    }

    private static byte[] CreateTag(ReadOnlySpan<byte> folderId, ReadOnlySpan<byte> deviceId, long hour)
    {
        Span<byte> input = stackalloc byte[TagLabel.Length + sizeof(long) + 32];
        TagLabel.CopyTo(input);
        BinaryPrimitives.WriteInt64LittleEndian(input[TagLabel.Length..], hour);
        deviceId.CopyTo(input[(TagLabel.Length + sizeof(long))..]);
        byte[] full = HMACSHA256.HashData(folderId, input);
        try { return full.AsSpan(0, TagLength).ToArray(); }
        finally { CryptographicOperations.ZeroMemory(full); }
    }
}
