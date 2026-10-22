using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
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
using PeerSync.Net.Security;
using PeerSync.Net.Tcp;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using PeerSync.Service.Control;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Service.Hosting;

/// <summary>Owns the installed service's authenticated peer listener, discovery, reconnects and folder passes.</summary>
internal sealed class PeerNetworkRuntime : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    private readonly X509Certificate2 certificate;
    private readonly byte[] localDeviceId;
    private readonly Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders;
    private readonly DiagnosticJournal? diagnostics;
    private readonly ControlStatusRuntime status;
    private readonly TimeProvider timeProvider;
    private readonly QuicPeerOptions options;
    private readonly TcpPeerProtocolOptions tcpProtocolOptions;
    private readonly TcpPeerOptions tcpOptions;
    private readonly ConnectionManager connections;
    private readonly CancellationTokenSource stopping = new();
    private readonly PeerEndpointCatalog endpoints;
    private readonly ConcurrentDictionary<string, Task> maintainers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> sessionWorkers = new();
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
    private Task? acceptTask;
    private int disposed;

    public Task Completion => acceptTask ?? Task.CompletedTask;

    private PeerNetworkRuntime(X509Certificate2 certificate, byte[] localDeviceId,
        Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders, ControlStatusRuntime status,
        DiagnosticJournal? diagnostics, TimeProvider? timeProvider)
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
            Folders = new RegistrationList(getFolders),
            IsDevicePinned = IsDevicePinned,
            Diagnostics = diagnostics,
            DeviceName = Environment.MachineName,
        };
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
        DiagnosticJournal? diagnostics, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        var runtime = new PeerNetworkRuntime(certificate, deviceId, getFolders, status, diagnostics, timeProvider);
        try
        {
            if (!QuicListener.IsSupported)
            {
                diagnostics?.TryWrite(new("service.quic_unavailable", "peer_network")
                    { Level = DiagnosticLevel.Error, ErrorCode = "platform_not_supported" });
                throw new PlatformNotSupportedException("The installed service requires Windows QUIC support.");
            }

            runtime.listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Any, deviceSettings.ListenPort),
                runtime.options, cancellationToken).ConfigureAwait(false);
            runtime.tcpListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Any, deviceSettings.ListenPort), runtime.tcpOptions);
            runtime.acceptTask = Task.WhenAll(runtime.AcceptQuicLoopAsync(runtime.stopping.Token),
                runtime.AcceptTcpLoopAsync(runtime.stopping.Token));
            diagnostics?.TryWrite(new("service.quic_listener_ready", "peer_network")
                { Transport = DiagnosticTransport.Quic, ItemsChanged = deviceSettings.ListenPort });
            diagnostics?.TryWrite(new("service.tcp_listener_ready", "peer_network")
                { Transport = DiagnosticTransport.Tcp, ItemsChanged = deviceSettings.ListenPort });

            if (deviceSettings.LanDiscovery && getFolders().Any(folder => folder.Settings.UseLan))
            {
                try
                {
                    runtime.discovery = new LanDiscovery(deviceId, deviceSettings.ListenPort,
                        () => getFolders().Where(folder => folder.Settings.UseLan)
                            .Select(folder => (ReadOnlyMemory<byte>)folder.Keys.FolderId()).ToArray(), diagnostics);
                    runtime.discovery.PeerDiscovered += runtime.OnLanPeerDiscovered;
                    runtime.discovery.Start();
                }
                catch (Exception error) when (error is SocketException or InvalidOperationException or PlatformNotSupportedException)
                {
                    diagnostics?.TryWrite(new("service.lan_discovery_unavailable", "peer_network")
                        { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
            }

            runtime.predefinedHosts = new PredefinedHosts(() => getFolders().SelectMany(folder => folder.Settings.PredefinedHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), runtime.DialPredefinedHostAsync, diagnostics);
            runtime.predefinedHosts.Start();
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static AuthDecision Authorize(InstalledFolderHost.FolderRuntime folder, byte[] deviceId)
    {
        DeviceRow? row = folder.Index.Devices.Find(deviceId);
        if (row?.Approval == DeviceApproval.Blocked || row is { Approval: DeviceApproval.Approved, Pinned: false })
            return AuthDecision.Denied;
        if (row is { Approval: DeviceApproval.Approved, Pinned: true }) return AuthDecision.Allowed;
        return folder.Settings.RequireApproval ? AuthDecision.Pending : AuthDecision.Allowed;
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
                QuicPeerSession session = await listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
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

    private void EnsureMaintainer(string key, byte[] deviceId)
    {
        Task task;
        lock (maintainerGate)
        {
            if (maintainers.TryGetValue(key, out Task? existing) && !existing.IsCompleted) return;
            task = connections.MaintainConnectionAsync(deviceId, token =>
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<ConnectionCandidate> candidates = endpoints.Snapshot(key)
                    .SelectMany(CreateTransportCandidates).ToArray();
                return ValueTask.FromResult(candidates);
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
        try
        {
            diagnostics?.TryWrite(new("service.network_recovery_started", reason)
                { ItemsSkipped = coalescedRequests });
            // Coalesce address/availability callbacks and let LAN announcements
            // refresh endpoints before the immediate reconnect pass.
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token).ConfigureAwait(false);
            KeyValuePair<Guid, IAsyncDisposable>[] activeSessions = sessions.ToArray();
            foreach (var active in activeSessions)
            {
                diagnostics?.TryWrite(new("service.peer_session_reset", "network_recovery")
                    { SessionId = active.Key, ErrorCode = reason });
                try { await active.Value.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    diagnostics?.TryWrite(new("service.peer_session_reset_failed", "network_recovery")
                        { Level = DiagnosticLevel.Warning, SessionId = active.Key,
                            ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
            }

            string[] knownPeers = endpoints.KnownPeerKeys();
            foreach (string peerKey in knownPeers)
            {
                token.ThrowIfCancellationRequested();
                EnsureMaintainer(peerKey, Convert.FromHexString(peerKey));
            }
            diagnostics?.TryWrite(new("service.network_recovery_complete", reason)
                { ItemsVisited = knownPeers.Length, ItemsSkipped = coalescedRequests });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.network_recovery_failed", reason)
                { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
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
        if (discovery is not null)
        {
            discovery.PeerDiscovered -= OnLanPeerDiscovered;
            await discovery.DisposeAsync().ConfigureAwait(false);
        }
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        if (tcpListener is not null) await tcpListener.DisposeAsync().ConfigureAwait(false);
        if (acceptTask is not null)
            try { await acceptTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        await connections.DisposeAsync().ConfigureAwait(false);
        Task[] workers = sessionWorkers.Values.Concat(maintainers.Values).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        foreach (IAsyncDisposable session in sessions.Values)
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        CryptographicOperations.ZeroMemory(localDeviceId);
        stopping.Dispose();
        diagnostics?.TryWrite(new("service.peer_network_stopped", "shutdown"));
    }

    private sealed class RegistrationList(Func<IReadOnlyList<InstalledFolderHost.FolderRuntime>> getFolders)
        : IReadOnlyList<PeerFolderRegistration>
    {
        public int Count => getFolders().Count;
        public PeerFolderRegistration this[int index]
        {
            get
            {
                InstalledFolderHost.FolderRuntime folder = getFolders()[index];
                return new(folder.Keys, peer => Authorize(folder, peer), peer => folder.Transport.CreateHandler(peer));
            }
        }
        public IEnumerator<PeerFolderRegistration> GetEnumerator()
        {
            foreach (InstalledFolderHost.FolderRuntime folder in getFolders())
                yield return new(folder.Keys, peer => Authorize(folder, peer), peer => folder.Transport.CreateHandler(peer));
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
