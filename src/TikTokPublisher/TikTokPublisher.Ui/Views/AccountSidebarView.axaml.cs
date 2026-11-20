using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountSidebarView : UserControl
{
    public event EventHandler? NavigatePageRequested;
    public Func<Task<bool>>? CanChangeAccountAsync { get; set; }
    private bool _changingAccount;

    public AccountSidebarView()
    {
        InitializeComponent();
    }

    private void OnAccountSettingsClick(object? sender, RoutedEventArgs e) =>
        NavigatePageRequested?.Invoke(this, EventArgs.Empty);

    private Task<bool> ConfirmAccountChangeAsync() =>
        CanChangeAccountAsync?.Invoke() ?? Task.FromResult(true);

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && await ConfirmAccountChangeAsync())
            vm.AddAccountCommand.Execute(null);
    }

    private async void OnAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingAccount || DataContext is not MainViewModel vm ||
            AccountList.SelectedItem is not AccountItemViewModel account ||
            ReferenceEquals(account, vm.SelectedAccount))
            return;

        _changingAccount = true;
        try
        {
            if (await ConfirmAccountChangeAsync())
                vm.SidebarSelectedAccount = account;
            else
                AccountList.SetCurrentValue(ListBox.SelectedItemProperty, vm.SidebarSelectedAccount);
        }
        finally
        {
            _changingAccount = false;
        }
    }

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
        if (!await ConfirmAccountChangeAsync())
            return;

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
