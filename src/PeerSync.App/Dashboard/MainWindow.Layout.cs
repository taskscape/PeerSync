using Binding = System.Windows.Data.Binding;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using SystemColors = System.Windows.SystemColors;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PeerSync.Core.Settings;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Orientation = System.Windows.Controls.Orientation;
using TabControl = System.Windows.Controls.TabControl;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace PeerSync.App.Dashboard;

internal sealed partial class MainWindow
{
    private readonly TabControl navigation = new();
    private readonly TabControl folderTabs = new();
    private readonly DockPanel overview = new();
    private readonly DockPanel folderDetails = new() { Visibility = Visibility.Collapsed };
    private readonly ContentControl preferences = new();
    private readonly TextBlock folderTitle = DashboardStyle.Text("Folder", 28, true);
    private readonly TextBlock folderPath = DashboardStyle.Text("");
    private readonly TextBlock overviewSummary = DashboardStyle.Text("Manage the folders shared by this PC.");
    private readonly TextBlock attentionText = DashboardStyle.Text("");
    private readonly TextBlock attentionTitle = DashboardStyle.Text("", 17, true);
    private readonly Button waitingDetails = new() { Content = "View details" };
    private readonly Border attention = new();
    private readonly TextBlock emptyFolders = DashboardStyle.Text("No folders yet. Add a folder on this PC, or join with a key from another PC.", 17);
    private readonly ComboBox versionFilter = new();
    private readonly Grid versionLists = new();
    private readonly Button folderPause = new();
    private readonly Button approvalsLink = new();
    private readonly Button overwritesLink = new();
    private readonly TextBlock approvalsTitle = DashboardStyle.Text("Device approvals", 28, true);
    private bool loadingPreferences;
    private bool navigatingToFolder;
    private bool overviewRefreshQueued;
    internal Action<string>? InformationMessage { get; init; }

