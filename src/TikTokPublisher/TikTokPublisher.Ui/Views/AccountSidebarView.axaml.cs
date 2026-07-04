using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountSidebarView : UserControl
{
    public event EventHandler? NavigatePageRequested;

    public AccountSidebarView()
    {
        InitializeComponent();
    }

    private void OnAccountSettingsClick(object? sender, RoutedEventArgs e) =>
        NavigatePageRequested?.Invoke(this, EventArgs.Empty);

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedAccount is null)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = await TextPromptDialog.ShowAsync(
            owner,
            "重命名账号",
            "请输入新的账号名称",
            vm.SelectedAccount.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        vm.RenameAccount(name.Trim());
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedAccount is null)
            return;

        if (vm.Accounts.Count <= 1)
        {
            vm.StatusMessage = "至少需要保留一个 TikTok 账号";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "删除账号",
            $"确认删除账号「{vm.SelectedAccount.DisplayName}」？");
        if (!confirmed)
            return;

        vm.RemoveSelectedAccount();
    }
}
