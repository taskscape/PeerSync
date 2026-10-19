using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PeerSync.App.Status;

internal sealed class ClockStatusWindow : Window
{
    internal ClockStatusWindow(ClockStatusViewModel model)
    {
        Title = "PeerSync — peer clock status"; Width = 1100; Height = 480; MinWidth = 760; MinHeight = 300;
        Background = Brushes.White; Foreground = Brushes.Black; DataContext = model;
        var panel = new DockPanel { Margin = new Thickness(24) }; Content = panel;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Peer clock status", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        header.Children.Add(BoundText(nameof(model.Connection), "ServiceConnection", 17));
        header.Children.Add(BoundText(nameof(model.Availability), "SyncAvailability", 14));
        header.Children.Add(BoundText(nameof(model.Detail), "StatusDetail", 14));
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserReorderColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 14, MinRowHeight = 40 };
        AutomationProperties.SetAutomationId(grid, "PeerClockRows"); grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Rows)));
        Column("Folder", nameof(ClockStatusRow.Folder), 130); Column("Peer", nameof(ClockStatusRow.Peer), 130);
        Column("Peer state", nameof(ClockStatusRow.Connection), 115); Column("Offset (minutes)", nameof(ClockStatusRow.OffsetMinutes), 130);
        grid.Columns.Add(new DataGridTextColumn { Header = "Clock status", Binding = new Binding(nameof(ClockStatusRow.Reason)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        panel.Children.Add(grid);
        void Column(string label, string property, int width) => grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = width });
    }
    private static TextBlock BoundText(string property, string id, int size)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
        AutomationProperties.SetAutomationId(text, id); text.SetBinding(TextBlock.TextProperty, new Binding(property)); return text;
    }
}
