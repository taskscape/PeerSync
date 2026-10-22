using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using PeerSync.App.Status;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using PeerSync.Core.Settings;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net;
using PeerSync.Net.InMemory;
using PeerSync.Net.Security;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using PeerSync.Storage.FolderDb;
using PeerSync.Storage.StateDb;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class ClockStatusRenderTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ClockUi-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public Task RealEnginePipeClientAndWpfRenderSkewRecoveryAndServiceFailure() => OnUiAsync(async () =>
    {
        await using var a = await Node(1); var clock = new Clock(); clock.Jump(TimeSpan.FromMinutes(11)); await using var b = await Node(2, clock);
        var runtime = new ControlStatusRuntime(); using var registration = runtime.RegisterFolder(a.Engine);
        using var identity = WindowsIdentity.GetCurrent(); string pipe = "PeerSync.Control.UiTests-" + Guid.NewGuid().ToString("N");
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token);
        var network = new InMemoryNetwork(); var endpoint = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await Should.ThrowAsync<PeerClockException>(() => a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token));
        var model = new ClockStatusViewModel(); var window = Window(model); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var controller = new ClockStatusController(model, (action, token) => window.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task,
            token => { token.ThrowIfCancellationRequested(); return Task.FromResult<IControlStatusConnection>(new VerifiedStatusConnection(ControlStatusClient.CreateFixture(new(pipe, identity.User!, (uint)Environment.ProcessId)))); });
        window.Show(); var watching = controller.RunAsync(stop.Token);
        try
        {
            await Until(() => model.Rows.Count == 1 && model.Rows[0].Reason.Contains("exchange blocked", StringComparison.Ordinal));
            model.Rows.Single().OffsetMinutes.ShouldBe("+11.00"); await Render(window, "runtime-skewed"); Text(window).ShouldContain("exchange blocked");
            clock.Jump(TimeSpan.FromMinutes(-11)); await using var connected = await a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
            await Until(() => model.Rows.Count == 1 && model.Rows[0].Reason == "Clock accepted");
            model.Rows.Single().Connection.ShouldBe("Connected"); await Render(window, "runtime-recovered"); Text(window).ShouldNotContain("exchange blocked"); Text(window).ShouldContain("Clock accepted");
            await host.DisposeAsync(); await Until(() => model.Connection.Contains("unavailable", StringComparison.Ordinal));
            model.Rows.ShouldBeEmpty(); await Render(window, "service-unavailable"); Text(window).ShouldNotContain("Clock accepted"); Text(window).ShouldContain("reconnecting");
        }
        finally { stop.Cancel(); await watching.WaitAsync(TimeSpan.FromSeconds(5), Token); window.Close(); }
        host.ActiveSubscriptions.ShouldBe(0);
    });

    [Fact]
    public Task InstalledHostQuicClockSkewAndReconnectReachVerifiedPipeAndRenderedWpf() => OnUiAsync(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-InstalledClockUi-" + Guid.NewGuid().ToString("N"));
        string leftData = Path.Combine(root, "left", "data"), rightData = Path.Combine(root, "right", "data");
        string leftShare = Path.Combine(root, "left", "share"), rightShare = Path.Combine(root, "right", "share");
        Directory.CreateDirectory(leftData); Directory.CreateDirectory(rightData);
        Directory.CreateDirectory(leftShare); Directory.CreateDirectory(rightShare);
        const string contents = "installed host clock recovery reaches the real control UI";
        File.WriteAllText(Path.Combine(leftShare, "recovery.txt"), contents);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string actorSid = identity.User!.Value;
            using var leftCertificate = CreateDeviceCertificate();
            using var rightCertificate = CreateDeviceCertificate();
            byte[] leftId = PeerTls.DeviceId(leftCertificate), rightId = PeerTls.DeviceId(rightCertificate);
            using var folderKeys = FolderKeys.CreateNew();
            int leftPort = ReservePeerPort(), rightPort = ReservePeerPort();
            string leftState = Path.Combine(leftData, "peersync.db"), rightState = Path.Combine(rightData, "peersync.db");
            await PrepareInstalledNodeAsync(leftState, leftData, leftShare, actorSid, folderKeys, rightId, timeout.Token);
            await PrepareInstalledNodeAsync(rightState, rightData, rightShare, actorSid, folderKeys, leftId, timeout.Token);

            var leftStatus = new ControlStatusRuntime(); var rightStatus = new ControlStatusRuntime();
            var rightClock = new Clock(); rightClock.Jump(TimeSpan.FromMinutes(11));
            using var leftJournal = new DiagnosticJournal(Path.Combine(root, "left-logs"), "clock_ui_test", debugEnabled: true);
            using var rightJournal = new DiagnosticJournal(Path.Combine(root, "right-logs"), "clock_ui_test", debugEnabled: true);
            await using var leftHost = await InstalledFolderHost.StartForTestWithNetworkAsync(leftState, leftId, actorSid,
                leftCertificate, new DeviceSettings { ListenPort = leftPort, LanDiscovery = false }, leftStatus,
                leftJournal, cancellationToken: timeout.Token);
            await using var rightHost = await InstalledFolderHost.StartForTestWithNetworkAsync(rightState, rightId, actorSid,
                rightCertificate, new DeviceSettings { ListenPort = rightPort, LanDiscovery = false }, rightStatus,
                rightJournal, timeProvider: rightClock, cancellationToken: timeout.Token);

            string pipe = "PeerSync.Control.InstalledClockUi-" + Guid.NewGuid().ToString("N");
            await using var controlHost = await ControlApiHost.StartFixtureAsync(pipe, leftStatus, [identity.User!], cancellationToken: timeout.Token);
            var model = new ClockStatusViewModel(); var window = Window(model); window.Show();
            CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var controller = new ClockStatusController(model,
                (action, token) => window.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task,
                token => Task.FromResult<IControlStatusConnection>(new VerifiedStatusConnection(
                    ControlStatusClient.CreateFixture(new(pipe, identity.User!, (uint)Environment.ProcessId)))));
            var watching = controller.RunAsync(stop.Token);
            try
            {
                await leftHost.ConnectToPeerForTestAsync(new IPEndPoint(IPAddress.Loopback, rightPort), timeout.Token);
                await Until(() => model.Rows.Any(row => row.Reason.Contains("exchange blocked", StringComparison.Ordinal)));
                model.Rows.ShouldContain(row => row.Reason.Contains("exchange blocked", StringComparison.Ordinal)
                    && row.OffsetMinutes == "+11.00");
                model.Availability.ShouldContain("not available");
                // Freeze the verified real-pipe snapshot while rendering the rejection;
                // automatic retries can replace it with newer samples between frames.
                stop.Cancel();
                await watching.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                await Render(window, "installed-runtime-skewed");
                Text(window).ShouldContain("exchange blocked");
                Text(window).ShouldContain("+11.00");

                rightClock.Jump(TimeSpan.FromMinutes(-11));
                string receivedPath = Path.Combine(rightShare, "recovery.txt");
                stop.Dispose(); stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                watching = controller.RunAsync(stop.Token);
                await Until(() => model.Rows.Any(row => row.Reason == "Clock accepted"
                    && row.Connection == "Connected"), TimeSpan.FromSeconds(35));
                await Until(() => FileMatches(receivedPath, contents), TimeSpan.FromSeconds(35));
                model.Availability.ShouldContain("available");
                await Render(window, "installed-runtime-recovered");
                Text(window).ShouldContain("Clock accepted");
                Text(window).ShouldContain("0.00");
                Text(window).ShouldNotContain("exchange blocked");

                await controlHost.DisposeAsync();
                await Until(() => model.Connection.Contains("reconnecting", StringComparison.Ordinal));
                await Render(window, "installed-runtime-control-reconnecting");
                Text(window).ShouldContain("reconnecting");
            }
            finally
            {
                stop.Cancel();
                await watching.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                stop.Dispose();
                window.Close();
            }
            controlHost.ActiveSubscriptions.ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    });
    [Fact]
    public Task ActualWpfControlsRenderMissingInvalidNegativeOffsetAndTruncatedStates() => OnUiAsync(async () =>
    {
        var model = new ClockStatusViewModel(); var window = Window(model); window.Show();
        try
        {
            long generation = model.BeginAttempt(); var epoch = Guid.NewGuid(); ulong sequence = 0;
            foreach (var state in new[] { PeerClockState.AwaitingHello, PeerClockState.InvalidTimestamp, PeerClockState.InvalidElapsed, PeerClockState.Skewed })
            {
                model.Apply(generation, ClockStatusTests.Snapshot(epoch, ++sequence, state, state == PeerClockState.Skewed ? -11 : null));
                await Render(window, "typed-" + state); Text(window).ShouldContain(model.Rows.Single().Reason); Text(window).ShouldContain(model.Rows.Single().OffsetMinutes);
            }
            model.Apply(generation, new(epoch, ++sequence, false, [], true)); await Render(window, "typed-truncated"); Text(window).ShouldContain("omitted");
        }
        finally { window.Close(); }
    });
    private static ClockStatusWindow Window(ClockStatusViewModel model) => new(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
    private static async Task Render(Window window, string name)
    {
        HwndSource? source = PresentationSource.FromVisual(window) as HwndSource;
        if (source is not null) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte[] title = new byte[280 * 40 * 4]; bitmap.CopyPixels(new Int32Rect(20, 20, 280, 40), title, 280 * 4, 0);
        int dark = 0, white = 0;
        for (int offset = 0; offset < title.Length; offset += 4)
        {
            if (title[offset + 3] != 255) continue;
            if (title[offset] < 128 && title[offset + 1] < 128 && title[offset + 2] < 128) dark++;
            if (title[offset] > 240 && title[offset + 1] > 240 && title[offset + 2] > 240) white++;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "plan.md"))) root = root.Parent;
        if (root is null) throw new IOException("Cannot locate render evidence directory."); string directory = Path.Combine(root.FullName, "artifacts", "clock-ui"); Directory.CreateDirectory(directory);
        using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
        string renderState = $"window visible={window.IsVisible}, loaded={window.IsLoaded}, source={(source is null ? "missing" : "present")}, content={window.Content?.GetType().Name}";
        dark.ShouldBeGreaterThan(100, $"Rendered title crop contained {dark} dark and {white} white opaque pixels ({bitmap.PixelWidth}x{bitmap.PixelHeight}); {renderState}; screenshot saved to {Path.Combine(directory, name + ".png")}");
        white.ShouldBeGreaterThan(5000, $"Rendered title crop contained {dark} dark and {white} white opaque pixels ({bitmap.PixelWidth}x{bitmap.PixelHeight}); {renderState}; screenshot saved to {Path.Combine(directory, name + ".png")}");
    }
    private static string Text(DependencyObject root)
    {
        var text = new List<string>(); Visit(root); return string.Join('\n', text);
        void Visit(DependencyObject node)
        { if (node is TextBlock block) text.Add(block.Text); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i)); }
    }
    private static Task OnUiAsync(Func<Task> work)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            { try { await work(); finished.TrySetResult(); } catch (Exception error) { finished.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return finished.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
    }
    private async Task<NodeState> Node(ulong value, TimeProvider? clock = null)
    {
        string directory = Path.Combine(fixture, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
        var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token); byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, value);
        var engine = new TransportFolder(root, index, keys, id, timeProvider: clock); await engine.RunPassAsync(Token); return new(index, engine, id);
    }
    private static async Task Until(Func<bool> predicate, TimeSpan? duration = null)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(duration ?? TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private sealed record NodeState(FolderIndex Index, TransportFolder Engine, byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    private sealed class Clock : TimeProvider
    { private long offset; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset)); internal void Jump(TimeSpan duration) => Interlocked.Add(ref offset, duration.Ticks); }
    private static X509Certificate2 CreateDeviceCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync installed clock UI test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "installed-clock-ui"), "installed-clock-ui",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    private static async Task PrepareInstalledNodeAsync(string statePath, string dataPath, string share, string actorSid,
        FolderKeys folderKeys, byte[] remoteId, CancellationToken token)
    {
        await using (var state = await StateStore.OpenAsync(statePath, cancellationToken: token))
            await state.RegisterFolderAsync(folderKeys, share, new FolderSettings
            { RescanIntervalSeconds = 3600, RequireApproval = true }, actorSid, cancellationToken: token);
        string indexPath = Path.Combine(dataPath, "folders", Convert.ToHexString(folderKeys.FolderId()) + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await using var index = await FolderIndex.OpenAsync(indexPath, token);
        await index.Devices.UpsertAsync([new DeviceRow(remoteId, Approval: DeviceApproval.Approved, Pinned: true)], token);
    }
    private static int ReservePeerPort()
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            // Stay below the Windows dynamic/Hyper-V exclusion ranges, then verify
            // the exact port is bindable on both transports before releasing it.
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
        throw new SocketException((int)SocketError.AddressNotAvailable);
    }
    private static bool FileMatches(string path, string expected)
    { try { return File.Exists(path) && File.ReadAllText(path) == expected; } catch (IOException) { return false; } }
    public void Dispose()
    {
        keys.Dispose(); if (!Directory.Exists(fixture)) return; string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ClockUi-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        Directory.Delete(resolved, true);
    }
}
