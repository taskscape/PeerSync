using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Rendezvous.Contracts.V1;
using Endpoint = PeerSync.Rendezvous.Contracts.V1.Endpoint;

namespace PeerSync.Rendezvous.Tracker;

internal sealed class PresenceRegistry(TimeProvider? clock = null)
{
    internal static readonly TimeSpan PresenceLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan InitialAnnouncementLifetime = TimeSpan.FromSeconds(30);
    internal const int MaximumPeersPerFolder = 64;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, PresenceSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> requestTimes = new(StringComparer.Ordinal);

    internal PresenceSession Open(ReadOnlySpan<byte> deviceId, IPEndPoint observed)
    {
        if (deviceId.Length != 32) throw new ArgumentException("Device identity must contain 32 bytes.", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(observed);
        var session = new PresenceSession(deviceId.ToArray(), observed) { CreatedUtc = time.GetUtcNow() };
        lock (gate)
        {
            string key = Convert.ToHexString(deviceId);
            if (sessions.Remove(key, out PresenceSession? replaced))
            {
                replaced.End();
                BroadcastLocked(replaced.FolderKeys);
            }
            sessions.Add(key, session);
        }
        return session;
    }

    internal void Announce(PresenceSession session, Announce announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        if (announcement.FolderIds.Count > 500 || announcement.ListenPort is < 1 or > 65535
            || announcement.LocalEndpoints.Count > 8)
            throw new ArgumentException("Announcement exceeds its folder, endpoint or port limit.", nameof(announcement));
        var folderKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ByteString folderId in announcement.FolderIds)
        {
            if (folderId.Length != 32 || !folderKeys.Add(Convert.ToHexString(folderId.Span)))
                throw new ArgumentException("Announcement contains an invalid or duplicate folder identity.", nameof(announcement));
        }
        var endpoints = new List<Endpoint>(announcement.LocalEndpoints.Count + 1);
        foreach (Endpoint endpoint in announcement.LocalEndpoints)
        {
            if (!IPAddress.TryParse(endpoint.Address, out IPAddress? address)
                || !IsDialableUnicast(address) || endpoint.Port is < 1 or > 65535)
                throw new ArgumentException("Announcement contains an invalid local endpoint.", nameof(announcement));
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            endpoints.Add(new Endpoint { Address = address.ToString(), Port = endpoint.Port });
        }
        var observed = new Endpoint
        {
            Address = session.Observed.Address.ToString(), Port = announcement.ListenPort,
        };
        if (!endpoints.Any(item => item.Address == observed.Address && item.Port == observed.Port))
            endpoints.Add(observed);
        lock (gate)
        {
            RequireCurrentLocked(session);
            foreach (string folderKey in folderKeys)
                if (sessions.Values.Count(peer => !ReferenceEquals(peer, session)
                    && IsLiveLocked(peer) && peer.FolderKeys.Contains(folderKey)) >= MaximumPeersPerFolder)
                    throw new ArgumentException("Folder peer limit exceeded.", nameof(announcement));
            string[] affected = session.FolderKeys.Union(folderKeys, StringComparer.Ordinal).ToArray();
            session.FolderKeys = folderKeys;
            session.Endpoints = endpoints;
            session.LastAnnouncedUtc = time.GetUtcNow();
            BroadcastLocked(affected);
        }
    }

