using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class InfoDialog : Window
{
    private InfoDialog(string title, string message, double width = 380, double height = 150, bool showInfoIcon = false)
    {
        Title = title;
        Width = width;
        Height = height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (showInfoIcon)
        {
            Content = CreateIconContent(message);
            return;
        }

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        });

        var ok = new Button
        {
            Content = "确定",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);
        Content = root;
    }

    private Grid CreateIconContent(string message)
    {
        var root = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("52,*"),
        };

        var icon = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromRgb(19, 145, 218)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "i",
                FontSize = 28,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetRow(icon, 0);
        Grid.SetColumn(icon, 0);
        root.Children.Add(icon);

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(body, 0);
        Grid.SetColumn(body, 1);
        root.Children.Add(body);

        var ok = new Button
        {
            Content = "确定",
            MinWidth = 78,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ok.Click += (_, _) => Close();
        Grid.SetRow(ok, 1);
        Grid.SetColumn(ok, 1);
        root.Children.Add(ok);

        return root;
    }

    public static async Task ShowAsync(
        Window? owner,
        string message,
        string title = "提示",
        double width = 380,
        double height = 150,
        bool showInfoIcon = false)
    {
        var dialog = new InfoDialog(title, message, width, height, showInfoIcon);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
    }

    public static Task ShowSaveSuccessAsync(Window? owner, string message = "配置已保存成功。") =>
        ShowAsync(owner, message, "保存成功");

    public static Task ShowLoginProbeSuccessAsync(Window? owner, string message) =>
        ShowAsync(owner, message, "测试登录成功 - 短剧助手", 300, 170, showInfoIcon: true);
}
