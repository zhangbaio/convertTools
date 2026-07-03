using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokBrowserView : UserControl
{
    private BrowserSessionHost? _browserHost;
    private MainViewModel? _vm;

    public TikTokBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
        Loaded += OnLoaded;
    }

    public void Initialize(BrowserSessionHost browserHost, MainViewModel vm)
    {
        _browserHost = browserHost;
        _vm = vm;
        DataContext = vm;
        if (BrowserArea is not null && EmptyHint is not null)
            _browserHost.Attach(BrowserArea, EmptyHint);
        vm.NavigateRequested += OnNavigateRequested;
        BindViewModel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) =>
        _browserHost?.ShowAccount(_vm?.SelectedAccount);

    private void BindViewModel()
    {
        if (_vm is null) _vm = DataContext as MainViewModel;
        if (_vm is null || _browserHost is null) return;
        _vm.AccountSwitchRequested -= OnAccountSwitchRequested;
        _vm.AccountSwitchRequested += OnAccountSwitchRequested;
        _browserHost.ShowAccount(_vm.SelectedAccount);
    }

    private void OnAccountSwitchRequested(AccountItemViewModel account) =>
        _browserHost?.ShowAccount(account);

    private void OnNavigateRequested(AccountItemViewModel account, string url)
    {
        if (_browserHost is null) return;
        var host = _browserHost.GetOrCreateHost(account);
        _browserHost.ShowAccount(account);
        host.Navigate(url);
    }

    private void OnOpenLoginClick(object? sender, RoutedEventArgs e)
    {
        var account = _vm?.SelectedAccount;
        if (account is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先在左侧选择账号";
            return;
        }

        var host = _browserHost?.GetOrCreateHost(account);
        _browserHost?.ShowAccount(account);
        host?.Navigate(MainViewModel.TikTokLoginUrl);
        account.Status = AccountStatus.LoggingIn;
        _vm!.StatusMessage = $"[{account.DisplayName}] 已在内置浏览器打开 TikTok 登录页";
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedAccount is null) return;
        var host = _browserHost?.TryGetHost(_vm.SelectedAccount.Id);
        host?.Navigate(MainViewModel.TikTokLoginUrl);
    }
}
