using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PeerSync.Control.Contracts.V1;

namespace PeerSync.App.Dashboard;

internal sealed partial class DashboardViewModel : ObservableObject
{
    [ObservableProperty] private string banner = "Connecting to PeerSync service…";
    [ObservableProperty] private string activity = "Waiting for the service.";
    [ObservableProperty] private string folderActivity = "Select a folder to see its activity.";
    [ObservableProperty] private bool serviceAvailable;
    [ObservableProperty] private bool needsAuthorization;
    [ObservableProperty] private string selectedFolderId = "";
    [ObservableProperty] private string transferRates = "Transfer rates unavailable";
    [ObservableProperty] private string archiveStatus = "Select a folder to list archived versions.";

    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<PeerItem> Peers { get; } = [];
    public ObservableCollection<OverwriteItem> Overwrites { get; } = [];
    public ObservableCollection<ArchiveItem> ArchiveVersions { get; } = [];
    public ObservableCollection<PendingDeviceItem> PendingDevices { get; } = [];
    private readonly HashSet<string> seenOverwrites = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenPending = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ulong Uploaded, ulong Downloaded)> progressByFolder =
        new(StringComparer.Ordinal);
    private bool hasSnapshot;
    public event Action<string>? Notification;

    internal void Connecting() { Banner = "Connecting to PeerSync service…"; ServiceAvailable = false; NeedsAuthorization = false; }
    internal void Disconnected(bool unauthorized)
    {
        ServiceAvailable = false;
        NeedsAuthorization = unauthorized;
        Banner = unauthorized ? "This Windows account is not authorized for PeerSync."
            : "Service not running — retrying automatically…";
        Activity = "No current service snapshot.";
        FolderActivity = "Select a folder to see its activity.";
        Folders.Clear(); Peers.Clear(); Overwrites.Clear(); ArchiveVersions.Clear(); PendingDevices.Clear();
        ArchiveStatus = "Select a folder to list archived versions.";
        hasSnapshot = false; seenOverwrites.Clear(); seenPending.Clear(); seenErrors.Clear();
        progressByFolder.Clear();
        TransferRates = "Transfer rates unavailable";
    }

    internal void Apply(ControlEvent update)
    {
        if (!update.FullSnapshot || update.Status is null)
            throw new InvalidDataException("The control stream did not provide a full snapshot.");
        string previous = SelectedFolderId;
        bool foldersChanged = ReplaceIfChanged(Folders, update.Folders.Select(folder => new FolderItem(
            folder.FolderId, folder.Path, folder.Access, folder.Paused ? "Paused" : "Active")).ToArray());
        SelectedFolderId = Folders.Any(folder => folder.Id == previous) ? previous : Folders.FirstOrDefault()?.Id ?? "";
        if (foldersChanged && SelectedFolderId == previous) OnPropertyChanged(nameof(SelectedFolderId));
        ReplaceIfChanged(Peers, update.Peers.Select(row => new PeerItem(
            row.FolderId, row.Peer.DeviceId, row.Peer.Name, row.Peer.Approval)
            { Transport = row.Peer.Transport }).ToArray());
        var overwrites = new List<OverwriteItem>();
        foreach (var item in update.Overwrites)
        {
            overwrites.Add(new(item.FolderId, item.Id, item.OriginalPath,
                new DateTime(item.ArchivedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            if (hasSnapshot && seenOverwrites.Add(item.FolderId + ":" + item.Id))
                Notification?.Invoke("A local version was overwritten in PeerSync.");
            else seenOverwrites.Add(item.FolderId + ":" + item.Id);
        }
        ReplaceIfChanged(Overwrites, overwrites);
        var pending = new List<PendingDeviceItem>();
        foreach (var item in update.PendingDevices)
        {
            pending.Add(new(item.FolderId, item.DeviceId, item.Name,
                new DateTime(item.RequestedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            if (hasSnapshot && seenPending.Add(item.FolderId + ":" + item.DeviceId))
                Notification?.Invoke("A device is waiting for approval in PeerSync.");
            else seenPending.Add(item.FolderId + ":" + item.DeviceId);
        }
        ReplaceIfChanged(PendingDevices, pending);
        Banner = update.Truncated ? "Connected — snapshot limited; open details to load more." : "Connected to PeerSync service";
        ServiceAvailable = true; NeedsAuthorization = false;
        ulong uploaded = 0, downloaded = 0;
        progressByFolder.Clear();
        foreach (var progress in update.Progress)
        {
            uploaded += progress.UploadedBytes; downloaded += progress.DownloadedBytes;
            progressByFolder[progress.FolderId] = (progress.UploadedBytes, progress.DownloadedBytes);
        }
        Activity = $"{Folders.Count} folders · {Peers.Count} peers · {Overwrites.Count} overwritten versions · "
            + $"↑ {uploaded / 1048576d:0.0} MiB · ↓ {downloaded / 1048576d:0.0} MiB";
        var currentErrors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in update.Errors)
        {
            string key = error.Code + ':' + error.Source + ':' + error.OccurredUtcTicks;
            currentErrors.Add(key);
            if (hasSnapshot && seenErrors.Add(key)) Notification?.Invoke($"PeerSync error: {error.Code}");
            else seenErrors.Add(key);
        }
        seenErrors.IntersectWith(currentErrors);
        UpdateFolderActivity();
        hasSnapshot = true;
    }

    partial void OnSelectedFolderIdChanged(string value)
    {
        ArchiveVersions.Clear();
        ArchiveStatus = value.Length == 0 ? "Select a folder to list archived versions."
            : "Open Archive or choose Refresh to load versions.";
        UpdateFolderActivity();
    }

    internal void ApplyArchive(ListArchiveResponse response)
    {
        if (response.FolderId != SelectedFolderId) return;
        ReplaceIfChanged(ArchiveVersions, response.Versions.Select(item => new ArchiveItem(
            item.FolderId, item.Id, item.OriginalPath,
            new DateTime(item.ArchivedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            item.Size, item.Overwritten)).ToArray());
        ArchiveStatus = response.Truncated
            ? $"Showing the newest {ArchiveVersions.Count} versions; more versions exist."
            : $"{ArchiveVersions.Count} archived versions.";
    }

    private void UpdateFolderActivity()
    {
        if (SelectedFolderId.Length == 0) { FolderActivity = "Select a folder to see its activity."; return; }
        int peers = Peers.Count(item => item.FolderId == SelectedFolderId);
        int overwrites = Overwrites.Count(item => item.FolderId == SelectedFolderId);
        progressByFolder.TryGetValue(SelectedFolderId, out var progress);
        FolderActivity = $"{peers} peers · {overwrites} overwritten versions · "
            + $"↑ {progress.Uploaded / 1048576d:0.0} MiB · ↓ {progress.Downloaded / 1048576d:0.0} MiB";
    }

    private static bool ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> next)
    {
        if (target.SequenceEqual(next)) return false;
        target.Clear();
        foreach (var item in next) target.Add(item);
        return true;
    }

    internal void Apply(ControlMetrics sample)
    {
        if (!sample.TransferMetricsAvailable) { TransferRates = "Transfer rates unavailable"; return; }
        TransferRates = $"Upload {sample.UploadBytesPerSecond / 1024d / 1024d:0.0} MiB/s · "
            + $"Download {sample.DownloadBytesPerSecond / 1024d / 1024d:0.0} MiB/s · "
            + $"Hashing {sample.HashingBytesPerSecond / 1024d / 1024d:0.0} MiB/s · "
            + $"Queue {sample.QueueDepth}";
    }
}

internal sealed record FolderItem(string Id, string Path, string Access, string State)
{
    public override string ToString() => $"{Path}  ·  {State}";
}
internal sealed record PeerItem(string FolderId, string DeviceId, string Name, string Approval)
{
    public string Fingerprint => DeviceId.Length > 16 ? DeviceId[..16] : DeviceId;
    public string Transport { get; init; } = "";
    public string Route => Transport switch
    {
        "Relay" => "via relay",
        "Quic" => "direct QUIC",
        "Tcp" => "direct TCP",
        _ => "Offline",
    };
}
internal sealed record OverwriteItem(string FolderId, long ArchiveId, string Path, string ArchivedLocal);
internal sealed record ArchiveItem(string FolderId, long ArchiveId, string Path,
    string ArchivedLocal, long Size, bool Overwritten)
{
    public string SizeDisplay => Size < 1024 ? $"{Size} B"
        : Size < 1024 * 1024 ? $"{Size / 1024d:0.0} KiB"
        : $"{Size / 1024d / 1024d:0.0} MiB";
}
internal sealed record PendingDeviceItem(string FolderId, string DeviceId, string Name, string RequestedLocal)
{
    public string Fingerprint => DeviceId.Length > 16 ? DeviceId[..16] : DeviceId;
}
