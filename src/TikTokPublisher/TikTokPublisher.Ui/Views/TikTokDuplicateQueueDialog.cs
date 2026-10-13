using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokDuplicateQueueDialog : Window
{
    public TikTokDuplicateQueueDialog(string summary)
    {
        Title = "已存在的剧集";
        Width = 560;
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
        var skip = new Button
        {
            Content = "跳过已存在",
            MinWidth = 108,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var add = new Button
        {
            Content = "仍然加入",
            MinWidth = 108,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        skip.Click += (_, _) => Close(true);
        add.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(skip);
        buttons.Children.Add(add);
        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = text });
        Content = root;
    }
}
