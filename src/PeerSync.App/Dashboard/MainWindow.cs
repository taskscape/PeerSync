using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Grpc.Core;
using Microsoft.Win32;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Binding = System.Windows.Data.Binding;

namespace PeerSync.App.Dashboard;

internal sealed partial class MainWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions DeviceJson = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly DashboardViewModel model;
    private readonly DiagnosticJournal journal;
    private readonly Forms.NotifyIcon tray;
    private readonly ICollectionView peerView;
    private readonly ICollectionView overwriteView;
    private readonly ListBox folders = new();
    private readonly DataGrid pending = new();
    private readonly DataGrid overwrites = new();
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
        Title = "PeerSync"; Width = 1180; Height = 820; MinWidth = 740; MinHeight = 560;
        DataContext = model;
        DashboardStyle.Apply(this);
        BuildShell();
        model.PropertyChanged += ModelChanged;
        model.Notification += Notify;
        model.PendingDevices.CollectionChanged += OverviewCollectionChanged;
        model.Overwrites.CollectionChanged += OverviewCollectionChanged;
        model.Folders.CollectionChanged += OverviewCollectionChanged;

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
        Closed += (_, _) =>
        {
            model.PropertyChanged -= ModelChanged; model.Notification -= Notify;
            model.PendingDevices.CollectionChanged -= OverviewCollectionChanged;
            model.Overwrites.CollectionChanged -= OverviewCollectionChanged;
            model.Folders.CollectionChanged -= OverviewCollectionChanged;
            Dispose(); menu.Dispose();
        };
        if (startInTray) { ShowInTaskbar = false; WindowState = WindowState.Minimized; }
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
        ShowProtectedAccessWarning(grant);
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
        FolderGrantResult grant;
        try { grant = await FolderAccessGrant.GrantAsync(path, new Progress<string>(text => model.Activity = text), CancellationToken.None); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (await ElevatedActions.GrantFolderAsync(path) != 0) throw new IOException("Administrator access could not be granted.");
            grant = new([], false);
        }
        using var client = ControlStatusClient.CreateForInstalledService();
        await client.JoinFolderAsync(key.Value, path);
        ShowProtectedAccessWarning(grant);
    }

    private void ShowProtectedAccessWarning(FolderGrantResult grant)
    {
        if (grant.ProtectedChildren.Count == 0 && !grant.Truncated) return;
        string more = grant.Truncated || grant.ProtectedChildren.Count > 10 ? "\nMore items may be affected." : "";
        MessageBox.Show(this,
            $"Some files or folders have protected ACLs and may need manual access:\n{string.Join("\n", grant.ProtectedChildren.Take(10))}{more}",
            "Folder access", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private string RequireSelectedFolder()
        => folders.SelectedItem is FolderItem item ? item.Id : throw new InvalidOperationException("Select a folder first.");

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            journal.TryWrite(new("app.action_failed", "dashboard")
                { Level = DiagnosticLevel.Warning, ErrorCode = error is RpcException rpc
                    ? "RpcException." + rpc.StatusCode : error.GetType().Name, Exception = error, HResult = error.HResult });
            MessageBox.Show(this, error is RpcException rpcError ? rpcError.Status.Detail : error.Message,
                "PeerSync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await ExecuteButtonAsync(button, action);
        return button;
    }

    private async Task ExecuteButtonAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try { await ExecuteAsync(action); }
        finally { button.IsEnabled = true; }
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
            UpdateFolderHeading();
        }
        if (args.PropertyName is nameof(DashboardViewModel.Activity) or nameof(DashboardViewModel.Banner)
            or nameof(DashboardViewModel.ServiceAvailable)) UpdateOverview();
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
