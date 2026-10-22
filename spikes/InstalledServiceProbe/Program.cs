using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using PeerSync.Control.Contracts;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Net.Security;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using PeerSync.Storage.StateDb;

namespace PeerSync.InstalledProbe;

/// <summary>Exercises the fixed SCM service with a disposable authenticated peer.
/// Never prints folder keys, device identities, source paths or file content.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is not (3 or 4 or 5) || !int.TryParse(args[1], out int servicePort) || servicePort is < 1 or > 65535 ||
            args.Length >= 4 && (!int.TryParse(args[3], out int requestedPort) || requestedPort is < 20000 or > 45000) ||
            args.Length == 5 && args[4] != "--render-ui")
        {
            Console.Error.WriteLine("Usage: PeerSync.InstalledProbe <registered-folder-path> <service-port> <existing-file-name> [peer-port] [--render-ui]");
            return 2;
        }

        string stage = "preflight";
        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
            string fileName = args[2];
            if (!Path.IsPathFullyQualified(folder) || fileName is "." or ".." ||
                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                fileName.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ArgumentException("The source must be a registered absolute folder and a single file name.");
            string source = Path.Combine(folder, fileName);
            var sourceInfo = new FileInfo(source);
            if (!sourceInfo.Exists || sourceInfo.Length is <= 0 or > 16 * 1024 * 1024 ||
                (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The probe requires a regular, nonempty source file no larger than 16 MiB.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            CancellationToken token = timeout.Token;
            using var installed = ControlStatusClient.CreateForInstalledService();
            var baseline = await installed.GetStatusAsync(token).ConfigureAwait(false);
            if (baseline.ProductionSyncAvailable || baseline.Clocks.Count != 0)
                throw new InvalidOperationException("The installed service must have no active peer during this probe.");
            var registered = (await installed.ListFoldersAsync(token).ConfigureAwait(false)).Folders
                .SingleOrDefault(row => !row.Paused && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.Path)), folder,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The requested folder is not active in the installed service.");
            var exported = await installed.GetFolderKeysAsync(registered.FolderId, token).ConfigureAwait(false);
            using FolderKeys keys = FolderKeys.FromText(exported.Key);
            if (!keys.CanRead || !keys.FolderId().AsSpan().SequenceEqual(Convert.FromHexString(registered.FolderId)))
                throw new InvalidOperationException("The installed folder key does not match the selected folder.");
            byte[] expectedHash = SHA256.HashData(await File.ReadAllBytesAsync(source, token).ConfigureAwait(false));

            string root = Path.Combine(Path.GetTempPath(), "PeerSync-InstalledProbe-" + Guid.NewGuid().ToString("N"));
            string data = Path.Combine(root, "data");
            string share = Path.Combine(root, "share");
            string target = Path.Combine(share, fileName);
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(share);
            var watch = Stopwatch.StartNew();
            try
            {
                stage = "peer_setup";
                using var identity = WindowsIdentity.GetCurrent();
                string actorSid = identity.User?.Value ?? throw new InvalidOperationException("Probe caller has no Windows identity.");
                using var certificate = CreateDeviceCertificate();
                byte[] peerId = PeerTls.DeviceId(certificate);
                string statePath = Path.Combine(data, "peersync.db");
                await using (var state = await StateStore.OpenAsync(statePath, cancellationToken: token).ConfigureAwait(false))
                    await state.RegisterFolderAsync(keys, share, new FolderSettings
                    { RequireApproval = false, UseLan = false, UseTracker = false, UseRelay = false },
                        actorSid, cancellationToken: token).ConfigureAwait(false);
                var peerStatus = new ControlStatusRuntime();
                var clock = new AdjustableClock(TimeSpan.FromMinutes(11));
                int peerPort = args.Length >= 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
                    : ReservePeerPort();
                await using var ui = args.Length == 5 ? await InstalledUiProbe.StartAsync(token).ConfigureAwait(false) : null;
                await using (var peer = await InstalledFolderHost.StartForTestWithNetworkAsync(statePath, peerId,
                    actorSid, certificate, new DeviceSettings { ListenPort = peerPort, LanDiscovery = false },
                    peerStatus, timeProvider: clock, cancellationToken: token).ConfigureAwait(false))
                {
                    stage = "skew";
                    await peer.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, servicePort), token)
                        .ConfigureAwait(false);
                    ControlStatusSnapshot skewed = await WaitForStatusAsync(installed, snapshot =>
                        snapshot.InstanceId == baseline.InstanceId && !snapshot.ProductionSyncAvailable &&
                        FindPeer(snapshot, keys.FolderId(), peerId)?.Clock.State == PeerClockState.Skewed,
                        token).ConfigureAwait(false);
                    if (File.Exists(target)) throw new InvalidDataException("Index exchange occurred during clock rejection.");
                    if (FindPeer(skewed, keys.FolderId(), peerId)?.Clock.OffsetTicks is not long offset ||
                        Math.Abs((double)offset) < TimeSpan.FromMinutes(10).Ticks)
                        throw new InvalidDataException("The fixed client did not receive the expected clock difference.");
                    if (ui is not null)
                    {
                        stage = "skew_ui";
                        await ui.CaptureSkewAsync(token).ConfigureAwait(false);
                    }

                    stage = "recovery";
                    clock.SetOffset(TimeSpan.Zero);
                    await WaitForStatusAsync(installed, snapshot =>
                        snapshot.InstanceId == baseline.InstanceId && snapshot.ProductionSyncAvailable &&
                        FindPeer(snapshot, keys.FolderId(), peerId) is { Clock.State: PeerClockState.Accepted,
                            IsConnected: true }, token).ConfigureAwait(false);
                    await WaitForFileAsync(target, expectedHash, token).ConfigureAwait(false);
                    if (!peerStatus.ReadStatus().ProductionSyncAvailable)
                        throw new InvalidDataException("The test peer did not admit the recovered session.");
                    if (ui is not null)
                    {
                        stage = "recovery_ui";
                        await ui.CaptureRecoveryAsync(token).ConfigureAwait(false);
                    }
                }

                stage = "disconnect";
                await WaitForStatusAsync(installed, snapshot =>
                    snapshot.InstanceId == baseline.InstanceId && !snapshot.ProductionSyncAvailable &&
                    FindPeer(snapshot, keys.FolderId(), peerId) is not { IsConnected: true },
                    token).ConfigureAwait(false);
                if (ui is not null)
                {
                    stage = "disconnect_ui";
                    await ui.CaptureDisconnectAsync(token).ConfigureAwait(false);
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "passed", scmInstanceVerified = true, authenticatedClockRejected = true,
                    recoveredTransferVerified = true, disconnectObserved = true, renderedUiVerified = ui is not null,
                    bytes = sourceInfo.Length, elapsedSeconds = watch.Elapsed.TotalSeconds,
                }));
                return 0;
            }
            finally
            {
                string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                string actual = Path.GetFullPath(root);
                if (actual.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(actual).StartsWith("PeerSync-InstalledProbe-", StringComparison.Ordinal) &&
                    Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            { status = "failed", stage, error = error.GetType().Name, hResult = error.HResult }));
            return 1;
        }
    }

    private static PeerSessionClockStatus? FindPeer(ControlStatusSnapshot snapshot, byte[] folderId, byte[] deviceId)
        => snapshot.Clocks.FirstOrDefault(row => row.FolderId.AsSpan().SequenceEqual(folderId) &&
            row.Session.RemoteDeviceId.AsSpan().SequenceEqual(deviceId))?.Session;

    private static async Task<ControlStatusSnapshot> WaitForStatusAsync(ControlStatusClient client,
        Func<ControlStatusSnapshot, bool> condition, CancellationToken token)
    {
        while (true)
        {
            ControlStatusSnapshot snapshot = await client.GetStatusAsync(token).ConfigureAwait(false);
            if (condition(snapshot)) return snapshot;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private static async Task WaitForFileAsync(string path, byte[] expectedHash, CancellationToken token)
    {
        while (true)
        {
            try
            {
                if (File.Exists(path) && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)), expectedHash)) return;
            }
            catch (IOException) { }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private static int ReservePeerPort()
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
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

    private static X509Certificate2 CreateDeviceCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync installed service probe", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 temporary = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx, "probe"), "probe",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class AdjustableClock(TimeSpan offset) : TimeProvider
    {
        private long offsetTicks = offset.Ticks;
        public override DateTimeOffset GetUtcNow()
            => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref offsetTicks));
        internal void SetOffset(TimeSpan value) => Interlocked.Exchange(ref offsetTicks, value.Ticks);
    }
}
