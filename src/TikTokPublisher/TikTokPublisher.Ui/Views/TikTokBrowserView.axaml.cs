using System;
using System.Diagnostics;
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
    private bool _manualAuthSavePromptPending;

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
        _browserHost.AuthSaved -= OnAuthSaved;
        _browserHost.AuthSaved += OnAuthSaved;
        _browserHost.AuthSaveFailed -= OnAuthSaveFailed;
        _browserHost.AuthSaveFailed += OnAuthSaveFailed;
        if (EmptyHint is not null)
            _browserHost.SetEmptyHint(EmptyHint);
        if (RuntimeMissingHint is not null)
            _browserHost.SetRuntimeMissingHint(RuntimeMissingHint);
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
        // 浏览器页不可见时切账号仅切换已存在会话的可见性；不要为未打开过浏览器的账号
        // 在切换瞬间创建 WebView2（引擎冷启动会明显拖慢切账号）。
        _browserHost?.ShowAccount(account, createIfMissing: IsVisible);

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

        _manualAuthSavePromptPending = true;
        await _browserHost.SaveAuthAsync(account);
    }

    private async void OnAuthSaved(EmbeddedAuthSavedEventArgs args)
    {
        if (!_manualAuthSavePromptPending)
            return;

        _manualAuthSavePromptPending = false;
        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "授权已保存成功。");
    }

    private void OnAuthSaveFailed(string message) => _manualAuthSavePromptPending = false;

    private void OnInstallWebView2Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 打开微软官方 WebView2 运行时下载页，由用户以管理员权限完成安装。
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://developer.microsoft.com/microsoft-edge/webview2/",
                UseShellExecute = true,
            });
            if (_vm is not null)
                _vm.StatusMessage = "已打开 WebView2 运行时下载页，安装完成后请点「我已安装，重试」。";
        }
        catch (Exception ex)
        {
            if (_vm is not null)
                _vm.StatusMessage = $"无法打开下载页面：{ex.Message}，请手动搜索安装 Microsoft Edge WebView2 Runtime。";
        }
    }

    private async void OnRetryWebView2Click(object? sender, RoutedEventArgs e)
    {
        var account = _vm?.SelectedAccount;
        if (account is null || _browserHost is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先在左侧选择账号";
            return;
        }

        if (RuntimeMissingHint is not null)
            RuntimeMissingHint.IsVisible = false;
        if (_vm is not null)
            _vm.StatusMessage = "正在重新初始化内置浏览器...";

        // 重建 host：若运行时已装好则正常加载，否则会再次触发缺失覆盖层。
        await _browserHost.RecreateHostAsync(
            account,
            navigateUrl: EmbeddedBrowserLoginHelper.ResolveHomeUrl(account.Model));
    }
}
