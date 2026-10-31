using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts;
using PeerSync.Control.Contracts.V1;
using PeerSync.Core.Diagnostics;

namespace PeerSync.InstalledProbe;

/// <summary>Opt-in native Archive UI exercise against the fixed SCM service.</summary>
internal static class InstalledArchiveUiProbe
{
    internal static Task RunAsync() => OnUiAsync(async () =>
    {
        string root = Path.Combine(@"C:\PeerSync-Test", "archive-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "notes.txt");
        string copy = Path.Combine(root, "restored-copy.txt");
        await File.WriteAllTextAsync(original, "before");
        await FolderAccessGrant.GrantAsync(root, null, CancellationToken.None);

        using var client = ControlStatusClient.CreateForInstalledService();
        FolderInfo? folder = null;
        MainWindow? window = null;
        try
        {
            folder = await client.AddFolderAsync(root);
            await UntilAsync(async () =>
            {
                await foreach (IndexEntry row in client.DumpIndexAsync(folder.FolderId))
                    if (row.Path == "notes.txt" && !row.Deleted) return true;
                return false;
            });
            await File.WriteAllTextAsync(original, "after");
            ListArchiveResponse archive = null!;
            await UntilAsync(async () =>
            {
                archive = await client.ListArchiveAsync(folder.FolderId);
                return archive.Versions.Any(version => version.OriginalPath == "notes.txt");
            });

            var model = new DashboardViewModel { ServiceAvailable = true,
                Banner = "Connected to PeerSync service", Activity = "1 folder · 0 peers" };
            model.Folders.Add(new FolderItem(folder.FolderId, root, "ReadWrite", "Active"));
            model.SelectedFolderId = folder.FolderId;
            string logs = root + "-logs";
            using var journal = new DiagnosticJournal(logs, "installed-archive-ui-test", debugEnabled: true);
            window = new MainWindow(model, journal, startInTray: false)
            { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            window.Show();
            var outer = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
            var foldersPage = (Grid)((TabItem)outer.Items[0]).Content;
            var inner = foldersPage.Children.OfType<TabControl>().Single();
            inner.SelectedItem = inner.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Archive"));
            await UntilAsync(() => Task.FromResult(model.ArchiveVersions.Any(item => item.Path == "notes.txt")));
            Require(model.ArchiveVersions.First(item => item.Path == "notes.txt").SizeDisplay == "6 B",
                "The archived six-byte file did not render its exact size.");
            var grid = Descendants<DataGrid>(window).Single(item =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(item) == "ArchivedVersions");
            grid.SelectedItem = model.ArchiveVersions.First(item => item.Path == "notes.txt");
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int opaque = 0;
            for (int offset = 3; offset < pixels.Length; offset += 4)
                if (pixels[offset] == 255) opaque++;
            Require(opaque > 10_000, "Archive UI did not render opaque pixels.");
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "plan.md")))
                repository = repository.Parent;
            if (repository is null) throw new IOException("Cannot locate Archive evidence directory.");
            string evidence = Path.Combine(repository.FullName, "artifacts", "archive-ui");
            Directory.CreateDirectory(evidence);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(evidence, "installed-archive-tab.png"))) encoder.Save(output);
            var buttons = Descendants<Button>(window).Where(item => item.IsVisible).ToArray();
            Button restore = buttons.Single(item => Equals(item.Content, "Restore"));
            Button restoreCopy = buttons.Single(item => Equals(item.Content, "Restore as copy"));

            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(async () => await File.ReadAllTextAsync(original) == "before");
            Require(model.Activity == "Archived version restored.", "The Restore action did not complete.");
            grid.SelectedItem = model.ArchiveVersions.First(item => item.Path == "notes.txt");

            var promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            bool promptSubmitted = false;
            promptTimer.Tick += (_, _) =>
            {
                TextPromptWindow? prompt = window.OwnedWindows.OfType<TextPromptWindow>().FirstOrDefault();
                if (prompt is null) return;
                TextBox input = Descendants<TextBox>(prompt).Single();
                input.Text = "restored-copy.txt";
                Descendants<Button>(prompt).Single(button => Equals(button.Content, "Continue"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                promptSubmitted = true;
                promptTimer.Stop();
            };
            promptTimer.Start();
            restoreCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(promptSubmitted));
            promptTimer.Stop();
            await UntilAsync(() => Task.FromResult(File.Exists(copy)));
            Require(await File.ReadAllTextAsync(copy) == "before", "The restored copy had the wrong bytes.");
            Require(model.Activity == "Archived version restored as a copy.", "The copy action did not complete.");
        }
        finally
        {
            window?.Exit();
            if (folder is not null)
                Require((await client.RemoveFolderAsync(folder.FolderId)).Completed,
                    "The temporary service folder registration was not removed.");
        }
    });

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        throw new TimeoutException("Installed Archive UI did not reach the expected state.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
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
        return finished.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }
}
