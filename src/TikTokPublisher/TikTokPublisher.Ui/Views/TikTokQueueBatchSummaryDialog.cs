using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

internal sealed class TikTokQueueBatchSummaryDialog : Window
{
    public TikTokQueueBatchSummaryDialog(string title, string summary, string primaryLabel,
        string? secondaryLabel, string? cancelLabel)
    {
        Title = title;
        Width = 600;
        Height = 360;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
        };
        var primary = new Button
        {
            Content = primaryLabel,
            MinWidth = 108,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "primaryAction" },
        };
        primary.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(primary);
        if (secondaryLabel is not null)
        {
            var secondary = new Button { Content = secondaryLabel };
            secondary.Click += (_, _) => Close(false);
            buttons.Children.Add(secondary);
        }
        if (cancelLabel is not null)
        {
            var cancel = new Button { Content = cancelLabel, IsCancel = true };
            cancel.Click += (_, _) => Close(null);
            buttons.Children.Add(cancel);
        }
        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = text });
        Content = root;
    }
}
