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
        DashboardStyle.Apply(this);
        Title = title; Width = 580; Height = 230; WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        DashboardStyle.Apply(this);
        Title = "Share PeerSync folder"; Width = 660; Height = 640; MinWidth = 580; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(DashboardStyle.Text("Share folder", 28, true));
        panel.Children.Add(new TextBlock { Text = "Choose a key type. Share a key only with someone you trust.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
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
        DashboardStyle.Identify(key, "ShareKey", "Folder key");
        panel.Children.Add(key); panel.Children.Add(qr);
        panel.Children.Add(DashboardStyle.Text("Read & write keys allow editing and deleting files. Only key types available to this folder are shown.", 13));
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
    internal DeviceSettings Value { get; private set; }
    internal PreferencesWindow(DeviceSettings current)
    {
        Value = current; Title = "PeerSync preferences"; Width = 1080; Height = 800; MinWidth = 620; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DashboardStyle.Apply(this);
        var editor = new PreferencesEditor(current, message => MessageBox.Show(this, message, "PeerSync", MessageBoxButton.OK, MessageBoxImage.Information))
        { Margin = new Thickness(28) };
        Content = editor;
        editor.SaveRequested += (_, _) =>
        {
            if (!editor.TryGetValue(out var value)) return;
            Value = value; DialogResult = true; Close();
        };
        editor.CancelRequested += (_, _) => { DialogResult = false; Close(); };
    }
}