    private void ShowInformation(string message)
    {
        if (InformationMessage is { } show) show(message);
        else MessageBox.Show(this, message, "PeerSync", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowUnsupported() => ShowInformation("Not supported yet");

    private void BuildShell()
    {
        var root = new DockPanel { Background = Background }; Content = root;
        var footer = new Border { BorderBrush = DashboardStyle.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 8, 24, 8) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var status = new DockPanel(); footer.Child = status;
        var transfer = UiButton("Transfer details", "TransferDetails", ShowTransferDetails);
        DashboardStyle.Link(transfer); DockPanel.SetDock(transfer, Dock.Right); status.Children.Add(transfer);
        status.Children.Add(BoundText(nameof(model.Banner), "ServiceBanner", 13));

        if (!SystemParameters.HighContrast) navigation.SetResourceReference(StyleProperty, "MainNavigation");
        DashboardStyle.Identify(navigation, "MainNavigation");
        root.Children.Add(navigation);
        navigation.Items.Add(new TabItem { Header = "Overview", Content = BuildFoldersPage() });
        navigation.Items.Add(new TabItem { Header = "Device approvals", Content = BuildPendingPage() });
        navigation.Items.Add(new TabItem { Header = "Settings", Content = preferences });
        navigation.SelectionChanged += async (_, args) =>
        {
            if (!ReferenceEquals(args.Source, navigation)) return;
            if (navigation.SelectedIndex == 2) await LoadPreferencesPageAsync();
        };
        preferences.Margin = new Thickness(32, 24, 32, 24);
        ShowPreferencesUnavailable();
        UpdateOverview();
    }

    private Grid BuildFoldersPage()
    {
        var page = new Grid { Margin = new Thickness(32, 24, 32, 20) };
        page.Children.Add(overview); page.Children.Add(folderDetails);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); overview.Children.Add(top);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        titles.Children.Add(DashboardStyle.Text("Your folders, at a glance", 28, true));
        overviewSummary.Foreground = DashboardStyle.Muted; overviewSummary.Margin = new Thickness(0, 8, 0, 0);
        titles.Children.Add(overviewSummary); heading.Children.Add(titles);
        var commands = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetColumn(commands, 1); heading.Children.Add(commands);
        var addBar = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var add = ActionButton("Add folder", AddFolderAsync); DashboardStyle.Identify(add, "AddFolder"); DashboardStyle.Primary(add);
        addBar.Children.Add(add);
        var join = ActionButton("Join with key", JoinFolderAsync); DashboardStyle.Identify(join, "JoinFolder"); addBar.Children.Add(join);
        commands.Children.Add(addBar);
        var pauseAll = UiButton("Pause all ▾", "PauseAll", () => { });
        pauseAll.Margin = new Thickness(0, 8, 8, 0); pauseAll.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        pauseAll.ContextMenu = CreateMenu();
        AddMenu(pauseAll.ContextMenu, "Pause for 30 minutes", () => PauseAllAsync(1800));
        AddMenu(pauseAll.ContextMenu, "Pause for 1 hour", () => PauseAllAsync(3600));
        AddMenu(pauseAll.ContextMenu, "Pause until resumed", () => PauseAllAsync(0));
        AddMenu(pauseAll.ContextMenu, "Resume all", ResumeAllAsync);
        pauseAll.Click += (_, _) => OpenMenu(pauseAll); commands.Children.Add(pauseAll);
        top.Children.Add(heading);

        attention.Padding = new Thickness(18); attention.Margin = new Thickness(0, 20, 0, 22);
        attention.BorderThickness = new Thickness(1); attention.CornerRadius = new CornerRadius(4);
        attention.BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(235, 204, 145));
        attention.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(255, 248, 233));
        var notice = new DockPanel(); attention.Child = notice;
        var recovery = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(recovery, Dock.Right); notice.Children.Add(recovery);
        authorize.Click += async (_, _) => await ExecuteAsync(() => ElevatedActions.AuthorizeCurrentUserAsync());
        startService.Click += async (_, _) => await ExecuteAsync(() => ElevatedActions.StartServiceAsync());
        DashboardStyle.Identify(authorize, "AuthorizeAccount"); DashboardStyle.Identify(startService, "StartService");
        recovery.Children.Add(authorize); recovery.Children.Add(startService);
        waitingDetails.Margin = new Thickness(18, 0, 0, 0); waitingDetails.VerticalAlignment = VerticalAlignment.Center;
        DashboardStyle.Identify(waitingDetails, "WaitingFolderDetails");
        waitingDetails.Click += (_, _) =>
        {
            var waitingFolder = model.Folders.FirstOrDefault(folder => folder.State != "Paused" && folder.CompleteSnapshot && folder.ConnectedPeers == 0);
            if (waitingFolder is not null) NavigateToFolder(waitingFolder.Id);
        };
        recovery.Children.Add(waitingDetails);
        var message = new StackPanel(); message.Children.Add(attentionTitle);
        attentionText.Margin = new Thickness(0, 6, 0, 0); message.Children.Add(attentionText);
        notice.Children.Add(message); top.Children.Add(attention);
        top.Children.Add(DashboardStyle.Text("Folders", 20, true));

        var links = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(links, Dock.Bottom); overview.Children.Add(links);
        DashboardStyle.Link(approvalsLink); approvalsLink.Click += (_, _) => navigation.SelectedIndex = 1;
        DashboardStyle.Identify(approvalsLink, "ReviewDevices"); links.Children.Add(approvalsLink);
        DashboardStyle.Link(overwritesLink); overwritesLink.Margin = new Thickness(22, 0, 0, 0);
        overwritesLink.Click += (_, _) =>
        {
            var first = model.Overwrites.FirstOrDefault();
            if (first is null) return;
            NavigateToFolder(first.FolderId, 1); versionFilter.SelectedIndex = 1;
        };
        DashboardStyle.Identify(overwritesLink, "ReviewOverwritten"); links.Children.Add(overwritesLink);