    private static bool IsDialableUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return address.GetAddressBytes()[0] is > 0 and < 224;
        return !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal;
    }

    internal bool TryGetShared(PresenceSession source, ReadOnlySpan<byte> remoteDeviceId,
        ReadOnlySpan<byte> folderId, out PresenceSession? target)
    {
        target = null;
        if (remoteDeviceId.Length != 32 || folderId.Length != 32) return false;
        lock (gate)
        {
            if (!IsCurrentLocked(source) || !IsLiveLocked(source)
                || !source.FolderKeys.Contains(Convert.ToHexString(folderId))
                || !sessions.TryGetValue(Convert.ToHexString(remoteDeviceId), out PresenceSession? candidate)
                || ReferenceEquals(candidate, source) || !IsLiveLocked(candidate)
                || !candidate.FolderKeys.Contains(Convert.ToHexString(folderId))) return false;
            target = candidate;
            return true;
        }
    }

    internal bool TryTakeRequest(PresenceSession session)
    {
        lock (gate)
        {
            if (!IsCurrentLocked(session)) return false;
            DateTimeOffset now = time.GetUtcNow();
            if (!requestTimes.TryGetValue(session.Key, out Queue<DateTimeOffset>? recent))
            {
                recent = new Queue<DateTimeOffset>();
                requestTimes.Add(session.Key, recent);
            }
            while (recent.Count > 0 && now - recent.Peek() >= TimeSpan.FromMinutes(1))
                recent.Dequeue();
            if (recent.Count >= 60) return false;
            recent.Enqueue(now);
            return true;
        }
    }

    internal bool RequestPunch(PresenceSession source, ReadOnlySpan<byte> remoteDeviceId,
        ReadOnlySpan<byte> folderId)
    {
        if (!TryGetShared(source, remoteDeviceId, folderId, out PresenceSession? target)) return false;
        long at = time.GetUtcNow().AddMilliseconds(500).UtcTicks;
        var forSource = new PunchNow { DeviceId = ByteString.CopyFrom(target!.DeviceId), StartUtcTicks = at };
        forSource.Endpoints.Add(target.Endpoints.Select(endpoint => endpoint.Clone()));
        var forTarget = new PunchNow { DeviceId = ByteString.CopyFrom(source.DeviceId), StartUtcTicks = at };
        forTarget.Endpoints.Add(source.Endpoints.Select(endpoint => endpoint.Clone()));
        return source.Outgoing.Writer.TryWrite(new ServerMessage { PunchNow = forSource })
            && target.Outgoing.Writer.TryWrite(new ServerMessage { PunchNow = forTarget });
    }

    internal int Expire()
    {
        lock (gate)
        {
            PresenceSession[] expired = sessions.Values.Where(item =>
                item.LastAnnouncedUtc is { } last
                    ? time.GetUtcNow() - last >= PresenceLifetime
                    : time.GetUtcNow() - item.CreatedUtc >= InitialAnnouncementLifetime).ToArray();
            foreach (PresenceSession session in expired)
            {
                sessions.Remove(session.Key);
                session.End();
            }
            BroadcastLocked(expired.SelectMany(item => item.FolderKeys).Distinct(StringComparer.Ordinal));
            foreach (string key in requestTimes.Keys.ToArray())
            {
                Queue<DateTimeOffset> recent = requestTimes[key];
                while (recent.Count > 0 && time.GetUtcNow() - recent.Peek() >= TimeSpan.FromMinutes(1))
                    recent.Dequeue();
                if (recent.Count == 0) requestTimes.Remove(key);
            }
            return expired.Length;
        }
    }

    internal void Close(PresenceSession session)
    {
        lock (gate)
        {
            if (!IsCurrentLocked(session)) return;
            sessions.Remove(session.Key);
            session.End();
            BroadcastLocked(session.FolderKeys);
        }
    }

    internal void CloseAll()
    {
        lock (gate)
        {
            foreach (PresenceSession session in sessions.Values) session.End();
            sessions.Clear();
        }
    }

    private void BroadcastLocked(IEnumerable<string> folderKeys)
    {
        string[] affected = folderKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (affected.Length == 0) return;
        foreach (PresenceSession receiver in sessions.Values)
        {
            if (!IsLiveLocked(receiver)) continue;
            foreach (string folderKey in affected)
            {
                if (!receiver.FolderKeys.Contains(folderKey)
                    || !receiver.PendingPeerLists.Add(folderKey)) continue;
                var pending = new ServerMessage
                { PeerList = new PeerList { FolderId = ByteString.CopyFrom(Convert.FromHexString(folderKey)) } };
                if (!receiver.Outgoing.Writer.TryWrite(pending))
                    receiver.PendingPeerLists.Remove(folderKey);
            }
        }
    }

    internal bool TryMaterialize(PresenceSession receiver, ServerMessage queued, out ServerMessage? message)
    {
        if (queued.PayloadCase != ServerMessage.PayloadOneofCase.PeerList)
        {
            message = queued;
            return true;
        }

        string folderKey = Convert.ToHexString(queued.PeerList.FolderId.Span);
        lock (gate)
        {
            receiver.PendingPeerLists.Remove(folderKey);
            if (!IsCurrentLocked(receiver) || !IsLiveLocked(receiver)
                || !receiver.FolderKeys.Contains(folderKey))
            {
                message = null;
                return false;
            }
            var list = new PeerList { FolderId = queued.PeerList.FolderId };
            foreach (PresenceSession peer in sessions.Values)
            {
                if (ReferenceEquals(peer, receiver) || !IsLiveLocked(peer)
                    || !peer.FolderKeys.Contains(folderKey)) continue;
                var entry = new Peer { DeviceId = ByteString.CopyFrom(peer.DeviceId) };
                entry.Endpoints.Add(peer.Endpoints.Select(endpoint => endpoint.Clone()));
                list.Peers.Add(entry);
            }
            message = new ServerMessage { PeerList = list };
            return true;
        }
    }

    internal bool TryReadOutgoing(PresenceSession session, out ServerMessage? message)
    {
        while (session.Outgoing.Reader.TryRead(out ServerMessage? queued))
            if (TryMaterialize(session, queued, out message)) return true;
        message = null;
        return false;
    }

    private bool IsLiveLocked(PresenceSession session) => session.LastAnnouncedUtc is { } last
        && time.GetUtcNow() - last < PresenceLifetime;

    private bool IsCurrentLocked(PresenceSession session)
        => sessions.TryGetValue(session.Key, out PresenceSession? current) && ReferenceEquals(current, session);

    private void RequireCurrentLocked(PresenceSession session)
    {
        if (!IsCurrentLocked(session)) throw new InvalidOperationException("Rendezvous session was replaced or closed.");
    }
}

internal sealed class PresenceSession
{
    internal PresenceSession(byte[] deviceId, IPEndPoint observed)
    {
        DeviceId = deviceId;
        Observed = observed;
        Outgoing = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true,
            SingleWriter = false, AllowSynchronousContinuations = false,
        });
    }

    internal Guid SessionId { get; } = Guid.NewGuid();
    internal byte[] DeviceId { get; }
    internal string Key => Convert.ToHexString(DeviceId);
    internal IPEndPoint Observed { get; }
    internal Channel<ServerMessage> Outgoing { get; }
    internal CancellationTokenSource Lifetime { get; } = new();
    internal DateTimeOffset CreatedUtc { get; init; }
    internal HashSet<string> FolderKeys { get; set; } = new(StringComparer.Ordinal);
    internal HashSet<string> PendingPeerLists { get; } = new(StringComparer.Ordinal);
    internal List<Endpoint> Endpoints { get; set; } = [];
    internal DateTimeOffset? LastAnnouncedUtc { get; set; }
    internal void End()
    {
        Lifetime.Cancel();
        Outgoing.Writer.TryComplete();
    }

}
