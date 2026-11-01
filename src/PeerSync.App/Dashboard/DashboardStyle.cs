using SystemColors = System.Windows.SystemColors;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using WpfControl = System.Windows.Controls.Control;
using Label = System.Windows.Controls.Label;
using MessageBox = System.Windows.MessageBox;
using FontFamily = System.Windows.Media.FontFamily;

namespace PeerSync.App.Dashboard;

internal static class DashboardStyle
{
    internal static Brush Accent => SystemParameters.HighContrast ? SystemColors.HotTrackBrush : new SolidColorBrush(Color.FromRgb(8, 127, 120));
    internal static Brush Muted => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(89, 101, 121));
    internal static Brush Line => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(220, 226, 232));

    internal static void Apply(Window window)
    {
        window.FontFamily = new FontFamily("Segoe UI");
        window.FontSize = 16;
        window.Background = SystemColors.WindowBrush;
        window.Foreground = SystemColors.WindowTextBrush;
        window.UseLayoutRounding = true;
        if (!SystemParameters.HighContrast)
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/PeerSync;component/Dashboard/DashboardTheme.xaml", UriKind.Relative) });
    }

    internal static TextBlock Text(string text, int size = 16, bool strong = false) => new()
    { Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

    internal static void Identify(DependencyObject control, string id, string? name = null)
    {
        AutomationProperties.SetAutomationId(control, id);
        if (name is not null) AutomationProperties.SetName(control, name);
    }

    internal static void Primary(Button button)
    {
        if (!SystemParameters.HighContrast) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
    }

    internal static void Link(Button button)
    {
        if (!SystemParameters.HighContrast) button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
    }

    internal static Border Divider() => new() { Height = 1, Background = Line, Margin = new Thickness(0, 20, 0, 20) };

    internal static void Field(Panel panel, string label, WpfControl control, string? id = null)
    {
        var caption = new Label { Content = label, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 16, 0, 6) };
        panel.Children.Add(caption);
        if (id is not null) Identify(control, id, label);
        panel.Children.Add(control);
    }

}
