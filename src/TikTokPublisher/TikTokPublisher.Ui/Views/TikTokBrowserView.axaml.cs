using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
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
        if (EmptyHint is not null)
            _browserHost.SetEmptyHint(EmptyHint);
        vm.NavigateRequested += OnNavigateRequested;
        BindViewModel();
    }

    public Rect? GetBrowserAreaBoundsIn(Visual relativeTo)
    {
        if (BrowserArea is null)
            return null;

        try
        {
            var topLeft = BrowserArea.TranslatePoint(new Point(0, 0), relativeTo);
            if (topLeft is null)
                return null;

            var bottomRight = BrowserArea.TranslatePoint(
                new Point(BrowserArea.Bounds.Width, BrowserArea.Bounds.Height),
                relativeTo);
            if (bottomRight is null)
                return null;

            return new Rect(topLeft.Value, bottomRight.Value);
        }
        catch
        {
            return null;
        }
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
        if (_vm?.SelectedAccount is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先在左侧选择账号";
            return;
        }

        _vm.BeginAccountLogin(forceRelogin: false);
    }

    private void OnHomeClick(object? sender, RoutedEventArgs e)
    {
        var account = _vm?.SelectedAccount;
        if (account is null || _browserHost is null)
            return;

        var url = EmbeddedBrowserLoginHelper.ResolveHomeUrl(account.Model);
        var host = _browserHost.GetOrCreateHost(account);
        _browserHost.ShowAccount(account);
        host.Navigate(url);
        _vm!.StatusMessage = $"[{account.DisplayName}] 已打开短剧中心主页";
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedAccount is null) return;
        _browserHost?.TryGetHost(_vm.SelectedAccount.Id)?.Reload();
    }

    private async void OnSaveAuthClick(object? sender, RoutedEventArgs e)
    {
        var account = _vm?.SelectedAccount;
        if (account is null || _browserHost is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先在左侧选择账号";
            return;
        }

        await _browserHost.SaveAuthAsync(account);
    }
}
