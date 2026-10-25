using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Engine.Scanning;
using PeerSync.Crypto.Chunking;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using PeerSync.Net.Connections;
using PeerSync.Net.Security;
using PeerSync.Storage.FolderDb;
using PeerSync.Storage.StateDb;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class InstalledFolderHostTests
{
    [Fact]
    public void ConfiguredSelfSignedDeviceCertificateLoadsByPinWithoutChainTrust()
    {
        using var certificate = CreateDeviceCertificate();
        var certificates = new X509Certificate2Collection { certificate };
        certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint!, validOnly: true).Count.ShouldBe(0);

        var loaded = InstalledFolderHost.FindDeviceCertificate(certificates, certificate.Thumbprint!);

        loaded.ShouldNotBeNull();
        loaded.Thumbprint.ShouldBe(certificate.Thumbprint);
        loaded.HasPrivateKey.ShouldBeTrue();
        loaded.Dispose();
    }

    [Fact]
    public async Task UnknownDeviceWaitsForApprovalAndBlockedDeviceCannotReconnect()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceApproval-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        const string firstContent = "approved transfer";
        File.WriteAllText(Path.Combine(leftShare, "approval.txt"), firstContent);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var keys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftShare, actorSid, keys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightShare, actorSid, keys, leftId, timeout.Token,
                approveRemote: false);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new DeviceSettings { ListenPort = rightPort, LanDiscovery = false },
                rightStatus, cancellationToken: timeout.Token);

            await left.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token);
            await UntilAsync(() => right.ListPendingDevices().Any(row => row.DeviceId.SequenceEqual(leftId)), timeout.Token);
            File.Exists(Path.Combine(rightShare, "approval.txt")).ShouldBeFalse();
            await right.DecideDeviceAsync(Convert.ToHexString(keys.FolderId()), leftId, DeviceApproval.Approved,
                actorSid, timeout.Token);
            await UntilAsync(() => !right.ListPendingDevices().Any(row => row.DeviceId.SequenceEqual(leftId)), timeout.Token);
            await left.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token);
            await UntilAsync(() => FileMatches(Path.Combine(rightShare, "approval.txt"), firstContent), timeout.Token);

            string folderId = Convert.ToHexString(keys.FolderId());
            var oldRuntime = right.ResolveFolder(folderId);
            var changed = await right.UpdateFolderSettingsAsync(folderId,
                right.GetFolderSettings(folderId) with { ArchiveDays = 7 }, actorSid, timeout.Token);
            changed.RestartRequired.ShouldBeFalse();
            right.ResolveFolder(folderId).ShouldNotBeSameAs(oldRuntime);
            right.ResolveFolder(folderId).Settings.ArchiveDays.ShouldBe(7);
            File.WriteAllText(Path.Combine(leftShare, "after-policy.txt"), "transfer after live policy change");
            await UntilAsync(() => FileMatches(Path.Combine(rightShare, "after-policy.txt"),
                "transfer after live policy change"), timeout.Token);

            await right.DecideDeviceAsync(folderId, leftId, DeviceApproval.Blocked,
                actorSid, timeout.Token);
            await UntilAsync(() => !rightStatus.ReadStatus().ProductionSyncAvailable, timeout.Token);
            File.WriteAllText(Path.Combine(leftShare, "after-block.txt"), "must not cross a blocked connection");
            try { await left.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token); }
            catch (IOException) { }
            await Task.Delay(1200, timeout.Token);
            File.Exists(Path.Combine(rightShare, "after-block.txt")).ShouldBeFalse();
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task AddingFolderToConnectedPeersRenegotiatesApprovalBeforeTransfer()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceDynamic-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftBase = Path.Combine(root, "left", "base"), rightBase = Path.Combine(root, "right", "base");
        string leftAdded = Path.Combine(root, "left", "added"), rightAdded = Path.Combine(root, "right", "added");
        foreach (string path in new[] { leftData, rightData, leftBase, rightBase, leftAdded, rightAdded })
            Directory.CreateDirectory(path);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var baseKeys = FolderKeys.CreateNew();
            using var addedKeys = FolderKeys.CreateNew();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftBase, actorSid, baseKeys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightBase, actorSid, baseKeys, leftId, timeout.Token);
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new DeviceSettings { ListenPort = rightPort, LanDiscovery = false },
                rightStatus, cancellationToken: timeout.Token);

            await left.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token);
            await UntilAsync(() => leftStatus.ReadStatus().ProductionSyncAvailable
                && rightStatus.ReadStatus().ProductionSyncAvailable, timeout.Token);

            string keyText = addedKeys.ExportReadWriteText();
            await right.AddFolderAsync(FolderKeys.FromText(keyText), rightAdded, actorSid, timeout.Token);
            string addedId = Convert.ToHexString(addedKeys.FolderId());
            await right.UpdateFolderSettingsAsync(addedId,
                right.GetFolderSettings(addedId) with { RequireApproval = true }, actorSid, timeout.Token);
            const string content = "must wait for approval on a newly added folder";
            File.WriteAllText(Path.Combine(leftAdded, "pending.txt"), content);
            await left.AddFolderAsync(FolderKeys.FromText(keyText), leftAdded, actorSid, timeout.Token);
            await UntilAsync(() => right.ListPendingDevices().Any(row => row.DeviceId.SequenceEqual(leftId)
                && row.FolderId.SequenceEqual(addedKeys.FolderId())), timeout.Token);
            File.Exists(Path.Combine(rightAdded, "pending.txt")).ShouldBeFalse();

            await right.DecideDeviceAsync(addedId, leftId, DeviceApproval.Approved, actorSid, timeout.Token);
            await UntilAsync(() => FileMatches(Path.Combine(rightAdded, "pending.txt"), content), timeout.Token);

            await right.PauseFolderAsync(addedId, actorSid, timeout.Token);
            await UntilAsync(() => leftStatus.ReadStatus().ProductionSyncAvailable
                && rightStatus.ReadStatus().ProductionSyncAvailable, timeout.Token);
            const string resumedContent = "resumed membership must be announced";
            File.WriteAllText(Path.Combine(leftAdded, "after-resume.txt"), resumedContent);
            File.Exists(Path.Combine(rightAdded, "after-resume.txt")).ShouldBeFalse();
            await right.ResumeFolderAsync(addedId, actorSid, timeout.Token);
            await UntilAsync(() => FileMatches(Path.Combine(rightAdded, "after-resume.txt"),
                resumedContent), timeout.Token);
        }
        finally { await DeleteTemporaryRootAsync(root); }
    }

    [Fact]
    public async Task InstalledNetworkClockSkewBlocksFolderSessionAndReconnectsAfterClockRecovery()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceClock-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        const string content = "must wait for installed host clock recovery";
        File.WriteAllText(Path.Combine(leftShare, "clock.txt"), content);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var keys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftShare, actorSid, keys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightShare, actorSid, keys, leftId, timeout.Token);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            using var skewedClock = new AdjustableClock(DateTimeOffset.UtcNow.AddMinutes(11));
            using var leftJournal = new DiagnosticJournal(Path.Combine(root, "left-logs"), "service_test", debugEnabled: true);
            using var rightJournal = new DiagnosticJournal(Path.Combine(root, "right-logs"), "service_test", debugEnabled: true);
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new PeerSync.Core.Settings.DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, leftJournal, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new PeerSync.Core.Settings.DeviceSettings { ListenPort = rightPort, LanDiscovery = false },
                rightStatus, rightJournal, timeProvider: skewedClock, cancellationToken: timeout.Token);

            await AbortQuicHandshakeAsync(rightPort, leftCertificate, timeout.Token);
            await UntilAsync(() => ReadDiagnosticLog(Path.Combine(root, "right-logs", "debug.jsonl"))
                .Contains("\"eventName\":\"service.incoming_peer_rejected\"", StringComparison.Ordinal), timeout.Token);
            right.Completion.IsCompleted.ShouldBeFalse("one aborted peer handshake must not stop the installed QUIC listener");

            await left.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token);
            await UntilAsync(() => leftStatus.ReadStatus().Clocks.Any(row => row.Session.Clock.State == PeerClockState.Skewed)
                && rightStatus.ReadStatus().Clocks.Any(row => row.Session.Clock.State == PeerClockState.Skewed), timeout.Token);
            string receivedPath = Path.Combine(rightShare, "clock.txt");
            File.Exists(receivedPath).ShouldBeFalse("a skewed folder handshake must not admit index exchange or apply files");
            leftStatus.ReadStatus().ProductionSyncAvailable.ShouldBeFalse();
            rightStatus.ReadStatus().ProductionSyncAvailable.ShouldBeFalse();
            ReadDiagnosticLog(Path.Combine(root, "left-logs", "debug.jsonl")).ShouldContain("\"eventName\":\"clock.blocked\"");

            skewedClock.SetUtcNow(DateTimeOffset.UtcNow);
            await UntilAsync(() => FileMatches(receivedPath, content)
                && leftStatus.ReadStatus().ProductionSyncAvailable && rightStatus.ReadStatus().ProductionSyncAvailable,
                timeout.Token);
            leftStatus.ReadStatus().Clocks.ShouldContain(row => row.Session.Clock.State == PeerClockState.Accepted);
            rightStatus.ReadStatus().Clocks.ShouldContain(row => row.Session.Clock.State == PeerClockState.Accepted);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task InstalledHostDialsPredefinedHostAddedWhileServiceIsRunning()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServicePredefined-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        const string content = "runtime configured predefined host";
        File.WriteAllText(Path.Combine(leftShare, "predefined.txt"), content);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var keys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftShare, actorSid, keys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightShare, actorSid, keys, leftId, timeout.Token);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new PeerSync.Core.Settings.DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new PeerSync.Core.Settings.DeviceSettings { ListenPort = rightPort, LanDiscovery = false },
                rightStatus, cancellationToken: timeout.Token);

            await left.SetPredefinedHostsAsync(leftShare, [$"127.0.0.1:{rightPort}"], actorSid, timeout.Token);
            string received = Path.Combine(rightShare, "predefined.txt");
            await UntilAsync(() => FileMatches(received, content)
                && leftStatus.ReadStatus().ProductionSyncAvailable
                && rightStatus.ReadStatus().ProductionSyncAvailable, timeout.Token);

            await left.DisposeAsync();
            await using var state = await StateStore.OpenAsync(leftState, cancellationToken: timeout.Token);
            state.ListFolders().Single().Settings.PredefinedHosts.ShouldBe([$"127.0.0.1:{rightPort}"]);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task InstalledHostFallsBackFromUnreachableLanAddressToPublicAddress()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceHappyEyeballs-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        const string content = "public endpoint wins after unreachable LAN candidates";
        File.WriteAllText(Path.Combine(leftShare, "fallback.txt"), content);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var keys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort(), closedPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftShare, actorSid, keys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightShare, actorSid, keys, leftId, timeout.Token);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new DeviceSettings { ListenPort = rightPort, LanDiscovery = false },
                rightStatus, cancellationToken: timeout.Token);

            left.SetPeerEndpointsForTest(rightId,
            [ (new IPEndPoint(IPAddress.Loopback, closedPort), ConnectionSource.Lan),
              (new IPEndPoint(IPAddress.Loopback, rightPort), ConnectionSource.Public) ]);

            string received = Path.Combine(rightShare, "fallback.txt");
            await UntilAsync(() => FileMatches(received, content)
                && leftStatus.ReadStatus().ProductionSyncAvailable
                && rightStatus.ReadStatus().ProductionSyncAvailable, timeout.Token);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task InstalledNetworkRuntimeAuthenticatesAndSynchronizesAnApprovedPeer()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServicePeer-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        string content = "service-hosted authenticated transfer";
        byte[] largeContent = new byte[512 * 1024]; new Random(4127).NextBytes(largeContent);
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        File.WriteAllText(Path.Combine(leftShare, "hello.txt"), content);
        File.WriteAllBytes(Path.Combine(leftShare, "large.bin"), largeContent);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var keys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareNodeAsync(leftState, leftData, leftShare, actorSid, keys, rightId, timeout.Token);
            await PrepareNodeAsync(rightState, rightData, rightShare, actorSid, keys, leftId, timeout.Token);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            string leftLogs = Path.Combine(root, "left-logs"), rightLogs = Path.Combine(root, "right-logs");
            using var leftJournal = new DiagnosticJournal(leftLogs, "service_test", debugEnabled: true);
            using var rightJournal = new DiagnosticJournal(rightLogs, "service_test", debugEnabled: true);
            await using var left = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new PeerSync.Core.Settings.DeviceSettings { ListenPort = leftPort, LanDiscovery = false },
                leftStatus, leftJournal, cancellationToken: timeout.Token);
            await using var right = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new PeerSync.Core.Settings.DeviceSettings
                { ListenPort = rightPort, LanDiscovery = false, DownloadLimitBytesPerSec = 128 * 1024 },
                rightStatus, rightJournal, cancellationToken: timeout.Token);

            try { await left.ConnectToPeerOverTcpForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token); }
            catch (Exception error)
            {
                string leftFailureLog = File.Exists(Path.Combine(leftLogs, "debug.jsonl")) ? ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl")) : "";
                string rightFailureLog = File.Exists(Path.Combine(rightLogs, "debug.jsonl")) ? ReadDiagnosticLog(Path.Combine(rightLogs, "debug.jsonl")) : "";
                throw new InvalidOperationException($"left={leftFailureLog}\nright={rightFailureLog}", error);
            }
            string receivedPath = Path.Combine(rightShare, "hello.txt");
            string receivedLargePath = Path.Combine(rightShare, "large.bin");
            bool smallReceived = false, largeReceived = false;
            while (!smallReceived || !largeReceived)
            {
                try { smallReceived = File.Exists(receivedPath) && File.ReadAllText(receivedPath) == content; }
                catch (IOException) { }
                if (File.Exists(receivedLargePath))
                {
                    try { largeReceived = File.ReadAllBytes(receivedLargePath).AsSpan().SequenceEqual(largeContent); }
                    catch (IOException) { }
                }
                if (!smallReceived || !largeReceived)
                    await Task.Delay(100, timeout.Token);
            }
            await UntilAsync(() =>
            {
                string leftLog = File.Exists(Path.Combine(leftLogs, "debug.jsonl"))
                    ? ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl")) : "";
                string rightLog = File.Exists(Path.Combine(rightLogs, "debug.jsonl"))
                    ? ReadDiagnosticLog(Path.Combine(rightLogs, "debug.jsonl")) : "";
                return EventSessions(leftLog, "service.folder_sync_pass").Length != 0
                    && EventSessions(rightLog, "service.folder_sync_pass").Length != 0;
            }, timeout.Token);
            string rightLog = ReadDiagnosticLog(Path.Combine(rightLogs, "debug.jsonl"));
            rightLog.ShouldContain("\"transport\":\"Tcp\"");
            rightLog.ShouldContain("transfer.download_rate_limited");
            Guid[] limitedSessions = EventSessions(rightLog, "transfer.download_rate_limited");
            string leftLog = ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"));
            Guid[] leftSyncSessions = EventSessions(leftLog, "service.folder_sync_pass");
            Guid[] rightSyncSessions = EventSessions(rightLog, "service.folder_sync_pass");
            leftSyncSessions.Intersect(rightSyncSessions).ShouldNotBeEmpty(
                "both peers should use their shared authenticated session ID for service sync diagnostics");
            Guid[] servingSessions = EventSessions(leftLog, "transport.block_stream_accepted");
            limitedSessions.ShouldNotBeEmpty();
            limitedSessions.Any(servingSessions.Contains).ShouldBeTrue();
            Guid[] oldSessions = leftStatus.ReadStatus().Clocks.Where(row => row.Session.IsConnected)
                .Select(row => row.Session.SessionId).ToArray();
            oldSessions.ShouldNotBeEmpty();
            string preRecoveryLog = ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"));
            int recoveriesBeforeNetworkChange = preRecoveryLog
                .Split("\"eventName\":\"service.network_recovery_complete\"", StringSplitOptions.None).Length - 1;
            int recoveryStartsBeforeNetworkChange = preRecoveryLog
                .Split("\"eventName\":\"service.network_recovery_started\"", StringSplitOptions.None).Length - 1;
            var recoveryTimer = System.Diagnostics.Stopwatch.StartNew();
            leftStatus.NotifyNetworkChanged();
            await UntilAsync(() => File.Exists(Path.Combine(leftLogs, "debug.jsonl"))
                && ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"))
                    .Split("\"eventName\":\"service.network_recovery_started\"", StringSplitOptions.None).Length
                    >= recoveryStartsBeforeNetworkChange + 2, timeout.Token);
            leftStatus.NotifyNetworkChanged();
            await UntilAsync(() => leftStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !oldSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => rightStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !oldSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => File.Exists(Path.Combine(leftLogs, "debug.jsonl"))
                && ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"))
                    .Split("\"eventName\":\"service.network_recovery_complete\"", StringSplitOptions.None).Length
                    >= recoveriesBeforeNetworkChange + 3, timeout.Token);
            await UntilAsync(() => leftStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !oldSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => rightStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !oldSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => File.Exists(Path.Combine(leftLogs, "debug.jsonl"))
                && ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"))
                    .Contains("\"eventName\":\"service.resume_scan_complete\"", StringComparison.Ordinal), timeout.Token);
            recoveryTimer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            Guid[] networkRecoveredSessions = leftStatus.ReadStatus().Clocks.Where(row => row.Session.IsConnected)
                .Select(row => row.Session.SessionId).ToArray();
            networkRecoveredSessions.ShouldNotBeEmpty();
            var resumeTimer = System.Diagnostics.Stopwatch.StartNew();
            leftStatus.NotifyResumed();
            await UntilAsync(() => leftStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !networkRecoveredSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => rightStatus.ReadStatus().Clocks.Any(row => row.Session.IsConnected
                && !networkRecoveredSessions.Contains(row.Session.SessionId)), timeout.Token);
            await UntilAsync(() => File.Exists(Path.Combine(leftLogs, "debug.jsonl"))
                && ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"))
                    .Split("\"eventName\":\"service.resume_scan_complete\"", StringSplitOptions.None).Length >= 3,
                timeout.Token);
            resumeTimer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            string recoveredLog = ReadDiagnosticLog(Path.Combine(leftLogs, "debug.jsonl"));
            recoveredLog.ShouldContain("\"eventName\":\"service.peer_session_reset\"");
            recoveredLog.ShouldContain("\"eventName\":\"service.network_recovery_complete\"");
            recoveredLog.ShouldContain("\"itemsSkipped\":1");
            leftStatus.ReadStatus().ProductionSyncAvailable.ShouldBeTrue();
            rightStatus.ReadStatus().ProductionSyncAvailable.ShouldBeTrue();
            var stopTimer = System.Diagnostics.Stopwatch.StartNew();
            await left.DisposeAsync(); await right.DisposeAsync();
            stopTimer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
            leftStatus.ReadStatus().ProductionSyncAvailable.ShouldBeFalse();
            rightStatus.ReadStatus().ProductionSyncAvailable.ShouldBeFalse();
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task InstalledHostOpensRegisteredFolderAndDrainsItsInitialScanOnStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceHost-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data");
        string share = Path.Combine(root, "share");
        string statePath = Path.Combine(data, "peersync.db");
        string logPath = Path.Combine(root, "logs");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(share);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var keys = FolderKeys.CreateNew();
            await using (var state = await StateStore.OpenAsync(statePath, cancellationToken: TestContext.Current.CancellationToken))
                await state.RegisterFolderAsync(keys, share, new FolderSettings { RescanIntervalSeconds = 3600 }, actorSid,
                    cancellationToken: TestContext.Current.CancellationToken);

            var status = new ControlStatusRuntime();
            var deviceId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            using var journal = new DiagnosticJournal(logPath, "service", debugEnabled: true);
            await using (var host = await InstalledFolderHost.StartForTestAsync(statePath, deviceId, actorSid, status,
                journal, TestContext.Current.CancellationToken))
            {
                var folder = host.ResolveFolder(Convert.ToHexString(keys.FolderId()));
                ReferenceEquals(folder.Scanner, folder.Transport.Scanner).ShouldBeTrue();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                bool scanCompleted = false;
                while (DateTime.UtcNow < deadline && !scanCompleted)
                {
                    string journalFile = Path.Combine(logPath, "debug.jsonl");
                    scanCompleted = File.Exists(journalFile) && ReadDiagnosticLog(journalFile).Contains("\"eventName\":\"scan.complete\"", StringComparison.Ordinal);
                    if (!scanCompleted) await Task.Delay(100, TestContext.Current.CancellationToken);
                }
                scanCompleted.ShouldBeTrue("the service scheduler should complete its startup scan");
                string events = ReadDiagnosticLog(Path.Combine(logPath, "debug.jsonl"));
                events.ShouldContain("scheduler.pass_start");
                events.ShouldNotContain("scheduler.scan_start");
                status.ReadStatus().ProductionSyncAvailable.ShouldBeFalse();
            }
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact]
    public async Task InstalledHostRecoversPreparedApplyBeforeStartingItsIndependentScanner()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-ServiceHostRecovery-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data"), share = Path.Combine(root, "share");
        string statePath = Path.Combine(data, "peersync.db"), logPath = Path.Combine(root, "logs");
        byte[] oldBytes = "original local bytes"u8.ToArray(), newBytes = "verified remote bytes"u8.ToArray();
        Directory.CreateDirectory(data); Directory.CreateDirectory(share);
        File.WriteAllBytes(Path.Combine(share, "file"), oldBytes);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var keys = FolderKeys.CreateNew();
            byte[] own = new byte[32], remote = new byte[32]; own[0] = 1; remote[0] = 2;
            await using (var state = await StateStore.OpenAsync(statePath, cancellationToken: TestContext.Current.CancellationToken))
                await state.RegisterFolderAsync(keys, share, new FolderSettings { RescanIntervalSeconds = 3600 }, actorSid,
                    cancellationToken: TestContext.Current.CancellationToken);
            string indexPath = Path.Combine(data, "folders", Convert.ToHexString(keys.FolderId()) + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await using (var index = await FolderIndex.OpenAsync(indexPath, TestContext.Current.CancellationToken))
            {
                using (var scanner = new Scanner(share, index, keys, own))
                    await scanner.ScanAsync(TestContext.Current.CancellationToken);
                byte[] hash = Blake3.Hasher.Hash(newBytes).AsSpan().ToArray();
                long now = DateTime.UtcNow.Ticks;
                var body = new FileRecordBody { Path = "file", Type = 0, MtimeTicks = now, ChangedTicks = now,
                    ModifiedBy = ByteString.CopyFrom(remote), Size = (ulong)newBytes.Length,
                    ChunkClass = ChunkParameters.ClassFor(newBytes.Length), FileHash = ByteString.CopyFrom(hash) };
                body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
                body.Chunks.Add(new Chunk { Hash = body.FileHash, Length = (uint)newBytes.Length });
                await new NeededSet(index, keys, own).ApplyIndexUpdateAsync(remote, 0, 1,
                    [SignedRecordFactory.Create(body, keys, 1)], TestContext.Current.CancellationToken);
                var interrupted = new FileApplier(share, index, keys, own)
                    { Checkpoint = stage => { if (stage == "archived") throw new IOException("injected"); } };
                await Should.ThrowAsync<IOException>(() => interrupted.ApplyAsync("file",
                    (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(newBytes), TestContext.Current.CancellationToken));
                index.ReadApplyIntents().ShouldHaveSingleItem();
                File.Exists(Path.Combine(share, "file")).ShouldBeFalse();
            }

            using var journal = new DiagnosticJournal(logPath, "service", debugEnabled: true);
            var status = new ControlStatusRuntime();
            await using (var host = await InstalledFolderHost.StartForTestAsync(statePath, own, actorSid, status,
                journal, TestContext.Current.CancellationToken))
            {
                File.ReadAllBytes(Path.Combine(share, "file")).ShouldBe(newBytes);
                host.ResolveFolder(Convert.ToHexString(keys.FolderId())).Index.ReadApplyIntents().ShouldBeEmpty();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await UntilAsync(() => File.Exists(Path.Combine(logPath, "debug.jsonl"))
                    && ReadDiagnosticLog(Path.Combine(logPath, "debug.jsonl")).Contains("\"eventName\":\"scan.complete\"", StringComparison.Ordinal),
                    deadline.Token);
                string events = ReadDiagnosticLog(Path.Combine(logPath, "debug.jsonl"));
                int recovery = events.IndexOf("puller.startup_recovery_complete", StringComparison.Ordinal);
                int scan = events.IndexOf("\"eventName\":\"scan.start\"", StringComparison.Ordinal);
                recovery.ShouldBeGreaterThanOrEqualTo(0);
                scan.ShouldBeGreaterThan(recovery);
                events.ShouldNotContain("scheduler.scan_failed");
            }
        }
        finally { await DeleteTemporaryRootAsync(root); }
    }

    private static async Task DeleteTemporaryRootAsync(string root)
    {
        string full = Path.GetFullPath(root);
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), temporary, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("PeerSync-Service", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _))
            throw new IOException("Unexpected service-test fixture root.");

        // SQLite pools are cleared by host disposal. Hosted Windows still
        // reports sharing violations for these fixture databases after shutdown.
        // Keep the functional assertion result and report deferred cleanup;
        // other I/O failures remain test failures.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (!Directory.Exists(full)) return;
            try { Directory.Delete(full, recursive: true); return; }
            catch (IOException) when (attempt < 29) { await Task.Delay(100); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                string message = $"Service test fixture cleanup deferred after 3 seconds " +
                    $"(Windows sharing violation): {full}; {error.Message}";
                TestContext.Current.TestOutputHelper?.WriteLine(message);
                TestContext.Current.SendDiagnosticMessage(message);
                return;
            }
        }
    }

    private static async Task PrepareNodeAsync(string statePath, string dataPath, string share, string actorSid,
        FolderKeys keys, byte[] remoteId, CancellationToken token, bool approveRemote = true)
    {
        await using (var state = await StateStore.OpenAsync(statePath, cancellationToken: token))
            await state.RegisterFolderAsync(keys, share, new PeerSync.Core.Settings.FolderSettings
            { RescanIntervalSeconds = 3600, RequireApproval = true }, actorSid, cancellationToken: token);
        string indexPath = Path.Combine(dataPath, "folders", Convert.ToHexString(keys.FolderId()) + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await using var index = await FolderIndex.OpenAsync(indexPath, token);
        if (approveRemote)
            await index.Devices.UpsertAsync([new DeviceRow(remoteId, Approval: DeviceApproval.Approved, Pinned: true)], token);
    }

    private static X509Certificate2 CreateDeviceCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync service test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "service-test"), "service-test",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task AbortQuicHandshakeAsync(int port, X509Certificate2 certificate, CancellationToken token)
    {
        var options = new System.Net.Quic.QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            ClientAuthenticationOptions = PeerTls.Client(certificate),
            DefaultCloseErrorCode = 1, DefaultStreamErrorCode = 1,
        };
        await using var connection = await System.Net.Quic.QuicConnection.ConnectAsync(options, token);
        await using var stream = await connection.OpenOutboundStreamAsync(System.Net.Quic.QuicStreamType.Bidirectional, token);
        stream.Abort(System.Net.Quic.QuicAbortDirection.Both, 1);
    }

    private static Guid[] EventSessions(string jsonLines, string eventName)
    {
        var result = new List<Guid>();
        foreach (string line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("eventName").GetString() == eventName
                && root.TryGetProperty("sessionId", out JsonElement session)
                && session.ValueKind == JsonValueKind.String
                && Guid.TryParse(session.GetString(), out Guid id)) result.Add(id);
        }
        return result.ToArray();
    }

    private static int ReservePeerPort()
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            // Windows may exclude large portions of the ephemeral port range for
            // Hyper-V and other services. Keep fixture ports in the device's
            // configured listener range and retry any transient bind conflict.
            int port = Random.Shared.Next(20000, 45001);
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.ExclusiveAddressUse = true;
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.ExclusiveAddressUse = true;
            try
            {
                udp.Bind(new IPEndPoint(IPAddress.Any, port));
                tcp.Bind(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException) when (attempt < 127) { }
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static string ReadDiagnosticLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 16 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }

    private static bool FileMatches(string path, string expected)
    {
        try { return File.Exists(path) && File.ReadAllText(path) == expected; }
        catch (IOException) { return false; }
    }

    private sealed class AdjustableClock(DateTimeOffset utcNow) : TimeProvider, IDisposable
    {
        private long utcTicks = utcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        internal void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref utcTicks, value.UtcTicks);
        public void Dispose() { }
    }
}
