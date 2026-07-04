using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class InfoDialog : Window
{
    private InfoDialog(string title, string message)
    {
        Title = title;
        Width = 380;
        Height = 150;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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

    public static async Task ShowAsync(Window? owner, string message, string title = "提示")
    {
        var dialog = new InfoDialog(title, message);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
    }

    public static Task ShowSaveSuccessAsync(Window? owner, string message = "配置已保存成功。") =>
        ShowAsync(owner, message, "保存成功");
}
