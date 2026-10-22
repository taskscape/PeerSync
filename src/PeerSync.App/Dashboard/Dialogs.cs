using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using QRCoder;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;
using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace PeerSync.App.Dashboard;

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox input = new() { MinWidth = 420, TextWrapping = TextWrapping.Wrap };
    internal string Value => input.Text.Trim();

    internal TextPromptWindow(string title, string label, Func<string, bool>? validate = null)
    {
        Title = title; Width = 520; Height = 190; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(input);
        var ok = new Button { Content = "Continue", IsDefault = true, IsEnabled = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 5, 16, 5) };
        panel.Children.Add(ok);
        input.TextChanged += (_, _) => ok.IsEnabled = validate?.Invoke(Value) ?? Value.Length > 0;
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        Loaded += (_, _) => input.Focus();
    }
}

internal sealed class ShareWindow : Window
{
    private readonly TextBox key = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Image qr = new() { Width = 230, Height = 230, Margin = new Thickness(0, 15, 0, 0) };
    private readonly FolderKeys keys;
    private bool readWriteVisible;

    internal ShareWindow(FolderKeys keys)
    {
        this.keys = keys;
        Title = "Share PeerSync folder"; Width = 510; Height = 475;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Share a key only with someone you trust.", TextWrapping = TextWrapping.Wrap });
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(bar);
        if (keys.CanRead)
        {
            var readOnly = new Button { Content = "Read-only key", Margin = new Thickness(0, 0, 8, 0) };
            readOnly.Click += (_, _) => ShowKey(keys.ExportReadOnlyText()); bar.Children.Add(readOnly);
        }
        if (keys.CanWrite)
        {
            var reveal = new Button { Content = "Reveal read-write key", Margin = new Thickness(0, 0, 8, 0) };
            reveal.Click += (_, _) =>
            {
                if (!readWriteVisible && MessageBox.Show(this,
                    "This key lets its holder edit and delete files in the folder. Reveal it?",
                    "Read-write key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                readWriteVisible = true; ShowKey(keys.ExportReadWriteText());
            };
            bar.Children.Add(reveal);
        }
        var encrypted = new Button { Content = "Encrypted-only key", Margin = new Thickness(0, 0, 8, 0) };
        encrypted.Click += (_, _) => ShowKey(keys.ExportEncryptedText()); bar.Children.Add(encrypted);
        var copy = new Button { Content = "Copy visible key" };
        copy.Click += (_, _) => { if (key.Text.Length > 0) Clipboard.SetText(key.Text); };
        bar.Children.Add(copy);
        panel.Children.Add(key); panel.Children.Add(qr);
        ShowKey(keys.CanRead ? keys.ExportReadOnlyText() : keys.ExportEncryptedText());
    }

    private void ShowKey(string value)
    {
        key.Text = value;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        using var stream = new MemoryStream(code.GetGraphic(8));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); qr.Source = bitmap;
    }
}

internal sealed class PreferencesWindow : Window
{
    private readonly DeviceSettings current;
    private readonly TextBox deviceName = new(), listenPort = new(), rendezvous = new();
    private readonly TextBox uploadLimit = new(), downloadLimit = new();
    private readonly CheckBox lan = new() { Content = "Discover peers on the LAN" };
    private readonly CheckBox mapping = new() { Content = "Try automatic port mapping" };
    private readonly CheckBox relay = new() { Content = "Allow relay fallback" };
    internal DeviceSettings Value { get; private set; }

    internal PreferencesWindow(DeviceSettings current)
    {
        this.current = current; Value = current;
        Title = "PeerSync preferences"; Width = 560; Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Field("Device name", deviceName, current.DeviceName);
        Field("Listening port", listenPort, current.ListenPort.ToString(CultureInfo.InvariantCulture));
        Field("Rendezvous server URLs (one per line)", rendezvous, string.Join(Environment.NewLine, current.RendezvousServers));
        Field("Upload limit in bytes/s (0 = unlimited)", uploadLimit, current.UploadLimitBytesPerSec.ToString(CultureInfo.InvariantCulture));
        Field("Download limit in bytes/s (0 = unlimited)", downloadLimit, current.DownloadLimitBytesPerSec.ToString(CultureInfo.InvariantCulture));
        lan.IsChecked = current.LanDiscovery; mapping.IsChecked = current.UsePortMapping;
        relay.IsChecked = current.UseRelay;
        panel.Children.Add(lan); panel.Children.Add(mapping); panel.Children.Add(relay);
        var save = new Button { Content = "Save preferences", IsDefault = true,
            Margin = new Thickness(0, 15, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Padding = new Thickness(14, 6, 14, 6) };
        save.Click += (_, _) => Save(); panel.Children.Add(save);
        void Field(string label, TextBox box, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
            box.Text = value; panel.Children.Add(box);
        }
    }

    private void Save()
    {
        if (!int.TryParse(listenPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || !int.TryParse(uploadLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int upload)
            || !int.TryParse(downloadLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int download))
        { MessageBox.Show(this, "Port and transfer limits must be whole numbers."); return; }
        var updated = current with
        {
            DeviceName = deviceName.Text.Trim(), ListenPort = port,
            RendezvousServers = rendezvous.Text.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UploadLimitBytesPerSec = upload, DownloadLimitBytesPerSec = download,
            LanDiscovery = lan.IsChecked == true, UsePortMapping = mapping.IsChecked == true,
            UseRelay = relay.IsChecked == true,
        };
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(updated, new ValidationContext(updated), results, true)
            || updated.DeviceName.Length == 0 || updated.RendezvousServers.Length > 16)
        { MessageBox.Show(this, "Please check the preference values."); return; }
        Value = updated; DialogResult = true; Close();
    }
}
