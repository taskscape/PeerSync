using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts.V1;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class ArchiveVisualTreeTests
{
    [Fact]
    public Task ArchiveTabExposesVersionsActionsAndTruncation() => OnUiAsync(async () =>
    {
        string folderId = new('A', 64);
        var model = new DashboardViewModel();
        model.Folders.Add(new FolderItem(folderId, @"C:\ArchiveFixture", "ReadWrite", "Active"));
        model.SelectedFolderId = folderId;
        var response = new ListArchiveResponse { FolderId = folderId, Truncated = true };
        response.Versions.Add(new ArchiveInfo { FolderId = folderId, Id = 7, OriginalPath = "notes.txt",
            ArchivedUtcTicks = DateTime.UtcNow.Ticks, Size = 1_048_576 });
        model.ApplyArchive(response);

        string evidence = Path.Combine(Path.GetTempPath(), "PeerSync-ArchiveUi-" + Guid.NewGuid().ToString("N"));
        using var journal = new DiagnosticJournal(evidence, "archive-ui-test", debugEnabled: true);
        var window = new MainWindow(model, journal, startInTray: false)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        try
        {
            var outer = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
            var foldersPage = (Grid)((TabItem)outer.Items[0]).Content;
            var inner = foldersPage.Children.OfType<TabControl>().Single();
            inner.SelectedItem = inner.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Archive"));
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var grid = Descendants<DataGrid>(window).Single(item => AutomationProperties.GetAutomationId(item) == "ArchivedVersions");
            grid.IsVisible.ShouldBeTrue();
            grid.Items.Count.ShouldBe(1);
            ((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)).IsVisible.ShouldBeTrue();
            var text = Descendants<TextBlock>(window).Select(item => item.Text).ToArray();
            text.ShouldContain("notes.txt");
            text.ShouldContain(model.ArchiveVersions.Single().SizeDisplay);
            text.ShouldContain(item => item.Contains("more versions exist", StringComparison.Ordinal));
            var buttons = Descendants<Button>(window).Where(item => item.IsVisible).Select(item => item.Content?.ToString()).ToArray();
            buttons.ShouldContain("Restore"); buttons.ShouldContain("Restore as copy");
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
