using System.Text.Json;
using System.Security.Principal;
using PeerSync.Core.Settings;
using PeerSync.Service.Settings;

namespace PeerSync.Service.Control;

/// <summary>Serializes local control updates to the durable machine settings.
/// Runtime listener changes take effect at the next service start.</summary>
internal sealed class ControlSettingsRuntime(string? path)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();

    internal DeviceSettings Read()
    {
        lock (gate) return Store().ReadIfPresent()
            ?? throw new InvalidOperationException("Device settings are unavailable.");
    }

    internal (DeviceSettings Value, bool RestartRequired) Update(string json)
    {
        var proposed = SettingsStore.ParseControlUpdate(json);
        lock (gate)
        {
            var store = Store();
            var current = store.ReadIfPresent()
                ?? throw new InvalidOperationException("Device settings are unavailable.");
            if (!string.Equals(current.DeviceCertificateThumbprint, proposed.DeviceCertificateThumbprint,
                    StringComparison.OrdinalIgnoreCase)
                || !current.ControlUserSids.SequenceEqual(proposed.ControlUserSids, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Device identity and control authorization cannot be changed through settings.", nameof(json));
            // The running transport owns a frozen startup snapshot of device preferences.
            bool restart = !string.Equals(Serialize(current), Serialize(proposed), StringComparison.Ordinal);
            store.Save(proposed);
            return (proposed, restart);
        }
    }

    internal bool AuthorizeUser(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var identity = new SecurityIdentifier(sid);
        lock (gate)
        {
            var store = Store();
            var current = store.ReadIfPresent()
                ?? throw new InvalidOperationException("Device settings are unavailable.");
            if (current.ControlUserSids.Any(value =>
                string.Equals(value, identity.Value, StringComparison.OrdinalIgnoreCase))) return false;
            if (current.ControlUserSids.Length >= 64)
                throw new InvalidOperationException("The control authorization list is full.");
            store.Save(current with { ControlUserSids = [.. current.ControlUserSids, identity.Value] });
            return true;
        }
    }

    internal static string Serialize(DeviceSettings value) => JsonSerializer.Serialize(value, Json);

    private SettingsStore Store() => path is null ? throw new ControlRuntimeUnavailableException() : new(path);
}
