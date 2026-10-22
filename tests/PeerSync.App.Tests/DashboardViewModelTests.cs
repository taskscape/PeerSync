using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts.V1;
using Grpc.Core;
using System.IO;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public void PipeAccessDeniedOffersAccountAuthorization()
    {
        DashboardController.IsAuthorizationFailure(new IOException("pipe", new UnauthorizedAccessException())).ShouldBeTrue();
        DashboardController.IsAuthorizationFailure(new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, "denied"))).ShouldBeTrue();
        DashboardController.IsAuthorizationFailure(new IOException("service offline")).ShouldBeFalse();
    }
    [Fact]
    public void ReconnectedFullSnapshotsReplaceStateAndNotifyOnlyNewPendingAndOverwrites()
    {
        var model = new DashboardViewModel();
        var notices = new List<string>(); model.Notification += notices.Add;
        var first = Snapshot();
        first.Folders.Add(new FolderInfo { FolderId = new string('A', 64), Path = @"C:\Share", Access = "ReadWrite" });
        first.PendingDevices.Add(new PendingDeviceInfo { FolderId = new string('A', 64),
            DeviceId = new string('B', 64), RequestedUtcTicks = DateTime.UtcNow.Ticks });
        model.Apply(first);
        model.ServiceAvailable.ShouldBeTrue(); model.Folders.Count.ShouldBe(1);
        model.PendingDevices.Count.ShouldBe(1); notices.ShouldBeEmpty();
        model.SelectedFolderId.ShouldBe(new string('A', 64));

        int selectionNotifications = 0;
        model.PropertyChanged += (_, change) =>
        { if (change.PropertyName == nameof(model.SelectedFolderId)) selectionNotifications++; };

        var second = Snapshot(); second.Folders.Add(first.Folders.Single());
        second.Progress.Add(new ControlTransferProgress { FolderId = new string('A', 64),
            UploadedBytes = 1048576, DownloadedBytes = 2097152 });
        second.Errors.Add(new ControlError { Code = "transfer.failed", Source = "peer_network",
            OccurredUtcTicks = DateTime.UtcNow.Ticks });
        second.PendingDevices.Add(first.PendingDevices.Single());
        second.Overwrites.Add(new ArchiveInfo { FolderId = new string('A', 64), Id = 7,
            OriginalPath = "file.txt", ArchivedUtcTicks = DateTime.UtcNow.Ticks, Overwritten = true });
        model.Apply(second); model.Apply(second);
        selectionNotifications.ShouldBe(0);
        notices.Count.ShouldBe(2);
        notices.ShouldContain(notice => notice.Contains("overwritten", StringComparison.Ordinal));
        notices.ShouldContain(notice => notice.Contains("transfer.failed", StringComparison.Ordinal));
        model.Overwrites.Count.ShouldBe(1);
        model.Activity.ShouldContain("MiB");
        model.FolderActivity.ShouldContain("MiB");
        var paused = Snapshot(); paused.Folders.Add(new FolderInfo
            { FolderId = new string('A', 64), Path = @"C:\Share", Access = "ReadWrite", Paused = true });
        model.Apply(paused);
        selectionNotifications.ShouldBe(1);

        model.Disconnected(unauthorized: true);
        model.ServiceAvailable.ShouldBeFalse(); model.NeedsAuthorization.ShouldBeTrue();
        model.Folders.ShouldBeEmpty(); model.PendingDevices.ShouldBeEmpty();
        model.Overwrites.ShouldBeEmpty();
    }

    [Fact]
    public void MetricsDoNotInventTransferRates()
    {
        var model = new DashboardViewModel();
        model.Apply(new ControlMetrics { TransferMetricsAvailable = false,
            UploadBytesPerSecond = 999_999 });
        model.TransferRates.ShouldBe("Transfer rates unavailable");
        model.Apply(new ControlMetrics { TransferMetricsAvailable = true,
            UploadBytesPerSecond = 1_048_576, DownloadBytesPerSecond = 2_097_152,
            HashingBytesPerSecond = 3_145_728, QueueDepth = 3 });
        string one = 1d.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        string two = 2d.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        model.TransferRates.ShouldContain($"Upload {one} MiB/s");
        model.TransferRates.ShouldContain($"Download {two} MiB/s");
        model.TransferRates.ShouldContain($"Hashing {3d.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} MiB/s");
        model.TransferRates.ShouldContain("Queue 3");
    }

    [Fact]
    public void PeerRouteReflectsTransportFromControlSnapshot()
    {
        var model = new DashboardViewModel();
        ControlEvent relay = Snapshot();
        relay.Peers.Add(new FolderPeerInfo
        {
            FolderId = new string('A', 64),
            Peer = new PeerInfo { DeviceId = new string('B', 64), Name = "Second PC",
                Approval = "Approved", Transport = "Relay" },
        });
        model.Apply(relay);
        model.Peers.Single().Route.ShouldBe("via relay");

        ControlEvent direct = Snapshot();
        direct.Peers.Add(new FolderPeerInfo
        {
            FolderId = new string('A', 64),
            Peer = new PeerInfo { DeviceId = new string('B', 64), Name = "Second PC",
                Approval = "Approved", Transport = "Quic" },
        });
        model.Apply(direct);
        model.Peers.Single().Route.ShouldBe("direct QUIC");
    }

    private static ControlEvent Snapshot() => new()
    { FullSnapshot = true, Status = new ServiceStatus { InstanceId = Guid.NewGuid().ToString("N"), Sequence = 1 } };
}
