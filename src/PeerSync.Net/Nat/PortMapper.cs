using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Mono.Nat;
using PeerSync.Core.Diagnostics;
using NatTransport = Mono.Nat.Protocol;

namespace PeerSync.Net.Nat;

/// <summary>Best-effort UPnP/NAT-PMP mappings for the service's TCP and UDP listener.</summary>
internal sealed class PortMapper(int port, DiagnosticJournal? diagnostics = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceState> devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> operations = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? renewer;
    private int nextOperation;
    private int disposed;
    internal event Action? MappingsChanged;

    internal IReadOnlyList<IPEndPoint> PublicEndpoints => devices.Values
        .SelectMany(state => state.PublicEndpoints())
        .DistinctBy(endpoint => endpoint.ToString()).ToArray();

    internal void Start()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (renewer is not null) return;
        NatUtility.DeviceFound += OnDeviceFound;
        try
        {
            NatUtility.StartDiscovery();
            renewer = Task.Run(RenewLoopAsync);
            diagnostics?.TryWrite(new("nat.discovery_started", "port_mapping")
            { Level = DiagnosticLevel.Debug });
        }
        catch (Exception error) when (error is SocketException or InvalidOperationException or IOException)
        {
            NatUtility.DeviceFound -= OnDeviceFound;
            diagnostics?.TryWrite(new("nat.discovery_unavailable", "port_mapping")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
    }

    private void OnDeviceFound(object? sender, DeviceEventArgs eventArgs)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        INatDevice device = eventArgs.Device;
        string key = device.NatProtocol + "|" + device.DeviceEndpoint;
        DeviceState state = devices.GetOrAdd(key, _ => new DeviceState(device));
        int id = Interlocked.Increment(ref nextOperation);
        Task task = MapDeviceAsync(state, stopping.Token);
        operations[id] = task;
        _ = task.ContinueWith(completed =>
        {
            operations.TryRemove(new KeyValuePair<int, Task>(id, completed));
            if (completed.IsFaulted)
                diagnostics?.TryWrite(new("nat.mapping_failed", "port_mapping")
                { Level = DiagnosticLevel.Warning,
                    ErrorCode = completed.Exception?.GetBaseException().GetType().Name });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RenewLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                await Task.WhenAll(devices.Values.Select(state => MapDeviceAsync(state, stopping.Token)))
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task MapDeviceAsync(DeviceState state, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IPAddress? external = null;
            try { external = await state.Device.GetExternalIPAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or MappingException or TimeoutException)
            {
                diagnostics?.TryWrite(new("nat.external_address_unavailable", "port_mapping")
                { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name });
            }
            foreach (NatTransport protocol in new[] { NatTransport.Udp, NatTransport.Tcp })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Mapping actual = await state.Device.CreatePortMapAsync(
                        new Mapping(protocol, port, port, 3600, "PeerSync"))
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    Mapping? previous = state.Set(protocol, actual, external);
                    try { MappingsChanged?.Invoke(); } catch { }
                    if (previous is not null && previous.PublicPort != actual.PublicPort)
                        try { await state.Device.DeletePortMapAsync(previous).WaitAsync(cancellationToken)
                            .ConfigureAwait(false); } catch (MappingException) { }
                    diagnostics?.TryWrite(new("nat.mapping_active", "port_mapping")
                    { Level = DiagnosticLevel.Debug, Transport = protocol == NatTransport.Udp
                        ? DiagnosticTransport.Quic : DiagnosticTransport.Tcp,
                        RemoteAddress = external, RemotePort = actual.PublicPort });
                }
                catch (Exception error) when (error is MappingException or SocketException or IOException
                    or TimeoutException or InvalidOperationException)
                {
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
        try { NatUtility.StopDiscovery(); } catch (Exception error) when (error is SocketException or IOException) { }
        if (renewer is not null) try { await renewer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(operations.Values.ToArray()).ConfigureAwait(false); } catch { }
        foreach (DeviceState state in devices.Values)
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (Mapping mapping in state.Mappings())
                    try { await state.Device.DeletePortMapAsync(mapping)
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
            finally { state.Gate.Release(); state.Gate.Dispose(); }
        }
        stopping.Dispose();
        diagnostics?.TryWrite(new("nat.discovery_stopped", "port_mapping")
        { Level = DiagnosticLevel.Debug });
    }

    private sealed class DeviceState(INatDevice device)
    {
        private readonly object gate = new();
        private Mapping? udp;
        private Mapping? tcp;
        private IPAddress? external;
        internal INatDevice Device { get; } = device;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal Mapping? Set(NatTransport protocol, Mapping actual, IPAddress? address)
        {
            lock (gate)
            {
                Mapping? previous = protocol == NatTransport.Udp ? udp : tcp;
                if (protocol == NatTransport.Udp) udp = actual; else tcp = actual;
                if (address is not null) external = address;
                return previous;
            }
        }
        internal Mapping[] Mappings()
        {
            lock (gate) return new[] { udp, tcp }.OfType<Mapping>().ToArray();
        }
        internal IPEndPoint[] PublicEndpoints()
        {
            lock (gate)
            {
                if (external is null || IPAddress.IsLoopback(external) || external.Equals(IPAddress.Any)) return [];
                return new[] { udp, tcp }.OfType<Mapping>()
                    .Where(mapping => mapping.PublicPort is >= 1 and <= 65535)
                    .Select(mapping => new IPEndPoint(external, mapping.PublicPort)).ToArray();
            }
        }
    }
}
