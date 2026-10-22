using System.ComponentModel.DataAnnotations;

namespace PeerSync.Core.Settings;

public sealed record DeviceSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string DeviceName { get; init; } = Environment.MachineName;
    [Range(1024, 65535)] public int ListenPort { get; init; }
    public bool UsePortMapping { get; init; } = true;
    public bool LanDiscovery { get; init; } = true;
    public string[] RendezvousServers { get; init; } = ["https://sync.taskscape.com:99"];
    public bool UseRelay { get; init; } = true;
    [Range(0, int.MaxValue)] public int UploadLimitBytesPerSec { get; init; }
    [Range(0, int.MaxValue)] public int DownloadLimitBytesPerSec { get; init; }
    public bool LimitLanTraffic { get; init; }
    public long PauseDownloadsBelowFreeBytes { get; init; } = 1L << 30;
    public bool PauseOnMeteredNetworks { get; init; }
    public int PeerExpiryDays { get; init; } = 7;
    public string DeviceCertificateThumbprint { get; init; } = "";
    public string[] ControlUserSids { get; init; } = [];
}
