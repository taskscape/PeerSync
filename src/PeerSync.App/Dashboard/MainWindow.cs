using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Grpc.Core;
using Microsoft.Win32;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using QRCoder;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using DataGrid = System.Windows.Controls.DataGrid;
using Binding = System.Windows.Data.Binding;

namespace PeerSync.App.Dashboard;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions DeviceJson = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly DashboardViewModel model;
    private readonly DiagnosticJournal journal;
    private readonly Forms.NotifyIcon tray;
    private readonly ICollectionView peerView;
    private readonly ICollectionView overwriteView;
    private readonly ListBox folders = new();
    private readonly ListBox pending = new();
    private readonly ListBox overwrites = new();
    private readonly DataGrid archives = new();
    private readonly TextBox ignoreRules = new() { AcceptsReturn = true, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox predefinedHosts = new() { AcceptsReturn = true, Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox archiveDays = new() { Width = 90 };
    private readonly CheckBox requireApproval = new() { Content = "Require approval for new devices" };
    private readonly CheckBox keepBoth = new() { Content = "Keep both copies after conflicts" };
    private readonly Button authorize = new() { Content = "Authorize this account", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button startService = new() { Content = "Start service", Margin = new Thickness(0, 8, 0, 0) };
    private bool exiting;

    internal MainWindow(DashboardViewModel model, DiagnosticJournal journal, bool startInTray)
    {
        this.model = model; this.journal = journal;
        peerView = CollectionViewSource.GetDefaultView(model.Peers);
        peerView.Filter = item => item is PeerItem peer && peer.FolderId == model.SelectedFolderId;
        overwriteView = CollectionViewSource.GetDefaultView(model.Overwrites);
        overwriteView.Filter = item => item is OverwriteItem overwrite && overwrite.FolderId == model.SelectedFolderId;
        Title = "PeerSync"; Width = 980; Height = 700; MinWidth = 740; MinHeight = 500;
        DataContext = model;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "PeerSync", FontSize = 27, FontWeight = FontWeights.SemiBold });
        header.Children.Add(BoundText(nameof(model.Banner), "ServiceBanner", 15));
        header.Children.Add(BoundText(nameof(model.Activity), "ActivitySummary", 13));
        header.Children.Add(BoundText(nameof(model.TransferRates), "TransferRates", 12));
        authorize.Click += async (_, _) => await ExecuteAsync(() => ElevatedActions.AuthorizeCurrentUserAsync());
        authorize.Visibility = Visibility.Collapsed; header.Children.Add(authorize);
        startService.Click += async (_, _) => await ExecuteAsync(() => ElevatedActions.StartServiceAsync());
        startService.Visibility = Visibility.Collapsed; header.Children.Add(startService);

        var navigation = new TabControl(); root.Children.Add(navigation);
        navigation.Items.Add(new TabItem { Header = "Folders", Content = BuildFoldersPage() });
        navigation.Items.Add(new TabItem { Header = "Pending devices", Content = BuildPendingPage() });
        navigation.Items.Add(new TabItem { Header = "Preferences", Content = BuildPreferencesPage() });
        model.PropertyChanged += ModelChanged;
        model.Notification += Notify;

        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Warning,
            Text = "PeerSync — connecting", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PeerSync", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add("Pause for 30 minutes", null, (_, _) => Dispatcher.InvokeAsync(() => _ = ExecuteAsync(() => PauseAllAsync(1800))));
        menu.Items.Add("Pause for 1 hour", null, (_, _) => Dispatcher.InvokeAsync(() => _ = ExecuteAsync(() => PauseAllAsync(3600))));
        menu.Items.Add("Pause until resumed", null, (_, _) => Dispatcher.InvokeAsync(() => _ = ExecuteAsync(() => PauseAllAsync(0))));
        menu.Items.Add("Resume all", null, (_, _) => Dispatcher.InvokeAsync(() => _ = ExecuteAsync(ResumeAllAsync)));
        menu.Items.Add("Exit app", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        Closing += OnClosing;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; model.Notification -= Notify; Dispose(); menu.Dispose(); };
        if (startInTray) { ShowInTaskbar = false; WindowState = WindowState.Minimized; }
    }

    private Grid BuildFoldersPage()
    {
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0, 8, 14, 0) };
        Grid.SetColumn(left, 0); layout.Children.Add(left);
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(controls, Dock.Top); left.Children.Add(controls);
        controls.Children.Add(ActionButton("Add", AddFolderAsync));
        controls.Children.Add(ActionButton("Join", JoinFolderAsync));
        controls.Children.Add(ActionButton("Share", ShareFolderAsync));
        controls.Children.Add(ActionButton("Pause / Resume", TogglePauseAsync));
        controls.Children.Add(ActionButton("Remove", RemoveFolderAsync));
        folders.ItemsSource = model.Folders; folders.SelectionChanged += (_, _) =>
        {
            if (folders.SelectedItem is FolderItem item)
            {
                model.SelectedFolderId = item.Id;
                peerView.Refresh(); overwriteView.Refresh();
                _ = ExecuteAsync(LoadFolderSettingsAsync);
                _ = ExecuteAsync(LoadArchiveAsync);
            }
        };
        AutomationProperties.SetAutomationId(folders, "FoldersList");
        left.Children.Add(folders);

        var right = new TabControl { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(right, 1); layout.Children.Add(right);
        right.Items.Add(new TabItem { Header = "Peers", Content = PeerList() });
        right.Items.Add(new TabItem { Header = "Activity", Content = BoundText(nameof(model.FolderActivity), "FolderActivity", 14) });
        right.Items.Add(new TabItem { Header = "Overwritten", Content = BuildOverwritePage() });
        var archiveTab = new TabItem { Header = "Archive", Content = BuildArchivePage() };
        right.Items.Add(archiveTab);
        right.Items.Add(new TabItem { Header = "Settings", Content = BuildFolderSettingsPage() });
        right.SelectionChanged += (_, change) =>
        {
            if (ReferenceEquals(change.Source, right) && ReferenceEquals(right.SelectedItem, archiveTab))
                _ = ExecuteAsync(LoadArchiveAsync);
        };
        return layout;
    }

    private DataGrid PeerList()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = peerView,
            CanUserAddRows = false, CanUserDeleteRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(PeerItem.Name)), Width = new(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Device fingerprint", Binding = new Binding(nameof(PeerItem.Fingerprint)), Width = 165 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Approval", Binding = new Binding(nameof(PeerItem.Approval)), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Route", Binding = new Binding(nameof(PeerItem.Route)), Width = 110 });
        return grid;
    }

    private DockPanel BuildOverwritePage()
    {
        var panel = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
        bar.Children.Add(ActionButton("Restore my version", () => RestoreSelectedAsync(copy: false)));
        bar.Children.Add(ActionButton("Restore as copy", () => RestoreSelectedAsync(copy: true)));
        overwrites.ItemsSource = overwriteView; AutomationProperties.SetAutomationId(overwrites, "OverwrittenVersions");
        panel.Children.Add(overwrites); return panel;
    }

    private DockPanel BuildArchivePage()
    {
        var panel = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
        bar.Children.Add(ActionButton("Refresh", LoadArchiveAsync));
        bar.Children.Add(ActionButton("Restore", () => RestoreArchiveSelectedAsync(copy: false)));
        bar.Children.Add(ActionButton("Restore as copy", () => RestoreArchiveSelectedAsync(copy: true)));
        var status = BoundText(nameof(model.ArchiveStatus), "ArchiveStatus", 12);
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        archives.IsReadOnly = true; archives.AutoGenerateColumns = false;
        archives.CanUserAddRows = false; archives.CanUserDeleteRows = false;
        archives.ItemsSource = model.ArchiveVersions;
        archives.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new Binding(nameof(ArchiveItem.Path)),
            Width = new(1, DataGridLengthUnitType.Star) });
        archives.Columns.Add(new DataGridTextColumn { Header = "Archived", Binding = new Binding(nameof(ArchiveItem.ArchivedLocal)), Width = 145 });
        archives.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding(nameof(ArchiveItem.SizeDisplay)), Width = 90 });
        AutomationProperties.SetAutomationId(archives, "ArchivedVersions");
        panel.Children.Add(archives);
        return panel;
    }

    private ScrollViewer BuildFolderSettingsPage()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(requireApproval); panel.Children.Add(keepBoth);
        panel.Children.Add(new TextBlock { Text = "Keep archived versions for (days)", Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(archiveDays);
        panel.Children.Add(new TextBlock { Text = "Predefined hosts (one host:port per line)", Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(predefinedHosts);
        panel.Children.Add(new TextBlock { Text = "Ignore rules (one pattern per line)", Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(ignoreRules);
        panel.Children.Add(ActionButton("Save folder settings", SaveFolderSettingsAsync));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private DockPanel BuildPendingPage()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        actions.Children.Add(ActionButton("Approve", () => DecidePendingAsync("approve")));
        actions.Children.Add(ActionButton("Deny", () => DecidePendingAsync("deny")));
        actions.Children.Add(ActionButton("Block", () => DecidePendingAsync("block")));
        pending.ItemsSource = model.PendingDevices; AutomationProperties.SetAutomationId(pending, "PendingDevices");
        panel.Children.Add(pending); return panel;
    }

    private StackPanel BuildPreferencesPage()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Device preferences", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Change the device name, network settings and transfer limits." });
        panel.Children.Add(ActionButton("Edit preferences", EditPreferencesAsync));
        return panel;
    }

    private async Task AddFolderAsync()
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder to sync" };
        if (picker.ShowDialog(this) != true) return;
        string path = picker.FolderName;
        var progress = new Progress<string>(value => model.Activity = value);
        FolderGrantResult grant;
        try { grant = await FolderAccessGrant.GrantAsync(path, progress, CancellationToken.None); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (await ElevatedActions.GrantFolderAsync(path) != 0)
                throw new IOException("Administrator access could not be granted to the service.");
            grant = new([], false);
        }
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.AddFolderAsync(path);
        if (grant.ProtectedChildren.Count > 0 || grant.Truncated)
            MessageBox.Show(this, $"Some children have protected ACLs and may need manual access: {string.Join("\n", grant.ProtectedChildren.Take(10))}",
                "Folder access", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task JoinFolderAsync()
    {
        var key = new TextPromptWindow("Join folder", "Paste the PeerSync key", validate: value =>
        {
            try { using var parsed = FolderKeys.FromText(value); return parsed.CanRead; }
            catch (Exception error) when (error is FormatException or ArgumentException) { return false; }
        }) { Owner = this };
        if (key.ShowDialog() != true) return;
        var picker = new OpenFolderDialog { Title = "Choose where to sync the joined folder" };
        if (picker.ShowDialog(this) != true) return;
        string path = picker.FolderName;
        try { await FolderAccessGrant.GrantAsync(path, new Progress<string>(text => model.Activity = text), CancellationToken.None); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (await ElevatedActions.GrantFolderAsync(path) != 0) throw new IOException("Administrator access could not be granted.");
        }
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.JoinFolderAsync(key.Value, path);
    }

    private async Task ShareFolderAsync()
    {
        string folder = RequireSelectedFolder();
        using var client = ControlStatusClient.CreateForInstalledService();
        var share = await client.GetFolderKeysAsync(folder);
        using var keys = FolderKeys.FromText(share.Key);
        new ShareWindow(keys) { Owner = this }.ShowDialog();
    }

    private async Task TogglePauseAsync()
    {
        if (folders.SelectedItem is not FolderItem folder) return;
        using var client = ControlStatusClient.CreateForInstalledService();
        if (folder.State == "Paused") await client.ResumeFolderAsync(folder.Id);
        else await client.PauseFolderAsync(folder.Id);
    }

    private async Task RemoveFolderAsync()
    {
        if (folders.SelectedItem is not FolderItem folder) return;
        if (MessageBox.Show(this, "Stop syncing this folder on this PC? Its files will be kept.",
            "Remove folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.RemoveFolderAsync(folder.Id);
    }

    private static async Task PauseAllAsync(long seconds)
    {
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.PauseAllAsync(seconds);
    }

    private static async Task ResumeAllAsync()
    {
        using var client = ControlStatusClient.CreateForInstalledService();
        var folders = await client.ListFoldersAsync();
        foreach (var folder in folders.Folders.Where(folder => folder.Paused))
            await client.ResumeFolderAsync(folder.FolderId);
    }

    private async Task RestoreSelectedAsync(bool copy)
    {
        if (overwrites.SelectedItem is not OverwriteItem item) return;
        string? destination = null;
        if (copy)
        {
            var prompt = new TextPromptWindow("Restore as copy", "Relative destination inside this folder") { Owner = this };
            if (prompt.ShowDialog() != true) return;
            destination = prompt.Value;
        }
        using var client = ControlStatusClient.CreateForInstalledService();
        var result = await client.RestoreArchiveAsync(item.FolderId, item.ArchiveId,
            overwrittenOnly: true, copyPath: destination);
        if (!result.Completed) throw new IOException("The restore could not complete: " + result.Detail);
    }

    private async Task LoadArchiveAsync()
    {
        string folder = model.SelectedFolderId;
        if (folder.Length == 0 || !model.ServiceAvailable) return;
        using var client = ControlStatusClient.CreateForInstalledService();
        var response = await client.ListArchiveAsync(folder);
        model.ApplyArchive(response);
    }

    private async Task RestoreArchiveSelectedAsync(bool copy)
    {
        if (archives.SelectedItem is not ArchiveItem item) return;
        string? destination = null;
        if (copy)
        {
            var prompt = new TextPromptWindow("Restore as copy", "Relative destination inside this folder") { Owner = this };
            if (prompt.ShowDialog() != true) return;
            destination = prompt.Value;
        }
        using var client = ControlStatusClient.CreateForInstalledService();
        var result = await client.RestoreArchiveAsync(item.FolderId, item.ArchiveId,
            overwrittenOnly: false, copyPath: destination);
        if (!result.Completed) throw new IOException("The restore could not complete: " + result.Detail);
        model.Activity = copy ? "Archived version restored as a copy." : "Archived version restored.";
        await LoadArchiveAsync();
    }

    private async Task DecidePendingAsync(string decision)
    {
        if (pending.SelectedItem is not PendingDeviceItem item) return;
        if (MessageBox.Show(this, $"{decision.ToUpperInvariant()} device {item.DeviceId}?\nFolder: {item.FolderId}",
            "Device fingerprint", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.DecideDeviceAsync(item.FolderId, item.DeviceId, decision);
    }

    private async Task LoadFolderSettingsAsync()
    {
        if (model.SelectedFolderId.Length == 0) return;
        using var client = ControlStatusClient.CreateForInstalledService();
        var settings = FolderSettings.FromJson((await client.GetFolderSettingsAsync(model.SelectedFolderId)).Json);
        requireApproval.IsChecked = settings.RequireApproval; keepBoth.IsChecked = settings.KeepBothCopies;
        archiveDays.Text = settings.ArchiveDays.ToString(CultureInfo.InvariantCulture);
        predefinedHosts.Text = string.Join(Environment.NewLine, settings.PredefinedHosts);
        ignoreRules.Text = settings.IgnoreRules;
    }

    private async Task SaveFolderSettingsAsync()
    {
        string folder = RequireSelectedFolder();
        if (!int.TryParse(archiveDays.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days))
            throw new ArgumentException("Archive days must be a whole number.");
        using var client = ControlStatusClient.CreateForInstalledService();
        var current = FolderSettings.FromJson((await client.GetFolderSettingsAsync(folder)).Json);
        var updated = current with { RequireApproval = requireApproval.IsChecked == true,
            KeepBothCopies = keepBoth.IsChecked == true, ArchiveDays = days,
            PredefinedHosts = predefinedHosts.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IgnoreRules = ignoreRules.Text };
        var saved = await client.UpdateFolderSettingsAsync(folder, updated.ToJson());
        if (saved.RestartRequired)
            MessageBox.Show(this, "Saved. Restart the PeerSync service to apply the changed folder policy.",
                "Folder settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task EditPreferencesAsync()
    {
        using var client = ControlStatusClient.CreateForInstalledService();
        var response = await client.GetSettingsAsync();
        var current = JsonSerializer.Deserialize<DeviceSettings>(response.Json, DeviceJson)
            ?? throw new InvalidDataException("Device settings are unavailable.");
        var dialog = new PreferencesWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var saved = await client.UpdateSettingsAsync(JsonSerializer.Serialize(dialog.Value, DeviceJson));
        if (saved.RestartRequired)
            MessageBox.Show(this, "Saved. Restart the PeerSync service to apply these preferences.",
                "Preferences", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string RequireSelectedFolder()
        => folders.SelectedItem is FolderItem item ? item.Id : throw new InvalidOperationException("Select a folder first.");

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            journal.TryWrite(new("app.action_failed", "dashboard")
                { Level = DiagnosticLevel.Warning, ErrorCode = error is RpcException rpc
                    ? "RpcException." + rpc.StatusCode : error.GetType().Name, HResult = error.HResult });
            MessageBox.Show(this, error is RpcException rpcError ? rpcError.Status.Detail : error.Message,
                "PeerSync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 7), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await ExecuteAsync(action); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static TextBlock BoundText(string property, string automationId, int size)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 1) };
        AutomationProperties.SetAutomationId(text, automationId);
        text.SetBinding(TextBlock.TextProperty, new Binding(property)); return text;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DashboardViewModel.SelectedFolderId))
        {
            if (folders.SelectedItem is not FolderItem current || current.Id != model.SelectedFolderId)
                folders.SelectedItem = model.Folders.FirstOrDefault(item => item.Id == model.SelectedFolderId);
            peerView.Refresh(); overwriteView.Refresh();
        }
        if (args.PropertyName is nameof(DashboardViewModel.NeedsAuthorization))
            authorize.Visibility = model.NeedsAuthorization ? Visibility.Visible : Visibility.Collapsed;
        if (args.PropertyName is nameof(DashboardViewModel.ServiceAvailable) or nameof(DashboardViewModel.Banner))
        {
            startService.Visibility = !model.ServiceAvailable && !model.NeedsAuthorization
                && model.Banner.StartsWith("Service not running", StringComparison.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;
            tray.Icon = model.ServiceAvailable
                ? model.Folders.Count != 0 && model.Folders.All(item => item.State == "Paused")
                    ? System.Drawing.SystemIcons.Information : System.Drawing.SystemIcons.Application
                : model.NeedsAuthorization ? System.Drawing.SystemIcons.Error : System.Drawing.SystemIcons.Warning;
            tray.Text = model.ServiceAvailable ? "PeerSync — connected" : "PeerSync — service unavailable";
        }
    }

    private void Notify(string message)
    {
        tray.BalloonTipTitle = "PeerSync"; tray.BalloonTipText = message;
        tray.ShowBalloonTip(5000);
    }

    internal void ShowAndActivate()
    {
        ShowInTaskbar = true; Show(); WindowState = WindowState.Normal; Activate();
    }

    internal void Exit()
    {
        exiting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (exiting) return;
        args.Cancel = true; Hide(); ShowInTaskbar = false;
    }

    public void Dispose() => tray.Dispose();
}
