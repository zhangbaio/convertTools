using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class ConfirmDialog : Window
{
    private bool? _accepted;

    private ConfirmDialog(string title, string message, double width, double height, string confirmText, string cancelText,
        string? discardText = null)
    {
        Title = title;
        Width = width;
        Height = height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var ok = new Button { Content = confirmText, MinWidth = 88 };
        var cancel = new Button { Content = cancelText, MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        if (discardText is not null)
        {
            var discard = new Button { Content = discardText, MinWidth = 88 };
            discard.Click += (_, _) =>
            {
                _accepted = false;
                Close();
            };
            buttons.Children.Add(discard);
        }
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    public static async Task<bool> ShowAsync(
        Window? owner,
        string title,
        string message,
        double width = 420,
        double height = 170,
        string confirmText = "确定",
        string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, width, height, confirmText, cancelText);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
        return dialog._accepted == true;
    }

    public static async Task<bool?> ShowSaveChangesAsync(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog(title, message, 440, 170, "保存", "取消", "放弃修改");
        await dialog.ShowDialog(owner);
        return dialog._accepted;
    }
}
