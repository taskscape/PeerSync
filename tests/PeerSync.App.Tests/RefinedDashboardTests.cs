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
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class RefinedDashboardTests
{
    private static readonly string[] FolderMenuLabels = ["Open folder", "Share folder…", "Devices & activity", "Previous versions", "Folder settings", "Stop syncing…"];
    private static readonly string[] SingleUnsupported = ["Not supported yet"];
    private static readonly string[] TripleUnsupported = ["Not supported yet", "Not supported yet", "Not supported yet"];
    private static readonly string[] DoubleUnsupported = ["Not supported yet", "Not supported yet"];
    private static readonly string[] PauseOptions = ["Pause for 30 minutes", "Pause for 1 hour", "Pause until resumed", "Resume all"];
    private static readonly string[] PreferenceTextIds = ["DeviceName", "ListenPort", "UploadLimit", "DownloadLimit", "RendezvousServers"];
    private static readonly string[] PreferenceCheckIds = ["LanDiscovery", "PortMapping", "RelayFallback", "AzureDiagnostics", "FullDiagnostics"];
    private static readonly string[] UnitIds = ["UploadLimitUnit", "DownloadLimitUnit"];
    private static readonly string[] OverviewActionIds = ["AddFolder", "JoinFolder", "PauseAll", "TransferDetails"];
    private static readonly string[] ApprovalActionIds = ["ApproveDevice", "DenyDevice", "BlockDevice"];
    [Fact]
    public Task OverviewExposesFolderCommandsAndUnsupportedActionsDoNotChangeState() => OnUi(async () =>
    {
        using var fixture = new Fixture(); var window = fixture.Window; var model = fixture.Model;
        await Ready(window);
        VisibleText(window).ShouldContain("Your folders, at a glance");
        VisibleText(window).ShouldContain("Waiting for a device");
        VisibleText(window).ShouldNotContain("Up to date");
        Id<Button>(window, "AddFolder").IsVisible.ShouldBeTrue();
        Id<Button>(window, "JoinFolder").IsVisible.ShouldBeTrue();
        var list = Id<ListBox>(window, "FoldersList");
        list.Items.Count.ShouldBe(4);
        var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var actions = Id<Button>(firstRow, "FolderActions");
        Click(actions);
        var menu = actions.ContextMenu!;
        await Ready(window);
        SaveVisual(menu, "folder-menu.png");
        menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ShouldBe(FolderMenuLabels);
        var before = model.Folders.ToArray();
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        fixture.Messages.ShouldBe(SingleUnsupported); model.Folders.ShouldBe(before);
        menu.IsOpen = false;
        window.NavigateToFolder(before[0].Id, refresh: false);
        await Ready(window);
        Click(Id<Button>(window, "OpenFolder")); Click(Id<Button>(window, "ClockDetails"));
        fixture.Messages.ShouldBe(TripleUnsupported);
        model.Folders.ShouldBe(before);
        Click(Id<Button>(window, "AllFolders")); await Ready(window);
        Id<ListBox>(window, "FoldersList").IsVisible.ShouldBeTrue();
        var pause = Id<Button>(window, "PauseAll");
        pause.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ShouldBe(PauseOptions);
        Save(window, "overview.png");
    });

    [Fact]
    public Task VersionsKeepsBothSourcesSelectionAndRecoveryActions() => OnUi(async () =>
    {
        using var fixture = new Fixture(); var window = fixture.Window; var model = fixture.Model;
        window.NavigateToFolder("A", tab: 1, refresh: false);
        await Ready(window);
        var archive = Id<DataGrid>(window, "ArchivedVersions");
        archive.Items.Count.ShouldBe(2);
        Id<Button>(window, "RestoreVersion").IsEnabled.ShouldBeFalse();
        archive.SelectedIndex = 0;
        await Ready(window);
        Id<Button>(window, "RestoreAsCopy").IsEnabled.ShouldBeTrue();
        Id<Button>(window, "RestoreVersion").Content.ShouldBe("Replace current file…");
        Save(window, "versions.png");
        var filter = Id<ComboBox>(window, "VersionFilter"); filter.SelectedIndex = 1;
        await Ready(window);
        var overwritten = Id<DataGrid>(window, "OverwrittenVersions");
        overwritten.IsVisible.ShouldBeTrue(); archive.IsVisible.ShouldBeFalse();
        overwritten.Items.Count.ShouldBe(1);
        ((OverwriteItem)overwritten.Items[0]).FolderId.ShouldBe("A");
        Id<Button>(window, "RestoreVersion").IsEnabled.ShouldBeFalse();
        overwritten.SelectedIndex = 0;
        Id<Button>(window, "RestoreVersion").IsEnabled.ShouldBeTrue();
        filter.SelectedIndex = 0; archive.Items.Count.ShouldBe(2);
        model.ArchiveVersions.Count.ShouldBe(2);
    });

    [Fact]
    public Task PreferencesRoundTripAllCurrentAndUnexposedSettingsAndRejectUnsupportedUnits() => OnUi(async () =>
    {
        using var fixture = new Fixture(); var current = Settings();
        fixture.Window.ShowPreferences(current); await Ready(fixture.Window);
        var editor = Descendants<PreferencesEditor>(fixture.Window).Single();
        editor.TryGetValue(out var unchanged).ShouldBeTrue();
        unchanged.ToString().ShouldBe(current.ToString());
        unchanged.RendezvousServers.ShouldBe(current.RendezvousServers);
        unchanged.ControlUserSids.ShouldBe(current.ControlUserSids);
        foreach (string id in PreferenceTextIds)
            Id<TextBox>(editor, id).IsVisible.ShouldBeTrue();
        foreach (string id in PreferenceCheckIds)
            Id<CheckBox>(editor, id).IsVisible.ShouldBeTrue();
        foreach (string id in UnitIds)
        {
            var unit = Id<ComboBox>(editor, id); unit.SelectedIndex = 1;
            unit.SelectedIndex.ShouldBe(0);
        }
        fixture.Messages.ShouldBe(DoubleUnsupported);
        editor.TryGetValue(out var afterUnsupported).ShouldBeTrue();
        afterUnsupported.UploadLimitBytesPerSec.ShouldBe(current.UploadLimitBytesPerSec);
        afterUnsupported.DownloadLimitBytesPerSec.ShouldBe(current.DownloadLimitBytesPerSec);
        Save(fixture.Window, "settings.png");
        Id<TextBox>(editor, "UploadLimit").Text = "1.5";
        editor.TryGetValue(out _).ShouldBeFalse();
        fixture.Messages.Last().ShouldBe("Port and transfer limits must be whole numbers.");
        Id<TextBox>(editor, "UploadLimit").Text = "4096";
        Id<TextBox>(editor, "DeviceName").Text = "  Workstation  ";
        Id<CheckBox>(editor, "FullDiagnostics").IsChecked = true;
        editor.TryGetValue(out var changed).ShouldBeTrue();
        changed.DeviceName.ShouldBe("Workstation"); changed.UploadLimitBytesPerSec.ShouldBe(4096);
        changed.FullDiagnostics.ShouldBeTrue(); changed.PauseOnMeteredNetworks.ShouldBeTrue();
        changed.PauseDownloadsBelowFreeBytes.ShouldBe(current.PauseDownloadsBelowFreeBytes);
    });

    [Fact]
    public Task ApprovalAndFolderSettingsRemainAccessibleAtMinimumWindowSize() => OnUi(async () =>
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Ready(window);
        foreach (string id in OverviewActionIds)
            AssertInside(window, Id<Button>(window, id));
        var navigation = Id<TabControl>(window, "MainNavigation"); navigation.SelectedIndex = 1;
        await Ready(window);
        var requests = Id<DataGrid>(window, "PendingDevices");
        requests.Items.Count.ShouldBe(1);
        foreach (string id in ApprovalActionIds)
            Id<Button>(window, id).IsEnabled.ShouldBeFalse();
        requests.SelectedIndex = 0;
        foreach (string id in ApprovalActionIds)
        { Id<Button>(window, id).IsEnabled.ShouldBeTrue(); AssertInside(window, Id<Button>(window, id)); }
        window.NavigateToFolder("A", tab: 2, refresh: false); await Ready(window);
        Id<CheckBox>(window, "RequireApproval").ShouldNotBeNull();
        Id<CheckBox>(window, "KeepBothCopies").ShouldNotBeNull();
        Id<TextBox>(window, "ArchiveDays").ShouldNotBeNull();
        Id<TextBox>(window, "IgnoreRules").ShouldNotBeNull();
        Descendants<Expander>(window).Single().IsExpanded = true; await Ready(window);
        Id<TextBox>(window, "PredefinedHosts").ShouldNotBeNull();
        Save(window, "folder-settings-small.png");
    });

    [Fact]
    public Task LiveRequestsAndServiceFailuresRefreshTheOverview() => OnUi(async () =>
    {
        using var fixture = new Fixture(); var model = fixture.Model; var window = fixture.Window;
        await Ready(window);
        window.NavigateToFolder("A", refresh: false);
        fixture.Frame.Peers.Clear(); model.Apply(fixture.Frame); await Ready(window);
        Id<TabControl>(window, "FolderNavigation").IsVisible.ShouldBeTrue();
        model.SelectedFolderId.ShouldBe("A");
        Click(Id<Button>(window, "AllFolders")); await Ready(window);
        Id<Button>(window, "ReviewDevices").Content.ShouldBe("1 device awaiting approval");
        string activity = model.Activity;
        model.PendingDevices.Clear(); await Ready(window);
        model.Activity.ShouldBe(activity);
        Id<Button>(window, "ReviewDevices").Content.ShouldBe("0 devices awaiting approval");
        model.Disconnected(unauthorized: true); await Ready(window);
        Id<Button>(window, "AuthorizeAccount").IsVisible.ShouldBeTrue();
        Id<Button>(window, "StartService").IsVisible.ShouldBeFalse();
        Id<ListBox>(window, "FoldersList").Items.Count.ShouldBe(0);
        model.Disconnected(unauthorized: false); await Ready(window);
        Id<Button>(window, "StartService").IsVisible.ShouldBeTrue();
        Id<Button>(window, "AuthorizeAccount").IsVisible.ShouldBeFalse();
        Save(window, "service-unavailable.png");
    });

    [Fact]
    public Task SharingRetainsReadOnlyDefaultEncryptedKeysQrAndCapabilityRestrictions() => OnUi(async () =>
    {
        using var keys = FolderKeys.CreateNew();
        var share = new ShareWindow(keys) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        share.Show();
        try
        {
            await Ready(share);
            Id<TextBox>(share, "ShareKey").Text.ShouldBe(keys.ExportReadOnlyText());
            Descendants<Image>(share).Single().Source.ShouldNotBeNull();
            Click(Descendants<Button>(share).Single(button => Equals(button.Content, "Encrypted-only key")));
            Id<TextBox>(share, "ShareKey").Text.ShouldBe(keys.ExportEncryptedText());
            Click(Descendants<Button>(share).Single(button => Equals(button.Content, "Read-only key")));
            Id<TextBox>(share, "ShareKey").Text.ShouldBe(keys.ExportReadOnlyText());
            Descendants<Button>(share).ShouldContain(button => Equals(button.Content, "Reveal read-write key"));
        }
        finally { share.Close(); }
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var limited = new ShareWindow(readOnly) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        limited.Show();
        try
        {
            await Ready(limited);
            Descendants<Button>(limited).ShouldNotContain(button => Equals(button.Content, "Reveal read-write key"));
            Id<TextBox>(limited, "ShareKey").Text.ShouldBe(readOnly.ExportReadOnlyText());
        }
        finally { limited.Close(); }
    });

    [Fact]
    public Task ClosingStillHidesTheDashboardUntilExplicitExit() => OnUi(async () =>
    {
        using var fixture = new Fixture(); await Ready(fixture.Window);
        bool closed = false; fixture.Window.Closed += (_, _) => closed = true;
        fixture.Window.Close(); closed.ShouldBeFalse(); fixture.Window.IsVisible.ShouldBeFalse();
        fixture.Window.ShowAndActivate(); fixture.Window.IsVisible.ShouldBeTrue();
        fixture.Window.Exit(); closed.ShouldBeTrue();
    });

    [Fact]
    public void FolderPresentationNeverClaimsCompletionAndKeepsIncompleteStatusDistinct()
    {
        var folder = new FolderItem("A", @"C:\Projects\Example", "ReadWrite", "Active");
        folder.Name.ShouldBe("Example"); folder.StatusText.ShouldBe("Waiting for a device");
        (folder with { ConnectedPeers = 1 }).StatusText.ShouldBe("Connected");
        (folder with { CompleteSnapshot = false }).StatusText.ShouldBe("Status incomplete");
        (folder with { State = "Paused", ConnectedPeers = 2 }).PauseLabel.ShouldBe("Resume");
        (folder with { Path = @"C:\" }).Name.ShouldBe(@"C:\");
    }

    private static DeviceSettings Settings() => new()
    {
        DeviceName = "Alex's desktop", ListenPort = 52525, UploadLimitBytesPerSec = 12345, DownloadLimitBytesPerSec = 67890,
        LanDiscovery = true, UsePortMapping = false, UseRelay = true, SendDiagnosticsToAzure = true, FullDiagnostics = false,
        RendezvousServers = ["https://sync.taskscape.com:99"], ControlUserSids = ["test-sid"],
        LimitLanTraffic = true, PauseOnMeteredNetworks = true, PauseDownloadsBelowFreeBytes = 987654321,
    };

    private sealed class Fixture : IDisposable
    {
        private readonly DiagnosticJournal journal = new(Path.Combine(Path.GetTempPath(), "PeerSync-Skin-" + Guid.NewGuid().ToString("N")), "skin-tests", debugEnabled: true);
        internal DashboardViewModel Model { get; } = new();
        internal ControlEvent Frame { get; } = new() { FullSnapshot = true, Status = new ServiceStatus() };
        internal List<string> Messages { get; } = [];
        internal MainWindow Window { get; }
        internal Fixture()
        {
            var frame = Frame;
            frame.Folders.Add(new FolderInfo { FolderId = "A", Path = @"C:\Users\Alex\Documents\Projects", Access = "ReadWrite" });
            frame.Folders.Add(new FolderInfo { FolderId = "B", Path = @"D:\Photos", Access = "ReadWrite" });
            frame.Folders.Add(new FolderInfo { FolderId = "C", Path = @"C:\Users\Alex\Documents\Shared", Access = "ReadOnly" });
            frame.Folders.Add(new FolderInfo { FolderId = "D", Path = @"D:\Backups", Access = "ReadWrite", Paused = true });
            frame.Peers.Add(new FolderPeerInfo { FolderId = "A", Peer = new PeerInfo { DeviceId = "one", Name = "Office PC", Transport = "Quic", Approval = "Approved" } });
            frame.Peers.Add(new FolderPeerInfo { FolderId = "B", Peer = new PeerInfo { DeviceId = "two", Name = "Home laptop", Transport = "Relay", Approval = "Approved" } });
            frame.PendingDevices.Add(new PendingDeviceInfo { FolderId = "A", DeviceId = new string('F', 64), Name = "New laptop", RequestedUtcTicks = DateTime.UtcNow.Ticks });
            frame.Overwrites.Add(new ArchiveInfo { FolderId = "A", Id = 7, OriginalPath = "Proposal.docx", ArchivedUtcTicks = DateTime.UtcNow.Ticks, Size = 284 * 1024, Overwritten = true });
            frame.Overwrites.Add(new ArchiveInfo { FolderId = "B", Id = 8, OriginalPath = "Notes.txt", ArchivedUtcTicks = DateTime.UtcNow.Ticks, Size = 4 * 1024, Overwritten = true });
            Model.Apply(frame);
            var archive = new ListArchiveResponse { FolderId = "A" };
            archive.Versions.Add(frame.Overwrites[0]);
            archive.Versions.Add(new ArchiveInfo { FolderId = "A", Id = 9, OriginalPath = "Budget.xlsx", ArchivedUtcTicks = DateTime.UtcNow.Ticks, Size = 96 * 1024 });
            Model.ApplyArchive(archive);
            Window = new MainWindow(Model, journal, false) { Width = 1440, Height = 1024, Left = -20000, Top = -20000, ShowInTaskbar = false, InformationMessage = Messages.Add };
            Window.Show();
            if (PresentationSource.FromVisual(Window) is HwndSource source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        }
        public void Dispose() { Window.Exit(); journal.Dispose(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Ready(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<string> VisibleText(DependencyObject root) => Descendants<TextBlock>(root).Where(item => item.IsVisible).Select(item => item.Text);
    private static T Id<T>(DependencyObject root, string id) where T : DependencyObject => Descendants<T>(root).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void AssertInside(Window window, FrameworkElement element)
    {
        element.IsVisible.ShouldBeTrue();
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        bounds.Left.ShouldBeGreaterThanOrEqualTo(0); bounds.Right.ShouldBeLessThanOrEqualTo(window.ActualWidth);
        bounds.Bottom.ShouldBeLessThanOrEqualTo(window.ActualHeight);
    }
    private static void Save(Window window, string name)
        => SaveVisual((FrameworkElement)window.Content, name);

    private static void SaveVisual(FrameworkElement content, string name)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "PeerSync.slnx"))) repo = repo.Parent;
        string path = Path.Combine(repo!.FullName, "artifacts", "refined-ui"); Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, name)); encoder.Save(output);
    }
    private static Task OnUi(Func<Task> work)
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
