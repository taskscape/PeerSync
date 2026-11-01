using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PeerSync.Core.Settings;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Label = System.Windows.Controls.Label;

namespace PeerSync.App.Dashboard;

/// <summary>Presentation of the existing device preferences; values and validation remain unchanged.</summary>
internal sealed class PreferencesEditor : UserControl
{
    private readonly DeviceSettings current;
    private readonly Action<string> showMessage;
    private readonly TextBox deviceName = new(), listenPort = new(), rendezvous = new() { AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox uploadLimit = new(), downloadLimit = new();
    private readonly CheckBox lan = new() { Content = "Discover devices on the local network" };
    private readonly CheckBox mapping = new() { Content = "Try automatic port mapping" };
    private readonly CheckBox relay = new() { Content = "Allow relay fallback" };
    private readonly CheckBox azureDiagnostics = new() { Content = "Send diagnostic events to Azure" };
    private readonly CheckBox fullDiagnostics = new() { Content = "Include full diagnostics and remote IP addresses" };
    internal event EventHandler? SaveRequested;
    internal event EventHandler? CancelRequested;

    internal PreferencesEditor(DeviceSettings current, Action<string> showMessage)
    {
        this.current = current; this.showMessage = showMessage;
        var root = new DockPanel(); Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var notice = DashboardStyle.Text("Some changes require a service restart. You'll be told after saving.", 13);
        notice.Foreground = DashboardStyle.Muted; footer.Children.Add(notice);
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty); actions.Children.Add(cancel);
        var save = new Button { Content = "Save settings", IsDefault = true };
        DashboardStyle.Primary(save); DashboardStyle.Identify(save, "SavePreferences");
        save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty); actions.Children.Add(save); footer.Children.Add(actions);

        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(DashboardStyle.Text("Settings", 28, true));
        var intro = DashboardStyle.Text("Preferences for this PC. Folder options live in Folder settings.");
        intro.Foreground = DashboardStyle.Muted; intro.Margin = new Thickness(0, 8, 0, 26); panel.Children.Add(intro);
        var columns = new WrapPanel(); panel.Children.Add(columns);
        var left = new StackPanel(); var right = new StackPanel(); columns.Children.Add(left); columns.Children.Add(right);
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 850;
            double width = Math.Max(280, wide ? (ActualWidth - 64) / 2 : ActualWidth - 24);
            left.Width = right.Width = width;
            left.Margin = new Thickness(0, 0, wide ? 40 : 0, 24);
        };
        left.Children.Add(DashboardStyle.Text("This PC", 21, true));
        Field(left, "Device name", deviceName, current.DeviceName, "DeviceName");
        left.Children.Add(DashboardStyle.Divider());
        left.Children.Add(DashboardStyle.Text("Transfer limits", 21, true));
        left.Children.Add(DashboardStyle.Text("0 means unlimited.", 13));
        var limits = new Grid(); limits.ColumnDefinitions.Add(new ColumnDefinition()); limits.ColumnDefinitions.Add(new ColumnDefinition());
        var upload = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; var download = new StackPanel(); Grid.SetColumn(download, 1);
        limits.Children.Add(upload); limits.Children.Add(download); left.Children.Add(limits);
        Limit(upload, "Upload limit", uploadLimit, current.UploadLimitBytesPerSec, "UploadLimit");
        Limit(download, "Download limit", downloadLimit, current.DownloadLimitBytesPerSec, "DownloadLimit");
        left.Children.Add(DashboardStyle.Divider());
        left.Children.Add(DashboardStyle.Text("Diagnostics", 21, true));
        azureDiagnostics.IsChecked = current.SendDiagnosticsToAzure; fullDiagnostics.IsChecked = current.FullDiagnostics;
        DashboardStyle.Identify(azureDiagnostics, "AzureDiagnostics"); DashboardStyle.Identify(fullDiagnostics, "FullDiagnostics");
        left.Children.Add(azureDiagnostics); left.Children.Add(fullDiagnostics);
        var privacy = DashboardStyle.Text("Azure receives diagnostic event names, counts, error codes, device/folder fingerprints, and address classes. Full diagnostics also sends remote IP addresses and ports. File contents, paths, and exception text stay local. Azure can see the upload's source IP. Restart the service after changing these options.", 13);
        privacy.Foreground = DashboardStyle.Muted; privacy.Margin = new Thickness(0, 10, 0, 0); left.Children.Add(privacy);
        right.Children.Add(DashboardStyle.Text("Connections", 21, true));
        Field(right, "Listening port", listenPort, current.ListenPort.ToString(CultureInfo.InvariantCulture), "ListenPort");
        lan.IsChecked = current.LanDiscovery; mapping.IsChecked = current.UsePortMapping; relay.IsChecked = current.UseRelay;
        DashboardStyle.Identify(lan, "LanDiscovery"); DashboardStyle.Identify(mapping, "PortMapping"); DashboardStyle.Identify(relay, "RelayFallback");
        right.Children.Add(lan); right.Children.Add(mapping); right.Children.Add(relay);
        Field(right, "Discovery servers (rendezvous)", rendezvous, string.Join(Environment.NewLine, current.RendezvousServers), "RendezvousServers");
        rendezvous.VerticalContentAlignment = VerticalAlignment.Top;
        right.Children.Add(DashboardStyle.Text("One server URL per line.", 13));
    }

    private static void Field(StackPanel panel, string label, TextBox box, string value, string id)
    { box.Text = value; DashboardStyle.Field(panel, label, box, id); }

    private void Limit(StackPanel panel, string label, TextBox box, int value, string id)
    {
        var row = new DockPanel();
        var unit = new ComboBox { Width = 108, Margin = new Thickness(10, 0, 0, 0), ItemsSource = new[] { "bytes/s", "KiB/s", "MiB/s" }, SelectedIndex = 0 };
        DashboardStyle.Identify(unit, id + "Unit", label + " unit"); DockPanel.SetDock(unit, Dock.Right); row.Children.Add(unit);
        unit.SelectionChanged += (_, _) =>
        {
            if (unit.SelectedIndex == 0) return;
            unit.SelectedIndex = 0;
            showMessage("Not supported yet");
        };
        box.Text = value.ToString(CultureInfo.InvariantCulture); DashboardStyle.Identify(box, id, label); row.Children.Add(box);
        var caption = new Label { Content = label, Target = box, Padding = new Thickness(0), Margin = new Thickness(0, 16, 0, 6) };
        panel.Children.Add(caption); panel.Children.Add(row);
    }

    internal bool TryGetValue(out DeviceSettings updated)
    {
        updated = current;
        if (!int.TryParse(listenPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || !int.TryParse(uploadLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int upload)
            || !int.TryParse(downloadLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int download))
        { showMessage("Port and transfer limits must be whole numbers."); return false; }
        var candidate = current with
        {
            DeviceName = deviceName.Text.Trim(), ListenPort = port,
            RendezvousServers = rendezvous.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UploadLimitBytesPerSec = upload, DownloadLimitBytesPerSec = download,
            LanDiscovery = lan.IsChecked == true, UsePortMapping = mapping.IsChecked == true,
            UseRelay = relay.IsChecked == true, SendDiagnosticsToAzure = azureDiagnostics.IsChecked == true,
            FullDiagnostics = fullDiagnostics.IsChecked == true,
        };
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(candidate, new ValidationContext(candidate), results, true)
            || candidate.DeviceName.Length == 0 || candidate.RendezvousServers.Length > 16)
        { showMessage("Please check the preference values."); return false; }
        updated = candidate; return true;
    }
}
