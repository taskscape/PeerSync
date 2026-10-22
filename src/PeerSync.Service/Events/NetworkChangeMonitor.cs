using System.Net.NetworkInformation;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Service.Events;

internal interface INetworkChangeSource : IDisposable
{
    event EventHandler? AddressChanged;
    event Action<bool>? AvailabilityChanged;
}

internal sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    public event EventHandler? AddressChanged;
    public event Action<bool>? AvailabilityChanged;

    public SystemNetworkChangeSource()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs args) => AddressChanged?.Invoke(sender, args);
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => AvailabilityChanged?.Invoke(args.IsAvailable);
}

internal sealed class NetworkChangeMonitor : IDisposable
{
    private readonly INetworkChangeSource source;
    private readonly DiagnosticJournal? diagnostics;
    private int disposed;

    public NetworkChangeMonitor(DiagnosticJournal? diagnostics, INetworkChangeSource? source = null)
    {
        this.diagnostics = diagnostics;
        this.source = source ?? new SystemNetworkChangeSource();
        this.source.AddressChanged += OnAddressChanged;
        this.source.AvailabilityChanged += OnAvailabilityChanged;
    }

    public event Action? Changed;

    private void OnAddressChanged(object? sender, EventArgs args) => Notify("service.network_address_changed");
    private void OnAvailabilityChanged(bool isAvailable) => Notify(isAvailable
        ? "service.network_available" : "service.network_unavailable");

    private void Notify(string eventName)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        diagnostics?.TryWrite(new(eventName, "network_change"));
        if (Changed is null) return;
        foreach (Action callback in Changed.GetInvocationList())
        {
            try { callback(); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("service.network_change_callback_failed", "network_change")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        source.AddressChanged -= OnAddressChanged;
        source.AvailabilityChanged -= OnAvailabilityChanged;
        source.Dispose();
    }
}
