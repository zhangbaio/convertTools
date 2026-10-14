using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class ConfigScopeDialog : Window
{
    private ConfigScopeDialog(string title, string message, string configText, string dataText)
    {
        Title = title;
        Width = 560;
        Height = 320;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        var config = new Button
        {
            Content = configText,
            MinWidth = 132,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var data = new Button
        {
            Content = dataText,
            MinWidth = 148,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 88,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        config.Click += (_, _) => Close(false);
        data.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(config);
        buttons.Children.Add(data);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = text });
        Content = root;
    }

    public static async Task<bool?> ShowAsync(
        Window? owner,
        string title,
        string message,
        string configText,
        string dataText)
    {
        var dialog = new ConfigScopeDialog(title, message, configText, dataText);
        if (owner is not null)
            return await dialog.ShowDialog<bool?>(owner);
        return await dialog.ShowDialog<bool?>(new Window());
    }
}
