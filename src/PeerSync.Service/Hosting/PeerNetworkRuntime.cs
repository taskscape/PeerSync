using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Engine.Sync;
using PeerSync.Net.Connections;
using PeerSync.Net.Discovery;
using PeerSync.Net.Quic;
using PeerSync.Net.Rendezvous;
using PeerSync.Net.Relay;
using PeerSync.Net.Nat;
using PeerSync.Net.Security;
using PeerSync.Net.Tcp;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using PeerSync.Service.Control;
using PeerSync.Storage.FolderDb;
using PeerSync.Rendezvous.Contracts.V1;
using RendezvousEndpoint = PeerSync.Rendezvous.Contracts.V1.Endpoint;

namespace PeerSync.Service.Hosting;

/// <summary>Owns the installed service's authenticated peer listener, discovery, reconnects and folder passes.</summary>
internal sealed class PeerNetworkRuntime : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerBindRetryWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenerBindRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly X509Certificate2 certificate;
    private readonly byte[] localDeviceId;
    private readonly Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders;
    private readonly DiagnosticJournal? diagnostics;
    private readonly ControlStatusRuntime status;
    private readonly TimeProvider timeProvider;
    private readonly QuicPeerOptions options;
    private readonly RegistrationList relayRegistrations;
    private readonly TcpPeerProtocolOptions tcpProtocolOptions;
    private readonly TcpPeerOptions tcpOptions;
    private readonly ConnectionManager connections;
    private readonly CancellationTokenSource stopping = new();
    private readonly PeerEndpointCatalog endpoints;
    private readonly ConcurrentDictionary<string, Task> maintainers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> sessionWorkers = new();
    private readonly ConcurrentDictionary<Guid, Task> relayAcceptWorkers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayOffer>> pendingRelayOffers = new(StringComparer.Ordinal);
    private readonly RelayPresenceCatalog relayPresence = new();
    private readonly ConcurrentDictionary<Guid, IAsyncDisposable> sessions = new();
    private readonly object networkRecoveryGate = new();
    private readonly object maintainerGate = new();
    private Task? networkRecoveryTask;
    private bool networkRecoveryRequested;
    private string? pendingNetworkRecoveryReason;
    private int coalescedNetworkRecoveryRequests;
    private QuicPeerListener? listener;
    private TcpPeerListener? tcpListener;
    private LanDiscovery? discovery;
    private PredefinedHosts? predefinedHosts;
    private readonly List<TrackerClient> trackers = [];
    private bool useRelay;
    private PortMapper? portMapper;
    private Task? acceptTask;
    private int disposed;

    public Task Completion => acceptTask ?? Task.CompletedTask;

    private PeerNetworkRuntime(X509Certificate2 certificate, byte[] localDeviceId,
        Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders, ControlStatusRuntime status,
        Action<byte[], byte[]> recordPending, DiagnosticJournal? diagnostics, TimeProvider? timeProvider)
    {
        this.certificate = certificate;
        this.localDeviceId = localDeviceId.ToArray();
        this.getFolders = getFolders;
        this.status = status;
        this.diagnostics = diagnostics;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        endpoints = new PeerEndpointCatalog(this.timeProvider, diagnostics: diagnostics);
        options = new QuicPeerOptions
        {
            Certificate = certificate,
            CreateHello = () => new Hello { ProtocolVersion = 1, UtcTicks = this.timeProvider.GetUtcNow().UtcTicks },
            Folders = new RegistrationList(getFolders, recordPending),
            IsDevicePinned = IsDevicePinned,
            Diagnostics = diagnostics,
            DeviceName = Environment.MachineName,
        };
        relayRegistrations = new RegistrationList(
            () => getFolders().Where(folder => folder.Settings.UseTracker && folder.Settings.UseRelay).ToArray(),
            recordPending);
        tcpProtocolOptions = new TcpPeerProtocolOptions
        {
            CreateHello = options.CreateHello, Folders = options.Folders, Diagnostics = diagnostics,
            AddStrike = options.AddStrike, DeviceName = options.DeviceName, ClientVersion = options.ClientVersion,
            RequestTimeout = options.RequestTimeout,
            MaximumConcurrentBlockRequests = TcpPeerSession.MaximumDataConnections,
        };
        tcpOptions = new TcpPeerOptions
        {
            Certificate = certificate, IsDevicePinned = IsDevicePinned, Diagnostics = diagnostics,
        };
        connections = new ConnectionManager(localDeviceId, diagnostics);
        connections.Authenticated += OnAuthenticated;
    }

    public static async Task<PeerNetworkRuntime> StartAsync(X509Certificate2 certificate, byte[] deviceId,
        Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders, DeviceSettings deviceSettings, ControlStatusRuntime status,
        Action<byte[], byte[]> recordPending, DiagnosticJournal? diagnostics, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        var runtime = new PeerNetworkRuntime(certificate, deviceId, getFolders, status, recordPending, diagnostics, timeProvider);
        runtime.useRelay = deviceSettings.UseRelay;
        try
        {
            if (!QuicListener.IsSupported)
            {
                diagnostics?.TryWrite(new("service.quic_unavailable", "peer_network")
                    { Level = DiagnosticLevel.Error, ErrorCode = "platform_not_supported" });
                throw new PlatformNotSupportedException("The installed service requires Windows QUIC support.");
            }

            (runtime.listener, runtime.tcpListener) = await BindListenersAsync(deviceSettings.ListenPort,
                runtime.options, runtime.tcpOptions, diagnostics, cancellationToken).ConfigureAwait(false);
            runtime.acceptTask = Task.WhenAll(runtime.AcceptQuicLoopAsync(runtime.stopping.Token),
                runtime.AcceptTcpLoopAsync(runtime.stopping.Token));
            diagnostics?.TryWrite(new("service.quic_listener_ready", "peer_network")
                { Transport = DiagnosticTransport.Quic, ItemsChanged = deviceSettings.ListenPort });
            diagnostics?.TryWrite(new("service.tcp_listener_ready", "peer_network")
                { Transport = DiagnosticTransport.Tcp, ItemsChanged = deviceSettings.ListenPort });

            await runtime.RefreshLanDiscoveryAsync(deviceSettings, cancellationToken).ConfigureAwait(false);

            runtime.predefinedHosts = new PredefinedHosts(() => getFolders().SelectMany(folder => folder.Settings.PredefinedHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), runtime.DialPredefinedHostAsync, diagnostics);
            runtime.predefinedHosts.Start();
            if (deviceSettings.UsePortMapping)
            {
                runtime.portMapper = new PortMapper(deviceSettings.ListenPort, diagnostics, runtime.timeProvider);
                runtime.portMapper.MappingsChanged += runtime.RefreshTrackerAnnouncements;
                runtime.portMapper.Start();
            }
            foreach (string serverText in deviceSettings.RendezvousServers.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(serverText, UriKind.Absolute, out Uri? server))
                {
                    diagnostics?.TryWrite(new("tracker.server_invalid", "rendezvous")
                    { Level = DiagnosticLevel.Warning, ErrorCode = "invalid_uri" });
                    continue;
                }
                try
                {
                    var tracker = new TrackerClient(server, certificate,
                        () => runtime.CreateAnnouncement(deviceSettings.ListenPort),
                        (message, token) => runtime.OnTrackerMessageFromServerAsync(server, message, token), diagnostics);
                    runtime.trackers.Add(tracker);
                    tracker.Start();
                }
                catch (ArgumentException error)
                {
                    diagnostics?.TryWrite(new("tracker.server_invalid", "rendezvous")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name });
                }
            }
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(QuicPeerListener Quic, TcpPeerListener Tcp)> BindListenersAsync(int port,
        QuicPeerOptions quicOptions, TcpPeerOptions tcpOptions, DiagnosticJournal? diagnostics,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int retries = 0;
        var endpoint = new IPEndPoint(IPAddress.Any, port);
        while (true)
        {
            QuicPeerListener? quic = null;
            TcpPeerListener? tcp = null;
            try
            {
                quic = await QuicPeerListener.ListenAsync(endpoint, quicOptions, cancellationToken).ConfigureAwait(false);
                tcp = TcpPeerListener.Listen(endpoint, tcpOptions);
                if (retries != 0)
                    diagnostics?.TryWrite(new("service.listener_bind_recovered", "peer_network")
                    { ItemsVisited = retries, ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                return (quic, tcp);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse
                && Stopwatch.GetElapsedTime(started) < ListenerBindRetryWindow)
            {
                if (tcp is not null) await tcp.DisposeAsync().ConfigureAwait(false);
                if (quic is not null) await quic.DisposeAsync().ConfigureAwait(false);
                if (++retries == 1)
                    diagnostics?.TryWrite(new("service.listener_bind_retry", "peer_network")
                    { Level = DiagnosticLevel.Warning, ErrorCode = "address_in_use", NativeError = error.NativeErrorCode,
                        RetryInMilliseconds = (int)ListenerBindRetryDelay.TotalMilliseconds });
                await Task.Delay(ListenerBindRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (tcp is not null) await tcp.DisposeAsync().ConfigureAwait(false);
                if (quic is not null) await quic.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static AuthDecision Authorize(InstalledFolderHost.FolderRuntime folder, byte[] deviceId,
        Action<byte[], byte[]> recordPending)
    {
        DeviceRow? row = folder.Index.Devices.Find(deviceId);
        if (row?.Approval == DeviceApproval.Blocked || row is { Approval: DeviceApproval.Approved, Pinned: false })
            return AuthDecision.Denied;
        if (row is { Approval: DeviceApproval.Approved, Pinned: true }) return AuthDecision.Allowed;
        if (!folder.Settings.RequireApproval) return AuthDecision.Allowed;
        recordPending(folder.Keys.FolderId(), deviceId);
        return AuthDecision.Pending;
    }

    private bool IsDevicePinned(byte[] deviceId) => getFolders().Any(folder =>
    {
        DeviceRow? row = folder.Index.Devices.Find(deviceId);
        // Device identity is certificate-bound; folder membership and its approval policy
        // are checked only after the folder proof. Unknown peers must reach that decision
        // so RequireApproval can report Pending rather than making approval impossible.
        return row?.Approval != DeviceApproval.Blocked && row is not { Approval: DeviceApproval.Approved, Pinned: false };
    });

    private async Task AcceptQuicLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QuicPeerSession session;
                try { session = await listener!.AcceptAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                    (error is AuthenticationException or OperationCanceledException or InvalidDataException
                     || error is IOException and not QuicException
                     || error is QuicException { QuicError: QuicError.StreamAborted or QuicError.ConnectionAborted
                         or QuicError.ConnectionIdle or QuicError.ConnectionTimeout }))
                {
                    // AcceptAsync has already disposed the accepted connection. A
                    // peer can reset its control stream during Hello; reject that
                    // connection without faulting the long-lived listener.
                    diagnostics?.TryWrite(new("service.incoming_peer_rejected", "peer_network")
                        { Level = DiagnosticLevel.Debug, Transport = DiagnosticTransport.Quic,
                            ErrorCode = error.GetType().Name, HResult = error.HResult });
                    continue;
                }
                TrackSession(session);
                try { await connections.RegisterIncomingAsync(ManagedPeerConnection.FromQuic(session), cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException
                    or ObjectDisposedException)
                {
                    diagnostics?.TryWrite(new("service.incoming_peer_rejected", "peer_network")
                        { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name, HResult = error.HResult });
                    try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (QuicException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.peer_accept_failed", "peer_network")
                { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
            throw;
        }
    }

    private async Task AcceptTcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpPeerSession raw = await tcpListener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
                TcpPeerProtocolSession session;
                try
                {
                    session = await TcpPeerProtocolSession.StartAsync(raw, tcpProtocolOptions,
                        initiator: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException
                    or ObjectDisposedException or InvalidDataException)
                {
                    diagnostics?.TryWrite(new("service.incoming_peer_rejected", "peer_network")
                        { Level = DiagnosticLevel.Debug, Transport = DiagnosticTransport.Tcp,
                            ErrorCode = error.GetType().Name, HResult = error.HResult });
                    try { await raw.DisposeAsync().ConfigureAwait(false); } catch { }
                    continue;
                }
                TrackSession(session.SessionId, session, session.Closed);
                try { await connections.RegisterIncomingAsync(ManagedPeerConnection.FromTcp(session), cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException
                    or ObjectDisposedException)
                {
                    diagnostics?.TryWrite(new("service.incoming_peer_rejected", "peer_network")
                        { Level = DiagnosticLevel.Debug, Transport = DiagnosticTransport.Tcp,
                            ErrorCode = error.GetType().Name, HResult = error.HResult });
                    try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.peer_accept_failed", "peer_network")
                { Level = DiagnosticLevel.Error, Transport = DiagnosticTransport.Tcp,
                    ErrorCode = error.GetType().Name, HResult = error.HResult });
            throw;
        }
    }

    private void OnLanPeerDiscovered(DiscoveredLanPeer peer)
    {
        if (peer.DeviceId.AsSpan().SequenceEqual(localDeviceId)) return;
        var endpoint = new IPEndPoint(peer.Address, peer.Port);
        string id = Convert.ToHexString(peer.DeviceId);
        RememberEndpoint(id, endpoint, ConnectionSource.Lan);
        diagnostics?.TryWrite(new("service.lan_peer_discovered", "peer_network")
            { Transport = DiagnosticTransport.Quic, RemoteAddress = peer.Address });
        EnsureMaintainer(id, peer.DeviceId);
    }

    private Announce CreateAnnouncement(int port)
    {
        var announce = new Announce { ListenPort = (uint)port };
        foreach (InstalledFolderHost.FolderRuntime folder in getFolders().Where(item => item.Settings.UseTracker).Take(500))
            announce.FolderIds.Add(ByteString.CopyFrom(folder.Keys.FolderId()));
        try
        {
            foreach (IPAddress address in NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(IsDialableUnicast)
                .DistinctBy(address => address.ToString()).Take(4))
                announce.LocalEndpoints.Add(new RendezvousEndpoint { Address = address.ToString(), Port = (uint)port });
        }
        catch (NetworkInformationException error)
        {
            diagnostics?.TryWrite(new("tracker.local_addresses_unavailable", "rendezvous")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
        if (portMapper is not null)
            foreach (IPEndPoint endpoint in portMapper.PublicEndpoints.Where(endpoint =>
                IsDialableUnicast(endpoint.Address)).Take(4))
                announce.LocalEndpoints.Add(new RendezvousEndpoint
                { Address = endpoint.Address.ToString(), Port = (uint)endpoint.Port });
        return announce;
    }

    private void RefreshTrackerAnnouncements()
    {
        foreach (TrackerClient tracker in trackers) tracker.Refresh();
    }

    private ValueTask OnTrackerMessageAsync(Uri server, ServerMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (message.PayloadCase)
        {
            case ServerMessage.PayloadOneofCase.ObservedAddress:
                diagnostics?.TryWrite(new("tracker.observed_address", "rendezvous")
                { Transport = DiagnosticTransport.Http2,
                    SessionId = message.ObservedAddress.SessionId.Length == 16
                        ? new Guid(message.ObservedAddress.SessionId.Span) : null,
                    RemoteAddress = IPAddress.TryParse(message.ObservedAddress.Endpoint.Address, out IPAddress? observed)
                        ? observed : null,
                    RemotePort = (int)message.ObservedAddress.Endpoint.Port });
                break;
            case ServerMessage.PayloadOneofCase.PeerList:
                if (!getFolders().Any(folder => folder.Settings.UseTracker
                    && folder.Keys.FolderId().AsSpan().SequenceEqual(message.PeerList.FolderId.Span))) break;
                bool relayEnabled = useRelay && getFolders().Any(folder => folder.Settings.UseTracker
                    && folder.Settings.UseRelay
                    && folder.Keys.FolderId().AsSpan().SequenceEqual(message.PeerList.FolderId.Span));
                var present = new HashSet<string>(StringComparer.Ordinal);
                var discovered = new List<(string Key, byte[] DeviceId, bool HasDirectEndpoint)>();
                foreach (Peer peer in message.PeerList.Peers.Take(1024))
                {
                    if (peer.DeviceId.Length != 32 || peer.DeviceId.Span.SequenceEqual(localDeviceId)) continue;
                    string key = Convert.ToHexString(peer.DeviceId.Span);
                    present.Add(key);
                    int accepted = 0;
                    foreach (RendezvousEndpoint endpoint in peer.Endpoints.Take(16))
                    {
                        if (!IPAddress.TryParse(endpoint.Address, out IPAddress? address)
                            || endpoint.Port is < 1 or > 65535 || !IsDialableUnicast(address)) continue;
                        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                        RememberEndpoint(key, new IPEndPoint(address, (int)endpoint.Port), ConnectionSource.Public);
                        accepted++;
                    }
                    discovered.Add((key, peer.DeviceId.ToByteArray(), accepted != 0));
                }
                relayPresence.Update(server, message.PeerList.FolderId.Span, present,
                    relayEnabled, timeProvider.GetUtcNow());
                foreach (var peer in discovered)
                    if (peer.HasDirectEndpoint || relayEnabled)
                        EnsureMaintainer(peer.Key, peer.DeviceId);
                diagnostics?.TryWrite(new("tracker.peer_list_received", "rendezvous")
                { ItemsVisited = message.PeerList.Peers.Count });
                break;
            case ServerMessage.PayloadOneofCase.PunchNow:
                diagnostics?.TryWrite(new("tracker.punch_introduction_received", "rendezvous")
                { ItemsVisited = message.PunchNow.Endpoints.Count });
                break;
            case ServerMessage.PayloadOneofCase.RelayOffer:
                diagnostics?.TryWrite(new("tracker.relay_offer_received", "rendezvous")
                { SessionId = message.RelayOffer.SessionId.Length == 16
                    ? new Guid(message.RelayOffer.SessionId.Span) : null });
                break;
        }
        return ValueTask.CompletedTask;
    }

    internal static bool IsDialableUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return address.GetAddressBytes()[0] is > 0 and < 224;
        return !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal;
    }

    private ValueTask OnTrackerMessageFromServerAsync(Uri server, ServerMessage message,
        CancellationToken cancellationToken)
    {
        ValueTask result = OnTrackerMessageAsync(server, message, cancellationToken);
        if (message.PayloadCase != ServerMessage.PayloadOneofCase.RelayOffer
            || !useRelay || relayRegistrations.Count == 0) return result;
        RelayOffer offer = message.RelayOffer;
        if (offer.PeerDeviceId.Length != 32 || offer.Token.Length != 32 || offer.SessionId.Length != 16
            || offer.PeerDeviceId.Span.SequenceEqual(localDeviceId)) return result;
        string key = RelayOfferKey(server, offer.PeerDeviceId.Span);
        if (offer.Initiator)
        {
            if (pendingRelayOffers.TryGetValue(key, out TaskCompletionSource<RelayOffer>? pending))
                pending.TrySetResult(offer.Clone());
            return result;
        }
        Guid id = new(offer.SessionId.Span);
        if (relayAcceptWorkers.Count >= 16 || relayAcceptWorkers.ContainsKey(id)) return result;
        Task worker = AcceptRelayAsync(server, offer.Clone(), stopping.Token);
        if (!relayAcceptWorkers.TryAdd(id, worker)) return result;
        _ = worker.ContinueWith(completed =>
        {
            relayAcceptWorkers.TryRemove(new KeyValuePair<Guid, Task>(id, completed));
            if (completed.IsFaulted)
                diagnostics?.TryWrite(new("service.relay_accept_failed", "relay")
                { Level = DiagnosticLevel.Warning, SessionId = id,
                    ErrorCode = completed.Exception?.GetBaseException().GetType().Name,
                    HResult = completed.Exception?.GetBaseException().HResult });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return result;
    }

    private async Task AcceptRelayAsync(Uri server, RelayOffer offer, CancellationToken cancellationToken)
    {
        if (!useRelay || relayRegistrations.Count == 0) return;
        if (connections.TryGetConnected(offer.PeerDeviceId.Span, out var current) && current is not null) return;
        RelayTransport transport = await RelayClient.ConnectAsync(server, offer, certificate, IsDevicePinned,
            cancellationToken).ConfigureAwait(false);
        RelayPeerProtocolSession session = await RelayPeerProtocolSession.StartAsync(transport, options.CreateHello,
            relayRegistrations, diagnostics, options.AddStrike, options.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        TrackSession(session.SessionId, session, session.Closed);
        try { await connections.RegisterIncomingAsync(new ManagedPeerConnection(session.SessionId,
            session.LocalDeviceId, session.RemoteDeviceId, false, session, session.Closed), cancellationToken)
                .ConfigureAwait(false); }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask<ManagedPeerConnection> DialRelayAsync(TrackerClient tracker, Uri server,
        byte[] peerId, byte[] folderId, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        string key = RelayOfferKey(server, peerId);
        var completion = new TaskCompletionSource<RelayOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRelayOffers.TryAdd(key, completion)) throw new IOException("A relay offer is already pending.");
        try
        {
            if (!tracker.RequestRelay(peerId, folderId)) throw new IOException("Tracker is not connected.");
            RelayOffer offer = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            RelayTransport transport = await RelayClient.ConnectAsync(server, offer, certificate,
                IsDevicePinned, cancellationToken).ConfigureAwait(false);
            RelayPeerProtocolSession session = await RelayPeerProtocolSession.StartAsync(transport,
                options.CreateHello, relayRegistrations, diagnostics, options.AddStrike, options.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            TrackSession(session.SessionId, session, session.Closed);
            return new ManagedPeerConnection(session.SessionId, session.LocalDeviceId, session.RemoteDeviceId,
                true, session, session.Closed);
        }
        finally { pendingRelayOffers.TryRemove(key, out _); }
    }

    private static string RelayOfferKey(Uri server, ReadOnlySpan<byte> peerId)
        => server.AbsoluteUri + "|" + Convert.ToHexString(peerId);

    private void EnsureMaintainer(string key, byte[] deviceId)
    {
        Task task;
        lock (maintainerGate)
        {
            if (maintainers.TryGetValue(key, out Task? existing) && !existing.IsCompleted) return;
            task = connections.MaintainConnectionAsync(deviceId, token =>
            {
                token.ThrowIfCancellationRequested();
                var candidates = endpoints.Snapshot(key).SelectMany(CreateTransportCandidates).ToList();
                if (useRelay)
                    foreach (TrackerClient tracker in trackers.Where(item => item.IsConnected))
                    {
                        Uri server = tracker.Server;
                        if (relayPresence.TryGetFolder(server, key, folderKey =>
                            getFolders().Any(folder => folder.Settings.UseTracker && folder.Settings.UseRelay
                                && Convert.ToHexString(folder.Keys.FolderId()) == folderKey),
                            timeProvider.GetUtcNow(), out byte[]? folderId))
                        {
                            candidates.Add(new ConnectionCandidate(ConnectionSource.Relay,
                                token => DialRelayAsync(tracker, server, deviceId, folderId!, token)));
                        }
                    }
                return ValueTask.FromResult<IReadOnlyList<ConnectionCandidate>>(candidates);
            }, stopping.Token);
            maintainers[key] = task;
        }
        _ = task.ContinueWith(completed =>
        {
            lock (maintainerGate)
                if (maintainers.TryGetValue(key, out Task? current) && ReferenceEquals(current, completed))
                    maintainers.TryRemove(key, out _);
            if (completed.IsFaulted)
                diagnostics?.TryWrite(new("service.peer_reconnect_stopped", "peer_network")
                    { Level = DiagnosticLevel.Error, ErrorCode = completed.Exception?.GetBaseException().GetType().Name });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void RequestNetworkRecovery(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (networkRecoveryGate)
        {
            if (Volatile.Read(ref disposed) != 0 || stopping.IsCancellationRequested) return;
            if (networkRecoveryRequested || networkRecoveryTask is { IsCompleted: false })
                coalescedNetworkRecoveryRequests++;
            networkRecoveryRequested = true;
            pendingNetworkRecoveryReason = reason;
            if (networkRecoveryTask is null || networkRecoveryTask.IsCompleted)
                networkRecoveryTask = Task.Run(RecoverNetworkLoopAsync);
        }
    }

    internal Task RefreshPredefinedHostsAsync(CancellationToken cancellationToken = default)
        => (predefinedHosts ?? throw new InvalidOperationException("Predefined host resolver is not running."))
            .RefreshNowAsync(cancellationToken);

    internal async Task<bool> RefreshFolderPolicyAsync(DeviceSettings deviceSettings, CancellationToken cancellationToken)
    {
        bool lanAvailable = await RefreshLanDiscoveryAsync(deviceSettings, cancellationToken).ConfigureAwait(false);
        foreach (TrackerClient tracker in trackers) tracker.Refresh();
        await RefreshPredefinedHostsAsync(cancellationToken).ConfigureAwait(false);
        RequestNetworkRecovery("folder_policy_change");
        return lanAvailable;
    }

    private async Task<bool> RefreshLanDiscoveryAsync(DeviceSettings deviceSettings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool wanted = deviceSettings.LanDiscovery && getFolders().Any(folder => folder.Settings.UseLan);
        if (!wanted)
        {
            LanDiscovery? old = discovery;
            discovery = null;
            if (old is not null)
            {
                old.PeerDiscovered -= OnLanPeerDiscovered;
                await old.DisposeAsync().ConfigureAwait(false);
            }
            return true;
        }
        if (discovery is not null) return true;
        var next = new LanDiscovery(localDeviceId, deviceSettings.ListenPort,
            () => getFolders().Where(folder => folder.Settings.UseLan)
                .Select(folder => (ReadOnlyMemory<byte>)folder.Keys.FolderId()).ToArray(), diagnostics, timeProvider);
        try
        {
            next.PeerDiscovered += OnLanPeerDiscovered;
            next.Start();
            discovery = next;
            return true;
        }
        catch (Exception error) when (error is SocketException or InvalidOperationException or PlatformNotSupportedException)
        {
            next.PeerDiscovered -= OnLanPeerDiscovered;
            await next.DisposeAsync().ConfigureAwait(false);
            diagnostics?.TryWrite(new("service.lan_discovery_unavailable", "peer_network")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            return false;
        }
    }

    internal async Task RefreshFolderMembershipAsync(CancellationToken cancellationToken)
    {
        foreach (TrackerClient tracker in trackers) tracker.Refresh();
        foreach (var session in sessions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await session.Value.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("service.peer_session_reset_failed", "folder_membership")
                    { Level = DiagnosticLevel.Warning, SessionId = session.Key,
                        ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
        }
        try { await Task.WhenAll(sessionWorkers.Values.ToArray()).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            diagnostics?.TryWrite(new("service.peer_session_worker_failed", "folder_membership")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
        RequestNetworkRecovery("folder_membership_change");
    }

    internal void NotifyFolderMembershipExpanded()
    {
        foreach (TrackerClient tracker in trackers) tracker.Refresh();
        RequestNetworkRecovery("folder_membership_change");
    }

    private async Task RecoverNetworkLoopAsync()
    {
        while (true)
        {
            string reason;
            int coalescedRequests;
            lock (networkRecoveryGate)
            {
                if (!networkRecoveryRequested || stopping.IsCancellationRequested)
                {
                    networkRecoveryTask = null;
                    return;
                }
                reason = pendingNetworkRecoveryReason ?? "network_change";
                pendingNetworkRecoveryReason = null;
                networkRecoveryRequested = false;
                coalescedRequests = coalescedNetworkRecoveryRequests;
                coalescedNetworkRecoveryRequests = 0;
            }
            await RecoverNetworkAsync(reason, coalescedRequests).ConfigureAwait(false);
        }
    }

    private async Task RecoverNetworkAsync(string reason, int coalescedRequests)
    {
        CancellationToken token = stopping.Token;
        var recoveryTimer = Stopwatch.StartNew();
        try
        {
            diagnostics?.TryWrite(new("service.network_recovery_started", reason)
                { ItemsSkipped = coalescedRequests });
            // Coalesce address/availability callbacks and let LAN announcements
            // refresh endpoints before the immediate reconnect pass.
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token).ConfigureAwait(false);
            KeyValuePair<Guid, IAsyncDisposable>[] activeSessions = sessions.ToArray();
            await ResetActiveSessionsAsync(activeSessions, reason, diagnostics).ConfigureAwait(false);

            string[] knownPeers = endpoints.KnownPeerKeys();
            foreach (string peerKey in knownPeers)
            {
                token.ThrowIfCancellationRequested();
                EnsureMaintainer(peerKey, Convert.FromHexString(peerKey));
            }
            diagnostics?.TryWrite(new("service.network_recovery_complete", reason)
                { ItemsVisited = knownPeers.Length, ItemsSkipped = coalescedRequests,
                    ElapsedMilliseconds = recoveryTimer.Elapsed.TotalMilliseconds });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.network_recovery_failed", reason)
                { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult,
                    ElapsedMilliseconds = recoveryTimer.Elapsed.TotalMilliseconds });
        }
    }

    internal static async Task ResetActiveSessionsAsync(
        IReadOnlyList<KeyValuePair<Guid, IAsyncDisposable>> activeSessions,
        string reason, DiagnosticJournal? diagnostics)
    {
        // Separate sessions can each wait for a stream shutdown. Closing them
        // one after another makes network recovery exceed the physical 30 s gate.
        var pending = new Task[activeSessions.Count];
        for (int index = 0; index < activeSessions.Count; index++)
        {
            KeyValuePair<Guid, IAsyncDisposable> active = activeSessions[index];
            pending[index] = ResetOneAsync(active, reason, diagnostics);
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task ResetOneAsync(KeyValuePair<Guid, IAsyncDisposable> active,
        string reason, DiagnosticJournal? diagnostics)
    {
        var timer = Stopwatch.StartNew();
        diagnostics?.TryWrite(new("service.peer_session_reset", "network_recovery")
            { SessionId = active.Key, ErrorCode = reason });
        try
        {
            await active.Value.DisposeAsync().ConfigureAwait(false);
            diagnostics?.TryWrite(new("service.peer_session_reset_complete", "network_recovery")
                { SessionId = active.Key, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
        }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.peer_session_reset_failed", "network_recovery")
                { Level = DiagnosticLevel.Warning, SessionId = active.Key,
                    ErrorCode = error.GetType().Name, HResult = error.HResult,
                    ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
        }
    }

    private ValueTask DialPredefinedHostAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        => DialEndpointAsync(endpoint, ConnectionSource.Public, cancellationToken);

    private async ValueTask DialEndpointAsync(IPEndPoint endpoint, ConnectionSource source, CancellationToken cancellationToken)
    {
        if (TryFindConnectedEndpoint(endpoint, out string? existingKey, out byte[]? existingPeer))
        {
            EnsureMaintainer(existingKey!, existingPeer!);
            return;
        }

        ManagedPeerConnection initial = await connections.ConnectAnyAsync(
        [new(source, DiagnosticTransport.Quic, endpoint, async candidateToken =>
            {
                QuicPeerSession session = await QuicPeerSession.ConnectAsync(endpoint, options, cancellationToken: candidateToken).ConfigureAwait(false);
                TrackSession(session);
                return ManagedPeerConnection.FromQuic(session);
            }),
         new(source, DiagnosticTransport.Tcp, endpoint, async candidateToken =>
            {
                TcpPeerSession raw = await tcpListener!.ConnectAsync(endpoint, candidateToken).ConfigureAwait(false);
                try
                {
                    TcpPeerProtocolSession session = await TcpPeerProtocolSession.StartAsync(raw, tcpProtocolOptions,
                        initiator: true, candidateToken).ConfigureAwait(false);
                    TrackSession(session.SessionId, session, session.Closed);
                    return ManagedPeerConnection.FromTcp(session);
                }
                catch { await raw.DisposeAsync().ConfigureAwait(false); throw; }
            })], cancellationToken).ConfigureAwait(false);

        byte[] peerId = initial.RemoteDeviceId;
        string key = Convert.ToHexString(peerId);
        RememberEndpoint(key, endpoint, source);
        EnsureMaintainer(key, peerId);
    }

    private bool TryFindConnectedEndpoint(IPEndPoint endpoint, out string? peerKey, out byte[]? peerId)
    {
        foreach (string knownPeer in endpoints.KnownPeerKeys())
        {
            if (!endpoints.Contains(knownPeer, endpoint)) continue;
            byte[] id;
            try { id = Convert.FromHexString(knownPeer); }
            catch (FormatException) { continue; }
            if (!connections.TryGetConnected(id, out var current) || current is null) continue;
            peerKey = knownPeer; peerId = id; return true;
        }
        peerKey = null; peerId = null; return false;
    }

    private void RememberEndpoint(string peerKey, IPEndPoint endpoint, ConnectionSource source)
    {
        endpoints.Remember(peerKey, endpoint, source);
    }

    private IEnumerable<ConnectionCandidate> CreateTransportCandidates(PeerEndpointCatalog.PeerEndpoint peerEndpoint)
    {
        IPEndPoint endpoint = peerEndpoint.Address;
        ConnectionSource source = peerEndpoint.Source;
        return
        [new(source, DiagnosticTransport.Quic, endpoint, async candidateToken =>
            {
                QuicPeerSession session = await QuicPeerSession.ConnectAsync(endpoint, options,
                    cancellationToken: candidateToken).ConfigureAwait(false);
                TrackSession(session);
                return ManagedPeerConnection.FromQuic(session);
            }),
         new(source, DiagnosticTransport.Tcp, endpoint, async candidateToken =>
            {
                TcpPeerSession raw = await tcpListener!.ConnectAsync(endpoint, candidateToken).ConfigureAwait(false);
                try
                {
                    TcpPeerProtocolSession session = await TcpPeerProtocolSession.StartAsync(raw,
                        tcpProtocolOptions, initiator: true, candidateToken).ConfigureAwait(false);
                    TrackSession(session.SessionId, session, session.Closed);
                    return ManagedPeerConnection.FromTcp(session);
                }
                catch { await raw.DisposeAsync().ConfigureAwait(false); throw; }
            })];
    }

    public ValueTask ConnectToPeerForTestAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        => DialPredefinedHostAsync(endpoint, cancellationToken);

    internal void SetPeerEndpointsForTest(byte[] deviceId,
        IReadOnlyList<(IPEndPoint Address, ConnectionSource Source)> peerEndpoints)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(peerEndpoints);
        string key = Convert.ToHexString(deviceId);
        foreach ((IPEndPoint address, ConnectionSource source) in peerEndpoints)
            RememberEndpoint(key, address, source);
        EnsureMaintainer(key, deviceId);
    }

    public async ValueTask ConnectToPeerOverTcpForTestAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        TcpPeerSession raw = await tcpListener!.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        TcpPeerProtocolSession session;
        try
        {
            session = await TcpPeerProtocolSession.StartAsync(raw, tcpProtocolOptions,
                initiator: true, cancellationToken).ConfigureAwait(false);
        }
        catch { await raw.DisposeAsync().ConfigureAwait(false); throw; }
        TrackSession(session.SessionId, session, session.Closed);
        byte[] peerId = session.RemoteDeviceId;
        string key = Convert.ToHexString(peerId);
        RememberEndpoint(key, endpoint, ConnectionSource.Lan);
        ManagedPeerConnection? unclaimed = ManagedPeerConnection.FromTcp(session);
        try
        {
            await connections.ConnectAsync(peerId,
                [new(ConnectionSource.Lan, _ => ValueTask.FromResult(
                    Interlocked.Exchange(ref unclaimed, null)
                    ?? throw new IOException("Preconnected TCP session was already claimed.")))], cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (unclaimed is not null) await unclaimed.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (unclaimed is not null) await unclaimed.DisposeAsync().ConfigureAwait(false);
        EnsureMaintainer(key, peerId);
    }

    private void OnAuthenticated(ManagedPeerConnection connection)
    {
        if (!sessions.ContainsKey(connection.SessionId)) return;
        (IReadOnlyList<AuthenticatedFolder> folders, DiagnosticTransport transport) = connection.Session switch
        {
            QuicPeerSession session => (session.AuthenticatedFolders, DiagnosticTransport.Quic),
            TcpPeerProtocolSession session => (session.AuthenticatedFolders, DiagnosticTransport.Tcp),
            RelayPeerProtocolSession session => (session.AuthenticatedFolders, DiagnosticTransport.Relay),
            _ => throw new InvalidOperationException("Unsupported authenticated peer session type."),
        };
        Task worker = SuperviseSessionAsync(connection, folders, transport, stopping.Token);
        if (!sessionWorkers.TryAdd(connection.SessionId, worker)) return;
        _ = worker.ContinueWith(completed =>
        {
            sessionWorkers.TryRemove(new KeyValuePair<Guid, Task>(connection.SessionId, completed));
            if (completed.IsFaulted)
                diagnostics?.TryWrite(new("service.peer_session_failed", "peer_network")
                    { Level = DiagnosticLevel.Warning, SessionId = connection.SessionId,
                        ErrorCode = completed.Exception?.GetBaseException().GetType().Name,
                        HResult = completed.Exception?.GetBaseException().HResult });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void TrackSession(QuicPeerSession session)
        => TrackSession(session.SessionId, session, session.Closed);

    private void TrackSession(Guid sessionId, IAsyncDisposable session, Task closed)
    {
        if (!sessions.TryAdd(sessionId, session)) throw new InvalidOperationException("Duplicate peer session identifier.");
        _ = closed.ContinueWith(_ => sessions.TryRemove(new KeyValuePair<Guid, IAsyncDisposable>(sessionId, session)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record PeerEndpoint(IPEndPoint Address, ConnectionSource Source);

    private async Task SuperviseSessionAsync(ManagedPeerConnection managed, IReadOnlyList<AuthenticatedFolder> authenticatedFolders,
        DiagnosticTransport transport,
        CancellationToken cancellationToken)
    {
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var folderConnections = new List<PeerFolderConnection>();
        var workers = new List<Task>();
        try
        {
            foreach (AuthenticatedFolder authenticated in authenticatedFolders)
            {
                InstalledFolderHost.FolderRuntime? folder = getFolders().FirstOrDefault(item =>
                    item.Keys.FolderId().AsSpan().SequenceEqual(authenticated.FolderId));
                if (folder is null || authenticated.Transport is null) continue;
                PeerFolderConnection connection = await folder.Transport.ConnectAsync(authenticated.Transport, sessionStop.Token).ConfigureAwait(false);
                folderConnections.Add(connection);
                status.SetPeerSessionActive(managed.SessionId, true);
                Guid sessionId = connection.SharedSessionId ?? managed.SessionId;
                workers.Add(SynchronizeFolderAsync(folder, connection, transport, sessionId, sessionStop.Token));
                diagnostics?.TryWrite(new("service.folder_peer_ready", "peer_network")
                    { SessionId = sessionId, Transport = transport });
            }
            if (folderConnections.Count == 0)
                diagnostics?.TryWrite(new("service.peer_no_common_approved_folders", "peer_network")
                    { Level = DiagnosticLevel.Information, SessionId = managed.SessionId, Transport = transport });
            else status.SetPeerTransport(managed.SessionId, managed.RemoteDeviceId, transport);
            await managed.Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // A failed folder handshake (including clock admission) invalidates this
            // authenticated session. Closing it lets the manager perform a fresh Hello.
            await managed.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await sessionStop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
            foreach (PeerFolderConnection connection in folderConnections)
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
            if (folderConnections.Count != 0) status.SetPeerSessionActive(managed.SessionId, false);
            status.ClearPeerTransport(managed.SessionId);
        }
    }

    private async Task SynchronizeFolderAsync(InstalledFolderHost.FolderRuntime folder,
        PeerFolderConnection peer, DiagnosticTransport transport, Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    PeerIndexSyncResult result = await peer.SynchronizeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    PullPassResult pull = await folder.Transport.RunPassAsync(cancellationToken).ConfigureAwait(false);
                    diagnostics?.TryWrite(new("service.folder_sync_pass", "peer_network")
                        { SessionId = sessionId, Transport = transport, ItemsVisited = result.Pages, ItemsChanged = pull.Applied,
                            ItemsDeferred = pull.Deferred + pull.Failed });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    diagnostics?.TryWrite(new("service.folder_sync_failed", "peer_network")
                        { SessionId = sessionId, Transport = transport, Level = DiagnosticLevel.Warning,
                            ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
                await Task.Delay(SyncInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        connections.Authenticated -= OnAuthenticated;
        await stopping.CancelAsync().ConfigureAwait(false);
        Task? recovery;
        lock (networkRecoveryGate) recovery = networkRecoveryTask;
        if (recovery is not null)
            try { await recovery.ConfigureAwait(false); } catch { }
        if (predefinedHosts is not null) await predefinedHosts.DisposeAsync().ConfigureAwait(false);
        foreach (TrackerClient tracker in trackers) await tracker.DisposeAsync().ConfigureAwait(false);
        if (portMapper is not null)
        {
            portMapper.MappingsChanged -= RefreshTrackerAnnouncements;
            await portMapper.DisposeAsync().ConfigureAwait(false);
        }
        if (discovery is not null)
        {
            discovery.PeerDiscovered -= OnLanPeerDiscovered;
            await discovery.DisposeAsync().ConfigureAwait(false);
        }
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        if (tcpListener is not null) await tcpListener.DisposeAsync().ConfigureAwait(false);
        Exception? acceptFailure = null;
        if (acceptTask is not null)
            try { await acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { acceptFailure = error; }
        await connections.DisposeAsync().ConfigureAwait(false);
        Task[] workers = sessionWorkers.Values.Concat(maintainers.Values).Concat(relayAcceptWorkers.Values).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        foreach (IAsyncDisposable session in sessions.Values)
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        CryptographicOperations.ZeroMemory(localDeviceId);
        stopping.Dispose();
        diagnostics?.TryWrite(new("service.peer_network_stopped", "shutdown"));
        if (acceptFailure is not null) ExceptionDispatchInfo.Capture(acceptFailure).Throw();
    }

    private sealed class RegistrationList(Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders,
        Action<byte[], byte[]> recordPending)
        : IReadOnlyList<PeerFolderRegistration>
    {
        public int Count => getFolders().Count;
        public PeerFolderRegistration this[int index]
        {
            get
            {
                InstalledFolderHost.FolderRuntime folder = getFolders()[index];
                return new(folder.Keys, peer => Authorize(folder, peer, recordPending), peer => folder.Transport.CreateHandler(peer));
            }
        }
        public IEnumerator<PeerFolderRegistration> GetEnumerator()
        {
            foreach (InstalledFolderHost.FolderRuntime folder in getFolders())
                yield return new(folder.Keys, peer => Authorize(folder, peer, recordPending), peer => folder.Transport.CreateHandler(peer));
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
