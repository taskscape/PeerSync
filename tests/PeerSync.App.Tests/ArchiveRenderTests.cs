using System.IO;
using System.Windows;
using System.Windows.Automation;
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

public sealed class ArchiveRenderTests
{
    [Fact]
    public Task ArchiveTabRendersVersionDateAndRestoreActions() => OnUiAsync(async () =>
    {
        string folderId = new('A', 64);
        var model = new DashboardViewModel();
        model.Folders.Add(new FolderItem(folderId, @"C:\ArchiveRenderFixture", "ReadWrite", "Active"));
        model.SelectedFolderId = folderId;
        var response = new ListArchiveResponse { FolderId = folderId, Truncated = true };
        response.Versions.Add(new ArchiveInfo { FolderId = folderId, Id = 7,
            OriginalPath = "notes.txt", ArchivedUtcTicks = DateTime.UtcNow.Ticks, Size = 1_048_576 });
        model.ApplyArchive(response);

        string journalPath = Path.Combine(Path.GetTempPath(), "PeerSync-ArchiveRender-" + Guid.NewGuid().ToString("N"));
        using var journal = new DiagnosticJournal(journalPath, "archive-render-test", debugEnabled: true);
        var window = new MainWindow(model, journal, startInTray: false)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        try
        {
            var outer = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
            var foldersPage = (Grid)((TabItem)outer.Items[0]).Content;
            var inner = foldersPage.Children.OfType<TabControl>().Single();
            inner.SelectedItem = inner.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Archive"));
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var grid = Descendants<DataGrid>(window).Single(item =>
                AutomationProperties.GetAutomationId(item) == "ArchivedVersions");
            grid.IsVisible.ShouldBeTrue();
            ((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)).IsVisible.ShouldBeTrue();
            string[] visible = Descendants<TextBlock>(window).Where(item => item.IsVisible)
                .Select(item => item.Text).ToArray();
            visible.ShouldContain("notes.txt");
            visible.ShouldContain(model.ArchiveVersions.Single().ArchivedLocal);
            visible.ShouldContain(item => item.Contains("more versions exist", StringComparison.Ordinal));
            string[] actions = Descendants<Button>(window).Where(item => item.IsVisible)
                .Select(item => item.Content?.ToString() ?? "").ToArray();
            actions.ShouldContain("Restore"); actions.ShouldContain("Restore as copy");

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int opaque = 0, dark = 0, light = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 3] != 255) continue;
                opaque++;
                if (pixels[offset] < 128 && pixels[offset + 1] < 128 && pixels[offset + 2] < 128) dark++;
                if (pixels[offset] > 240 && pixels[offset + 1] > 240 && pixels[offset + 2] > 240) light++;
            }
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "plan.md")))
                repository = repository.Parent;
            if (repository is null) throw new IOException("Cannot locate Archive render evidence directory.");
            string evidence = Path.Combine(repository.FullName, "artifacts", "archive-ui");
            Directory.CreateDirectory(evidence);
            string image = Path.Combine(evidence, "archive-tab.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(image)) encoder.Save(output);
            opaque.ShouldBeGreaterThan(10_000, $"Archive render had {opaque} opaque pixels; saved {image}");
            dark.ShouldBeGreaterThan(500, $"Archive render had {dark} dark pixels; saved {image}");
            light.ShouldBeGreaterThan(5_000, $"Archive render had {light} light pixels; saved {image}");
        }
        finally { window.Exit(); }
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