        var listArea = new Grid { Margin = new Thickness(0, 14, 0, 0) }; overview.Children.Add(listArea);
        folders.ItemsSource = model.Folders; folders.BorderBrush = DashboardStyle.Line; folders.BorderThickness = new Thickness(1);
        folders.VerticalAlignment = VerticalAlignment.Top;
        folders.SetResourceReference(ItemsControl.ItemTemplateProperty, "FolderRow");
        if (!SystemParameters.HighContrast) folders.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "FolderListItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(folders, ScrollBarVisibility.Disabled);
        DashboardStyle.Identify(folders, "FoldersList"); listArea.Children.Add(folders);
        folders.SelectionChanged += (_, _) =>
        {
            if (folders.SelectedItem is not FolderItem folder) return;
            model.SelectedFolderId = folder.Id; peerView.Refresh(); overwriteView.Refresh(); UpdateFolderHeading();
        };
        folders.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(FolderRowClicked));
        folders.ContextMenu = CreateMenu();
        folders.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(folders, source) is ListBoxItem item) folders.SelectedItem = item.DataContext;
        };
        folders.ContextMenuOpening += (_, args) =>
        {
            if (folders.SelectedItem is FolderItem selected) folders.ContextMenu = FolderMenu(selected.Id);
            else args.Handled = true;
        };
        folders.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter && folders.SelectedItem is FolderItem selected)
            { NavigateToFolder(selected.Id); args.Handled = true; }
        };
        emptyFolders.Margin = new Thickness(24); emptyFolders.Foreground = DashboardStyle.Muted;
        emptyFolders.IsHitTestVisible = false; listArea.Children.Add(emptyFolders);
        BuildFolderDetails();
        return page;
    }

    private void FolderRowClicked(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button button || button.DataContext is not FolderItem folder) return;
        folders.SelectedItem = folder;
        switch (button.Tag as string)
        {
            case "details": NavigateToFolder(folder.Id); break;
            case "pause": _ = ExecuteButtonAsync(button, TogglePauseAsync); break;
            case "more": button.ContextMenu = FolderMenu(folder.Id); OpenMenu(button); break;
        }
        args.Handled = true;
    }

    private ContextMenu FolderMenu(string folderId)
    {
        var menu = CreateMenu();
        AddMenu(menu, "Open folder", () => { ShowUnsupported(); return Task.CompletedTask; });
        AddMenu(menu, "Share folder…", () => { SelectFolder(folderId); return ShareFolderAsync(); });
        AddMenu(menu, "Devices & activity", () => { NavigateToFolder(folderId); return Task.CompletedTask; });
        AddMenu(menu, "Previous versions", () => { NavigateToFolder(folderId, 1); return Task.CompletedTask; });
        AddMenu(menu, "Folder settings", () => { NavigateToFolder(folderId, 2); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenu(menu, "Stop syncing…", () => { SelectFolder(folderId); return RemoveFolderAsync(); });
        return menu;
    }

    private void SelectFolder(string folderId)
    {
        folders.SelectedItem = model.Folders.FirstOrDefault(folder => folder.Id == folderId);
        if (folders.SelectedItem is null) throw new InvalidOperationException("This folder is no longer available.");
    }

    internal void NavigateToFolder(string folderId, int tab = 0, bool refresh = true)
    {
        SelectFolder(folderId); navigation.SelectedIndex = 0;
        overview.Visibility = Visibility.Collapsed; folderDetails.Visibility = Visibility.Visible;
        navigatingToFolder = true;
        try { folderTabs.SelectedIndex = tab; }
        finally { navigatingToFolder = false; }
        UpdateFolderHeading();
        if (refresh && model.ServiceAvailable)
        {
            _ = ExecuteAsync(LoadFolderSettingsAsync);
            _ = ExecuteAsync(LoadArchiveAsync);
        }
    }

    private void BuildFolderDetails()
    {
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); folderDetails.Children.Add(header);
        var back = UiButton("‹ All folders", "AllFolders", () => { folderDetails.Visibility = Visibility.Collapsed; overview.Visibility = Visibility.Visible; });
        back.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; DashboardStyle.Link(back); header.Children.Add(back);
        var title = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right); title.Children.Add(actions);
        actions.Children.Add(UiButton("Open folder", "OpenFolder", ShowUnsupported));
        var share = ActionButton("Share folder…", ShareFolderAsync); DashboardStyle.Identify(share, "ShareFolder"); actions.Children.Add(share);
        folderPause.Click += async (_, _) => await ExecuteButtonAsync(folderPause, TogglePauseAsync);
        DashboardStyle.Identify(folderPause, "SelectedFolderPause"); actions.Children.Add(folderPause);
        var more = UiButton("…", "SelectedFolderActions", () => { });
        System.Windows.Automation.AutomationProperties.SetName(more, "Folder actions");
        more.Click += (_, _) => { more.ContextMenu = FolderMenu(model.SelectedFolderId); OpenMenu(more); };
        actions.Children.Add(more);
        title.Children.Add(folderTitle); header.Children.Add(title);
        folderPath.Foreground = DashboardStyle.Muted; folderPath.Margin = new Thickness(0, 0, 0, 18); header.Children.Add(folderPath);
        DashboardStyle.Identify(folderTabs, "FolderNavigation"); folderDetails.Children.Add(folderTabs);
        folderTabs.Items.Add(new TabItem { Header = "Devices & activity", Content = BuildDevicesPage() });
        folderTabs.Items.Add(new TabItem { Header = "Versions", Content = BuildVersionsPage() });
        folderTabs.Items.Add(new TabItem { Header = "Folder settings", Content = BuildFolderSettingsPage() });
        folderTabs.SelectionChanged += (_, args) =>
        {
            if (!navigatingToFolder && ReferenceEquals(args.Source, folderTabs) && folderTabs.SelectedIndex == 1 && model.ServiceAvailable)
                _ = ExecuteAsync(LoadArchiveAsync);
        };
    }

    private DockPanel BuildDevicesPage()
    {
        var panel = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(DashboardStyle.Text("Devices sharing this folder", 22, true));
        header.Children.Add(BoundText(nameof(model.FolderActivity), "FolderActivity", 14));
        var clock = UiButton("Clock details", "ClockDetails", ShowUnsupported);
        DashboardStyle.Link(clock); clock.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        DockPanel.SetDock(clock, Dock.Bottom); panel.Children.Add(clock);
        var grid = Table("PeersList", peerView);
        Column(grid, "Device", nameof(PeerItem.Name)); Column(grid, "Fingerprint", nameof(PeerItem.Fingerprint), 185);
        Column(grid, "Approval", nameof(PeerItem.Approval), 120); Column(grid, "Connection", nameof(PeerItem.Route), 150);
        panel.Children.Add(grid); return panel;
    }

    private DockPanel BuildVersionsPage()
    {
        var panel = new DockPanel();
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var bar = new DockPanel(); var refresh = ActionButton("Refresh", LoadArchiveAsync);
        DashboardStyle.Identify(refresh, "RefreshVersions"); DockPanel.SetDock(refresh, Dock.Right); bar.Children.Add(refresh);
        bar.Children.Add(DashboardStyle.Text("Previous versions", 26, true)); heading.Children.Add(bar);
        var intro = DashboardStyle.Text("Recover a saved version, or restore it as a separate copy."); intro.Foreground = DashboardStyle.Muted;
        intro.Margin = new Thickness(0, 8, 0, 18); heading.Children.Add(intro);
        versionFilter.Items.Add("All archived versions"); versionFilter.Items.Add("Overwritten local versions");
        versionFilter.SelectedIndex = 0; versionFilter.Width = 280; versionFilter.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        versionFilter.Margin = new Thickness(0, 0, 0, 16); DashboardStyle.Identify(versionFilter, "VersionFilter", "Show versions");
        heading.Children.Add(versionFilter);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var archiveStatus = BoundText(nameof(model.ArchiveStatus), "ArchiveStatus", 13); bottom.Children.Add(archiveStatus);
        var actionBar = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) }; bottom.Children.Add(actionBar);
        var copy = ActionButton("Restore as copy…", () => versionFilter.SelectedIndex == 1 ? RestoreSelectedAsync(true) : RestoreArchiveSelectedAsync(true));
        var replace = ActionButton("Replace current file…", () => versionFilter.SelectedIndex == 1 ? RestoreSelectedAsync(false) : RestoreArchiveSelectedAsync(false));
        DashboardStyle.Identify(copy, "RestoreAsCopy"); DashboardStyle.Identify(replace, "RestoreVersion"); DashboardStyle.Primary(copy);
        actionBar.Children.Add(copy); actionBar.Children.Add(replace);
        bottom.Children.Add(DashboardStyle.Text("Restore as copy lets you choose a new name inside this folder. Replace current file restores over the current file.", 13));
        ConfigureTable(archives, "ArchivedVersions", model.ArchiveVersions);
        Column(archives, "File", nameof(ArchiveItem.Path)); Column(archives, "Saved", nameof(ArchiveItem.ArchivedLocal), 185);
        Column(archives, "Size", nameof(ArchiveItem.SizeDisplay), 100); Column(archives, "Type", nameof(ArchiveItem.Kind), 120);
        ConfigureTable(overwrites, "OverwrittenVersions", overwriteView);
        Column(overwrites, "File", nameof(OverwriteItem.Path)); Column(overwrites, "Saved", nameof(OverwriteItem.ArchivedLocal), 185);
        overwrites.Visibility = Visibility.Collapsed;
        versionLists.Children.Add(archives); versionLists.Children.Add(overwrites); panel.Children.Add(versionLists);
        void UpdateActions()
        {
            bool overwritten = versionFilter.SelectedIndex == 1;
            archives.Visibility = overwritten ? Visibility.Collapsed : Visibility.Visible;
            overwrites.Visibility = overwritten ? Visibility.Visible : Visibility.Collapsed;
            archiveStatus.Visibility = overwritten ? Visibility.Collapsed : Visibility.Visible;
            copy.IsEnabled = replace.IsEnabled = overwritten ? overwrites.SelectedItem is not null : archives.SelectedItem is not null;
        }
        versionFilter.SelectionChanged += (_, _) => UpdateActions();
        archives.SelectionChanged += (_, _) => UpdateActions(); overwrites.SelectionChanged += (_, _) => UpdateActions();
        UpdateActions(); return panel;
    }

    private ScrollViewer BuildFolderSettingsPage()
    {
        var panel = new StackPanel { MaxWidth = 820, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        panel.Children.Add(DashboardStyle.Text("Folder settings", 26, true));
        panel.Children.Add(requireApproval); panel.Children.Add(keepBoth);
        DashboardStyle.Identify(requireApproval, "RequireApproval"); DashboardStyle.Identify(keepBoth, "KeepBothCopies");
        DashboardStyle.Field(panel, "Keep archived versions for (days)", archiveDays, "ArchiveDays"); archiveDays.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        DashboardStyle.Field(panel, "Ignore rules — one pattern per line", ignoreRules, "IgnoreRules");
        var advanced = new StackPanel(); DashboardStyle.Field(advanced, "Predefined hosts — one host:port per line", predefinedHosts, "PredefinedHosts");
        panel.Children.Add(new Expander { Header = "Advanced connections", Content = advanced, Margin = new Thickness(0, 18, 0, 18) });
        var save = ActionButton("Save folder settings", SaveFolderSettingsAsync); DashboardStyle.Primary(save);
        DashboardStyle.Identify(save, "SaveFolderSettings"); save.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; panel.Children.Add(save);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private DockPanel BuildPendingPage()
    {
        var panel = new DockPanel { Margin = new Thickness(32, 24, 32, 24) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 20) }; DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(approvalsTitle);
        top.Children.Add(DashboardStyle.Text("Select a request and verify the device fingerprint before approving it."));
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        foreach (string decision in new[] { "Approve", "Deny", "Block" })
        {
            var button = ActionButton(decision, () => DecidePendingAsync(decision.ToLowerInvariant()));
            DashboardStyle.Identify(button, decision + "Device"); if (decision == "Approve") DashboardStyle.Primary(button);
            button.IsEnabled = false; pending.SelectionChanged += (_, _) => button.IsEnabled = pending.SelectedItem is not null;
            actions.Children.Add(button);
        }
        ConfigureTable(pending, "PendingDevices", model.PendingDevices);
        Column(pending, "Device", nameof(PendingDeviceItem.Name)); Column(pending, "Folder", nameof(PendingDeviceItem.FolderPath));
        Column(pending, "Fingerprint", nameof(PendingDeviceItem.Fingerprint), 185); Column(pending, "Requested", nameof(PendingDeviceItem.RequestedLocal), 180);
        panel.Children.Add(pending); return panel;
    }

    private void UpdateOverview()
    {
        int waiting = model.Folders.Count(folder => folder.State != "Paused" && folder.CompleteSnapshot && folder.ConnectedPeers == 0);
        overviewSummary.Text = waiting == 0 ? "Manage the folders shared by this PC." : $"{waiting} {(waiting == 1 ? "folder is" : "folders are")} waiting for another device.";
        emptyFolders.Visibility = model.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        attention.Visibility = !model.ServiceAvailable || waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        attentionTitle.Text = !model.ServiceAvailable ? model.Banner : "Waiting for another device";
        attentionText.Text = !model.ServiceAvailable ? "Folder status is unavailable until the service reconnects."
            : "Open PeerSync on the other PC and check that it uses the same folder key.";
        waitingDetails.Visibility = model.ServiceAvailable && waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        authorize.Visibility = model.NeedsAuthorization ? Visibility.Visible : Visibility.Collapsed;
        startService.Visibility = !model.ServiceAvailable && !model.NeedsAuthorization
            && model.Banner.StartsWith("Service not running", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        approvalsLink.Content = $"{model.PendingDevices.Count} {(model.PendingDevices.Count == 1 ? "device" : "devices")} awaiting approval";
        if (navigation.Items.Count > 1) ((TabItem)navigation.Items[1]).Header = $"Device approvals ({model.PendingDevices.Count})";
        overwritesLink.Content = $"{model.Overwrites.Count} overwritten versions";
        approvalsTitle.Text = $"Device approvals ({model.PendingDevices.Count})";
        if (model.Folders.Count == 0) { folderDetails.Visibility = Visibility.Collapsed; overview.Visibility = Visibility.Visible; }
        UpdateFolderHeading();
    }

    private void OverviewCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        // Snapshot replacement clears and then repopulates collections. Do not navigate
        // away from folder details during that transient empty state.
        if (overviewRefreshQueued) return;
        overviewRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
        {
            overviewRefreshQueued = false;
            if (!exiting) UpdateOverview();
        }));
    }

    private void UpdateFolderHeading()
    {
        var folder = model.Folders.FirstOrDefault(item => item.Id == model.SelectedFolderId);
        folderTitle.Text = folder?.Name ?? "Folder";
        folderPath.Text = folder is null ? "Select a folder to see its details." : $"{folder.Path}  ·  {folder.AccessDisplay} access";
        folderPause.Content = folder?.PauseLabel ?? "Pause";
    }

    private void ShowTransferDetails()
    {
        var dialog = new Window { Title = "PeerSync — transfer details", Owner = this, Width = 660, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, DataContext = model };
        DashboardStyle.Apply(dialog);
        var panel = new StackPanel { Margin = new Thickness(28) }; dialog.Content = panel;
        panel.Children.Add(DashboardStyle.Text("Transfer details", 24, true)); panel.Children.Add(DashboardStyle.Text("Totals and rates for this PC."));
        panel.Children.Add(BoundText(nameof(model.Activity), "ActivitySummary", 14)); panel.Children.Add(BoundText(nameof(model.TransferRates), "TransferRates", 14));
        var close = UiButton("Close", "CloseTransferDetails", dialog.Close); close.Margin = new Thickness(0, 18, 0, 0); close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; panel.Children.Add(close);
        dialog.ShowDialog();
    }

    private void ShowPreferencesUnavailable()
    {
        var panel = new StackPanel(); panel.Children.Add(DashboardStyle.Text("Settings", 28, true));
        panel.Children.Add(DashboardStyle.Text("Connect to the PeerSync service to edit this PC's preferences."));
        panel.Children.Add(ActionButton("Load settings", LoadPreferencesPageAsync)); preferences.Content = panel;
    }

    private async Task LoadPreferencesPageAsync()
    {
        if (loadingPreferences) return;
        if (!model.ServiceAvailable) { ShowPreferencesUnavailable(); return; }
        loadingPreferences = true;
        try
        {
            await ExecuteAsync(async () =>
            {
                using var client = PeerSync.Control.Contracts.ControlStatusClient.CreateForInstalledService();
                var response = await client.GetSettingsAsync();
                var current = System.Text.Json.JsonSerializer.Deserialize<DeviceSettings>(response.Json, DeviceJson)
                    ?? throw new System.IO.InvalidDataException("Device settings are unavailable.");
                ShowPreferences(current);
            });
        }
        finally { loadingPreferences = false; }
    }

    internal void ShowPreferences(DeviceSettings current)
    {
        bool wasLoading = loadingPreferences;
        loadingPreferences = true;
        try { navigation.SelectedIndex = 2; }
        finally { loadingPreferences = wasLoading; }
        var editor = new PreferencesEditor(current, ShowInformation);
        editor.SaveRequested += async (_, _) =>
        {
            if (!editor.TryGetValue(out var updated)) return;
            editor.IsEnabled = false;
            try
            {
                await ExecuteAsync(async () =>
                {
                    using var client = PeerSync.Control.Contracts.ControlStatusClient.CreateForInstalledService();
                    var saved = await client.UpdateSettingsAsync(System.Text.Json.JsonSerializer.Serialize(updated, DeviceJson));
                    if (saved.RestartRequired) ShowInformation("Saved. Restart the PeerSync service to apply these preferences.");
                });
            }
            finally { editor.IsEnabled = true; }
        };
        editor.CancelRequested += (_, _) => { preferences.Content = null; navigation.SelectedIndex = 0; };
        preferences.Content = editor;
    }

    private static Button UiButton(string label, string id, Action action)
    {
        var button = new Button { Content = label }; DashboardStyle.Identify(button, id);
        button.Click += (_, _) => action(); return button;
    }

    private void AddMenu(ContextMenu menu, string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label, Padding = new Thickness(12, 7, 12, 7), MinHeight = 36 };
        item.Click += async (_, _) => await ExecuteAsync(action); menu.Items.Add(item);
    }

    private ContextMenu CreateMenu() => new()
    { FontFamily = FontFamily, FontSize = FontSize, Background = Background, Foreground = Foreground, Padding = new Thickness(4) };

    private static void OpenMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    private static DataGrid Table(string id, System.Collections.IEnumerable source)
    { var grid = new DataGrid(); ConfigureTable(grid, id, source); return grid; }

    private static void ConfigureTable(DataGrid grid, string id, System.Collections.IEnumerable source)
    {
        grid.IsReadOnly = true; grid.AutoGenerateColumns = false; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
        grid.SelectionMode = DataGridSelectionMode.Single; grid.ItemsSource = source; DashboardStyle.Identify(grid, id);
    }

    private static void Column(DataGrid grid, string label, string property, double width = 0)
    {
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(MarginProperty, new Thickness(12, 8, 12, 8)));
        text.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        text.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
        grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), ElementStyle = text,
            Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
    }
}
