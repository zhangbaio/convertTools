using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class AccountConfigSyncDirectionDialog : Window
{
    private TikTokAccountSyncDirection? _direction;

    private AccountConfigSyncDirectionDialog()
    {
        Title = "同步账号配置";
        Width = 460;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = "同步默认关闭，只有这次确认后才会和当前登录的 TT 用户交换账号配置。切换登录账号不会自动同步。",
            TextWrapping = TextWrapping.Wrap,
        });
        var upload = new Button { Content = "上传到管理系统", MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
        var download = new Button { Content = "从管理系统同步到本机", MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancel = new Button { Content = "取消", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        upload.Click += (_, _) =>
        {
            _direction = TikTokAccountSyncDirection.PushToCloud;
            Close();
        };
        download.Click += (_, _) =>
        {
            _direction = TikTokAccountSyncDirection.PullFromCloud;
            Close();
        };
        cancel.Click += (_, _) => Close();
        root.Children.Add(upload);
        root.Children.Add(download);
        root.Children.Add(cancel);
        Content = root;
    }

    public static async Task<TikTokAccountSyncDirection?> ShowAsync(Window? owner)
    {
        var dialog = new AccountConfigSyncDirectionDialog();
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
        return dialog._direction;
    }
}
