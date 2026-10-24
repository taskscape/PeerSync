using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Mono.Nat;
using PeerSync.Core.Diagnostics;
using NatTransport = Mono.Nat.Protocol;

namespace PeerSync.Net.Nat;

/// <summary>Best-effort UPnP/NAT-PMP mappings for the service's TCP and UDP listener.</summary>
internal sealed class PortMapper(int port, DiagnosticJournal? diagnostics = null, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceState> devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> operations = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private Task? renewer;
    private int nextOperation;
    private int disposed;
    private int discoveryStarted;
    internal event Action? MappingsChanged;

    internal IReadOnlyList<IPEndPoint> PublicEndpoints => devices.Values
        .SelectMany(state => state.PublicEndpoints(clock.GetUtcNow()))
        .DistinctBy(endpoint => endpoint.ToString()).ToArray();

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (renewer is not null) return;
        NatUtility.DeviceFound += OnDeviceFound;
        try
        {
            NatUtility.StartDiscovery();
            Volatile.Write(ref discoveryStarted, 1);
            renewer = Task.Run(RenewLoopAsync);
            diagnostics?.TryWrite(new("nat.discovery_started", "port_mapping")
            { Level = DiagnosticLevel.Debug });
        }
        catch (Exception error) when (IsGatewayError(error))
        {
            NatUtility.DeviceFound -= OnDeviceFound;
            try { NatUtility.StopDiscovery(); } catch (Exception cleanup) when (IsGatewayError(cleanup)) { }
            diagnostics?.TryWrite(new("nat.discovery_unavailable", "port_mapping")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
    }

    private void OnDeviceFound(object? sender, DeviceEventArgs eventArgs) => _ = ObserveDeviceAsync(eventArgs.Device);

    // The discovery callback is synchronous. Retain one owned task per device
    // while a mapping attempt is active; repeated gateway announcements do not
    // queue an unbounded series of renewals behind its semaphore.
    internal Task ObserveDeviceAsync(INatDevice device)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.CompletedTask;
        string key = device.NatProtocol + "|" + device.DeviceEndpoint;
        DeviceState state = devices.GetOrAdd(key, _ => new DeviceState(device));
        Task task = state.BeginDiscoveryMap(() => MapDeviceAsync(state, stopping.Token), out bool started);
        if (!started) return task;
        int id = Interlocked.Increment(ref nextOperation);
        operations[id] = task;
        _ = task.ContinueWith(completed =>
        {
            operations.TryRemove(new KeyValuePair<int, Task>(id, completed));
            if (completed.IsFaulted)
                diagnostics?.TryWrite(new("nat.mapping_failed", "port_mapping")
                { Level = DiagnosticLevel.Warning,
                    ErrorCode = completed.Exception?.GetBaseException().GetType().Name });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task RenewLoopAsync()
    {
        try
        {
            // Router-assigned leases can be shorter than the requested hour.
            // Poll a cheap in-memory deadline and contact only gateways whose
            // mappings need renewal; failed protocols get another chance.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await RenewDueAsync(stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                catch (Exception error) when (IsGatewayError(error))
                {
                    diagnostics?.TryWrite(new("nat.renewal_failed", "port_mapping")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    internal Task RenewDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return Task.WhenAll(devices.Values.Where(state => state.NeedsRenew(now))
            .Select(state => MapDeviceAsync(state, cancellationToken)));
    }

    private async Task MapDeviceAsync(DeviceState state, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            bool refreshExternal = state.ExternalNeedsRefresh(now);
            bool renewUdp = state.ProtocolNeedsRenew(NatTransport.Udp, now);
            bool renewTcp = state.ProtocolNeedsRenew(NatTransport.Tcp, now);
            if (!refreshExternal && !renewUdp && !renewTcp) return;
            IPAddress? external = null;
            try { external = await state.Device.GetExternalIPAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsGatewayError(error))
            {
                diagnostics?.TryWrite(new("nat.external_address_unavailable", "port_mapping")
                { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name });
            }
            if (state.SetExternal(external, clock.GetUtcNow()))
                try { MappingsChanged?.Invoke(); } catch { }
            foreach (NatTransport protocol in new[] { NatTransport.Udp, NatTransport.Tcp })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (protocol == NatTransport.Udp ? !renewUdp : !renewTcp) continue;
                try
                {
                    Mapping actual = await state.Device.CreatePortMapAsync(
                        new Mapping(protocol, port, port, 3600, "PeerSync"))
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    int leaseSeconds = actual.Lifetime > 0 ? Math.Min(actual.Lifetime, 3600) : 3600;
                    Mapping? previous = state.Set(protocol, actual, clock.GetUtcNow(), leaseSeconds);
                    try { MappingsChanged?.Invoke(); } catch { }
                    if (previous is not null && previous.PublicPort != actual.PublicPort)
                        try { await state.Device.DeletePortMapAsync(previous)
                            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error) when (IsGatewayError(error))
                        { diagnostics?.TryWrite(new("nat.old_mapping_cleanup_failed", "port_mapping")
                            { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name }); }
                    diagnostics?.TryWrite(new("nat.mapping_active", "port_mapping")
                    { Level = DiagnosticLevel.Debug, Transport = protocol == NatTransport.Udp
                        ? DiagnosticTransport.Quic : DiagnosticTransport.Tcp,
                        RemoteAddress = external, RemotePort = actual.PublicPort });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (IsGatewayError(error))
                {
                    if (state.RecordFailure(protocol, clock.GetUtcNow()))
                        try { MappingsChanged?.Invoke(); } catch { }
                    diagnostics?.TryWrite(new("nat.mapping_refused", "port_mapping")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name,
                        Transport = protocol == NatTransport.Udp ? DiagnosticTransport.Quic : DiagnosticTransport.Tcp });
                }
            }
        }
        finally { state.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        NatUtility.DeviceFound -= OnDeviceFound;
        await stopping.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref discoveryStarted) != 0)
            try { NatUtility.StopDiscovery(); } catch (Exception error) when (IsGatewayError(error)) { }
        if (renewer is not null) try { await renewer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(operations.Values.ToArray()).ConfigureAwait(false); } catch { }
        await Task.WhenAll(devices.Values.Select(RemoveDeviceAsync)).ConfigureAwait(false);
        stopping.Dispose();
        diagnostics?.TryWrite(new("nat.discovery_stopped", "port_mapping")
        { Level = DiagnosticLevel.Debug });
    }

    private static async Task RemoveDeviceAsync(DeviceState state)
    {
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (Mapping mapping in state.Mappings())
                try { await state.Device.DeletePortMapAsync(mapping)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception error) when (IsGatewayError(error)) { }
        }
        finally { state.Gate.Release(); state.Gate.Dispose(); }
    }

    private static bool IsGatewayError(Exception error) => error is MappingException or SocketException
        or IOException or TimeoutException or InvalidOperationException or OperationCanceledException
        or HttpRequestException or PlatformNotSupportedException;

    private sealed class DeviceState(INatDevice device)
    {
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ExternalRefreshInterval = TimeSpan.FromMinutes(30);
        private readonly object gate = new();
        private Mapping? udp;
        private Mapping? tcp;
        private IPAddress? external;
        private DateTimeOffset udpExpires;
        private DateTimeOffset tcpExpires;
        private DateTimeOffset udpRenewAt;
        private DateTimeOffset tcpRenewAt;
        private DateTimeOffset externalRefreshAt;
        private bool udpExpiryReported;
        private bool tcpExpiryReported;
        private Task? discoveryMap;
        internal INatDevice Device { get; } = device;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal Task BeginDiscoveryMap(Func<Task> start, out bool started)
        {
            lock (gate)
            {
                if (discoveryMap is { IsCompleted: false }) { started = false; return discoveryMap; }
                started = true;
                return discoveryMap = start();
            }
        }
        internal bool ExternalNeedsRefresh(DateTimeOffset now)
        {
            lock (gate) return now >= externalRefreshAt;
        }
        internal bool ProtocolNeedsRenew(NatTransport protocol, DateTimeOffset now)
        {
            lock (gate) return now >= (protocol == NatTransport.Udp ? udpRenewAt : tcpRenewAt);
        }
        internal bool SetExternal(IPAddress? address, DateTimeOffset now)
        {
            lock (gate)
            {
                externalRefreshAt = now.Add(address is null ? RetryInterval : ExternalRefreshInterval);
                if (Equals(external, address)) return false;
                external = address;
                return true;
            }
        }
        internal Mapping? Set(NatTransport protocol, Mapping actual, DateTimeOffset now, int leaseSeconds)
        {
            lock (gate)
            {
                Mapping? previous = protocol == NatTransport.Udp ? udp : tcp;
                DateTimeOffset expires = now.AddSeconds(leaseSeconds);
                DateTimeOffset renewAt = now.AddSeconds(Math.Max(1, leaseSeconds / 2));
                if (protocol == NatTransport.Udp)
                { udp = actual; udpExpires = expires; udpRenewAt = renewAt; udpExpiryReported = false; }
                else
                { tcp = actual; tcpExpires = expires; tcpRenewAt = renewAt; tcpExpiryReported = false; }
                return previous;
            }
        }
        internal bool RecordFailure(NatTransport protocol, DateTimeOffset now)
        {
            lock (gate)
            {
                if (protocol == NatTransport.Udp)
                {
                    udpRenewAt = now.Add(RetryInterval);
                    if (udp is null || now < udpExpires || udpExpiryReported) return false;
                    udpExpiryReported = true;
                }
                else
                {
                    tcpRenewAt = now.Add(RetryInterval);
                    if (tcp is null || now < tcpExpires || tcpExpiryReported) return false;
                    tcpExpiryReported = true;
                }
                return true;
            }
        }
        internal bool NeedsRenew(DateTimeOffset now)
        {
            lock (gate) return now >= externalRefreshAt || now >= udpRenewAt || now >= tcpRenewAt;
        }
        internal Mapping[] Mappings()
        {
            lock (gate) return new[] { udp, tcp }.OfType<Mapping>().ToArray();
        }
        internal IPEndPoint[] PublicEndpoints(DateTimeOffset now)
        {
            lock (gate)
            {
                if (external is null || IPAddress.IsLoopback(external) || external.Equals(IPAddress.Any)) return [];
                return new[] { now < udpExpires ? udp : null, now < tcpExpires ? tcp : null }.OfType<Mapping>()
                    .Where(mapping => mapping.PublicPort is >= 1 and <= 65535)
                    .Select(mapping => new IPEndPoint(external, mapping.PublicPort)).ToArray();
            }
        }
    }
}
