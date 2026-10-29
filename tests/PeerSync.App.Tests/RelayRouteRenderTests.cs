using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts.V1;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class RelayRouteRenderTests
{
    [Fact]
    public Task PeerRouteRendersRelayAndThenDirectQuic() => OnUiAsync(async () =>
    {
        string folderId = new('A', 64);
        string deviceId = new('B', 64);
        var model = new DashboardViewModel();
        model.Apply(Snapshot("Relay"));
        string journalPath = Path.Combine(Path.GetTempPath(),
            "PeerSync-RelayRouteRender-" + Guid.NewGuid().ToString("N"));
        using var journal = new DiagnosticJournal(journalPath, "relay-route-render-test", debugEnabled: true);
        MainWindow CreateWindow() => new(model, journal, startInTray: false)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000 };
        var window = CreateWindow();
        window.Show();
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        try
        {
            await AssertRouteAsync(window, "via relay", "relay-route.png");
            model.Apply(Snapshot("Quic"));
            await AssertRouteAsync(window, "direct QUIC", null);
            var freshWindow = CreateWindow();
            freshWindow.Show();
            try { await AssertRouteAsync(freshWindow, "direct QUIC", "direct-route.png"); }
            finally { freshWindow.Exit(); }
        }
        finally { window.Exit(); }

        ControlEvent Snapshot(string transport)
        {
            var update = new ControlEvent
            { FullSnapshot = true, Status = new ServiceStatus { InstanceId = Guid.NewGuid().ToString("N"), Sequence = 1 } };
            update.Folders.Add(new FolderInfo { FolderId = folderId,
                Path = @"C:\RelayRouteRenderFixture", Access = "ReadWrite" });
            update.Peers.Add(new FolderPeerInfo { FolderId = folderId,
                Peer = new PeerInfo { DeviceId = deviceId, Name = "Second PC",
                    Approval = "Approved", Transport = transport } });
            return update;
        }

        async Task AssertRouteAsync(MainWindow renderedWindow, string expected, string? imageName)
        {
            renderedWindow.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = Descendants<DataGrid>(renderedWindow).Single();
            grid.IsVisible.ShouldBeTrue();
            grid.Items.Count.ShouldBe(1);
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            row.IsVisible.ShouldBeTrue();
            Descendants<TextBlock>(row).Where(text => text.IsVisible)
                .Select(text => text.Text).ShouldContain(expected);
            if (imageName is null) return;

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(renderedWindow.ActualWidth),
                (int)Math.Ceiling(renderedWindow.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(renderedWindow);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int opaque = 0, light = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 3] != 255) continue;
                opaque++;
                if (pixels[offset] > 240 && pixels[offset + 1] > 240 && pixels[offset + 2] > 240) light++;
            }
            opaque.ShouldBeGreaterThan(10_000);
            light.ShouldBeGreaterThan(5_000);

            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "plan.md")))
                repository = repository.Parent;
            if (repository is null) throw new IOException("Cannot locate relay route evidence directory.");
            string evidence = Path.Combine(repository.FullName, "artifacts", "relay-route-ui");
            Directory.CreateDirectory(evidence);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(evidence, imageName));
            encoder.Save(output);
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static Task OnUiAsync(Func<Task> work)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await work(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
