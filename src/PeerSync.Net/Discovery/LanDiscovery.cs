using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Net.Discovery;

public sealed record DiscoveredLanPeer(byte[] DeviceId, IPAddress Address, int Port);

/// <summary>LAN-only discovery using one-second, bounded announcements on each active interface.</summary>
public sealed class LanDiscovery : IAsyncDisposable
{
    public const int DiscoveryPort = 47820;
    public static readonly IPAddress IPv4Group = IPAddress.Parse("239.255.80.83");
    public static readonly IPAddress IPv6Group = IPAddress.Parse("ff12::5053");
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(1);
    private readonly byte[] deviceId;
    private readonly int listenPort;
    private readonly Func<IReadOnlyCollection<ReadOnlyMemory<byte>>> getFolderIds;
    private readonly DiagnosticJournal? diagnostics;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<int> joinedV4 = [];
    private readonly HashSet<int> joinedV6 = [];
    private readonly Dictionary<string, DateTimeOffset> reportedPeers = new(StringComparer.Ordinal);
    private readonly List<Socket> receivers = [];
    private Task? receiveV4;
    private Task? receiveV6;
    private Task? announce;
    private bool started;
    private int disposed;

    public LanDiscovery(ReadOnlySpan<byte> deviceId, int listenPort,
        Func<IReadOnlyCollection<ReadOnlyMemory<byte>>> getFolderIds,
        DiagnosticJournal? diagnostics = null, TimeProvider? timeProvider = null)
    {
        if (deviceId.Length != 32) throw new ArgumentException("DeviceId must be 32 bytes.", nameof(deviceId));
        if (listenPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(listenPort));
        this.getFolderIds = getFolderIds ?? throw new ArgumentNullException(nameof(getFolderIds));
        this.deviceId = deviceId.ToArray();
        this.listenPort = listenPort;
        this.diagnostics = diagnostics;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<DiscoveredLanPeer>? PeerDiscovered;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (gate)
        {
            if (started) return;
            started = true;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            try
            {
                TryOpenReceiver(AddressFamily.InterNetwork);
                TryOpenReceiver(AddressFamily.InterNetworkV6);
                if (receivers.Count == 0) throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                RefreshMemberships();
                Socket? v4 = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetwork);
                Socket? v6 = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetworkV6);
                if (v4 is not null) receiveV4 = ReceiveLoopAsync(v4, stop.Token);
                if (v6 is not null) receiveV6 = ReceiveLoopAsync(v6, stop.Token);
                announce = AnnounceLoopAsync(stop.Token);
            }
            catch
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                foreach (Socket socket in receivers) socket.Dispose();
                receivers.Clear();
                started = false;
                throw;
            }
        }
        diagnostics?.TryWrite(new("discovery.lan_started", "lan_discovery"));
    }

    private void TryOpenReceiver(AddressFamily family)
    {
        try
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (family == AddressFamily.InterNetworkV6) socket.DualMode = false;
                socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, DiscoveryPort));
                receivers.Add(socket);
            }
            catch { socket.Dispose(); throw; }
        }
        catch (SocketException error)
        {
            diagnostics?.TryWrite(new("discovery.lan_socket_unavailable", "lan_discovery")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.SocketErrorCode.ToString() });
        }
        catch (PlatformNotSupportedException)
        {
            diagnostics?.TryWrite(new("discovery.lan_socket_unavailable", "lan_discovery")
            { Level = DiagnosticLevel.Warning, ErrorCode = "platform_not_supported" });
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => RefreshMemberships();

    internal void RefreshMemberships()
    {
        lock (gate)
        {
            if (!started || Volatile.Read(ref disposed) != 0) return;
            try
            {
                var desiredV4 = new HashSet<int>();
                var desiredV6 = new HashSet<int>();
                Socket? v4Socket = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetwork);
                Socket? v6Socket = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetworkV6);
                foreach (NetworkInterface nic in GetUsableInterfaces())
                {
                    if (!nic.SupportsMulticast) continue;
                    try
                    {
                        IPInterfaceProperties properties = nic.GetIPProperties();
                        if (v4Socket is not null && properties.UnicastAddresses.Any(address =>
                            address.Address.AddressFamily == AddressFamily.InterNetwork && !address.Address.Equals(IPAddress.Any))
                            && SafeIPv4(nic) is { } v4)
                            desiredV4.Add(v4.Index);
                        if (v6Socket is not null && properties.UnicastAddresses.Any(address =>
                            address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                            && SafeIPv6(nic) is { } v6)
                            desiredV6.Add(v6.Index);
                    }
                    catch (NetworkInformationException error)
                    {
                        diagnostics?.TryWrite(new("discovery.lan_membership_refresh_failed", "lan_discovery")
                        { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
                    }
                }

                if (v4Socket is not null)
                    ReconcileMemberships(joinedV4, desiredV4, index =>
                        v4Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                            new MulticastOption(IPv4Group, index)), index =>
                        v4Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(IPv4Group, index)), "ipv4");
                if (v6Socket is not null)
                    ReconcileMemberships(joinedV6, desiredV6, index =>
                        v6Socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.DropMembership,
                            new IPv6MulticastOption(IPv6Group, index)), index =>
                        v6Socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                            new IPv6MulticastOption(IPv6Group, index)), "ipv6");
            }
            catch (NetworkInformationException error)
            {
                diagnostics?.TryWrite(new("discovery.lan_membership_refresh_failed", "lan_discovery")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
        }
    }

    internal void ReconcileMemberships(HashSet<int> joined, HashSet<int> desired,
        Action<int> drop, Action<int> add, string family)
    {
        int changed = 0;
        // Address-change notifications can preserve an interface index even after Windows
        // discarded its socket membership. Rejoin every current interface, not only new IDs.
        foreach (int index in joined.ToArray())
        {
            try { drop(index); }
            catch (SocketException) { } // Windows may already have removed a departed interface.
            joined.Remove(index);
            changed++;
        }
        foreach (int index in desired)
        {
            try { add(index); joined.Add(index); changed++; }
            catch (SocketException error)
            {
                diagnostics?.TryWrite(new("discovery.lan_membership_join_failed", family)
                { Level = DiagnosticLevel.Warning, ErrorCode = error.SocketErrorCode.ToString(), HResult = error.HResult });
            }
        }
        diagnostics?.TryWrite(new("discovery.lan_memberships", family)
        { Level = DiagnosticLevel.Debug, ItemsVisited = desired.Count, ItemsChanged = changed });
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    IReadOnlyCollection<ReadOnlyMemory<byte>> folders = getFolderIds();
                    byte[] packet = LanDiscoveryPacket.Create(deviceId, listenPort, folders, timeProvider.GetUtcNow());
                    await SendOnInterfacesAsync(packet, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    diagnostics?.TryWrite(new("discovery.lan_announce_failed", "lan_discovery")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
                await Task.Delay(AnnounceInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendOnInterfacesAsync(byte[] packet, CancellationToken cancellationToken)
    {
        foreach (NetworkInterface nic in GetUsableInterfaces())
        {
            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            IPv4InterfaceProperties? v4 = SafeIPv4(nic);
            Socket? send4 = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is not null && send4 is not null)
            {
                if (nic.SupportsMulticast)
                {
                    try
                    {
                        send4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                        send4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(v4.Index));
                        await send4.SendToAsync(packet, SocketFlags.None, new IPEndPoint(IPv4Group, DiscoveryPort), cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException error) { LogSendFailure(error); }
                }
                try
                {
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                        IPAddress broadcast = BroadcastAddress(address.Address, address.IPv4Mask);
                        send4.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                        await send4.SendToAsync(packet, SocketFlags.None, new IPEndPoint(broadcast, DiscoveryPort), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (SocketException error) { LogSendFailure(error); }
            }

            IPv6InterfaceProperties? v6 = SafeIPv6(nic);
            Socket? send6 = receivers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetworkV6);
            if (nic.SupportsMulticast && v6 is not null && send6 is not null)
            {
                try
                {
                    send6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, 1);
                    send6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, v6.Index);
                    IPAddress group = new(IPv6Group.GetAddressBytes(), v6.Index);
                    await send6.SendToAsync(packet, SocketFlags.None, new IPEndPoint(group, DiscoveryPort), cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException error)
                {
                    diagnostics?.TryWrite(new("discovery.lan_send_failed", "lan_discovery")
                    { Level = DiagnosticLevel.Debug, ErrorCode = error.SocketErrorCode.ToString(), HResult = error.HResult });
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[LanDiscoveryPacket.MaximumPacketBytes + 1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EndPoint placeholder = socket.AddressFamily == AddressFamily.InterNetwork
                    ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, placeholder, cancellationToken).ConfigureAwait(false);
                if (received.ReceivedBytes > LanDiscoveryPacket.MaximumPacketBytes
                    || !LanDiscoveryPacket.TryDecode(buffer.AsSpan(0, received.ReceivedBytes), out var announcement)
                    || announcement is null)
                {
                    diagnostics?.TryWrite(new("discovery.lan_packet_rejected", "lan_discovery") { Level = DiagnosticLevel.Debug, ErrorCode = "invalid_announcement" });
                    continue;
                }
                if (CryptographicOperations.FixedTimeEquals(announcement.DeviceId, deviceId)) continue;
                IReadOnlyCollection<ReadOnlyMemory<byte>> folders = getFolderIds();
                if (!LanDiscoveryPacket.MatchesAnyFolder(announcement, folders, timeProvider.GetUtcNow())) continue;
                if (received.RemoteEndPoint is not IPEndPoint remote || remote.Address.IsIPv6Multicast || IPAddress.IsLoopback(remote.Address)) continue;
                string peerKey = Convert.ToHexString(announcement.DeviceId) + "|" + remote.Address + "|" + announcement.ListenPort;
                DateTimeOffset now = timeProvider.GetUtcNow();
                lock (gate)
                {
                    if (reportedPeers.TryGetValue(peerKey, out DateTimeOffset previous) && now - previous < TimeSpan.FromSeconds(10)) continue;
                    reportedPeers[peerKey] = now;
                    foreach (string stale in reportedPeers.Where(pair => now - pair.Value > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray())
                        reportedPeers.Remove(stale);
                }
                diagnostics?.TryWrite(new("discovery.lan_peer_found", "lan_discovery")
                { Level = DiagnosticLevel.Debug, RemoteAddress = remote.Address });
                var peer = new DiscoveredLanPeer(announcement.DeviceId, remote.Address, announcement.ListenPort);
                if (PeerDiscovered is not { } callbacks) continue;
                foreach (Action<DiscoveredLanPeer> callback in callbacks.GetInvocationList())
                {
                    try { callback(peer); }
                    catch (Exception error)
                    {
                        diagnostics?.TryWrite(new("discovery.lan_callback_failed", "lan_discovery")
                        { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("discovery.lan_receive_failed", "lan_discovery")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
    }

    private static IEnumerable<NetworkInterface> GetUsableInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up
            && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    private void LogSendFailure(SocketException error) => diagnostics?.TryWrite(new("discovery.lan_send_failed", "lan_discovery")
    { Level = DiagnosticLevel.Debug, ErrorCode = error.SocketErrorCode.ToString(), HResult = error.HResult });

    private static IPv4InterfaceProperties? SafeIPv4(NetworkInterface nic)
    { try { return nic.GetIPProperties().GetIPv4Properties(); } catch (NetworkInformationException) { return null; } }

    private static IPv6InterfaceProperties? SafeIPv6(NetworkInterface nic)
    { try { return nic.GetIPProperties().GetIPv6Properties(); } catch (NetworkInformationException) { return null; } }

    private static IPAddress BroadcastAddress(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes(), subnet = mask.GetAddressBytes();
        if (ip.Length != subnet.Length) throw new ArgumentException("IPv4 address and subnet mask lengths differ.");
        for (int i = 0; i < ip.Length; i++) ip[i] = (byte)(ip[i] | ~subnet[i]);
        return new IPAddress(ip);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        stop.Cancel();
        lock (gate) foreach (Socket socket in receivers) socket.Dispose();
        Task[] tasks = new[] { receiveV4, receiveV6, announce }.Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        stop.Dispose();
        diagnostics?.TryWrite(new("discovery.lan_stopped", "lan_discovery"));
    }
}
