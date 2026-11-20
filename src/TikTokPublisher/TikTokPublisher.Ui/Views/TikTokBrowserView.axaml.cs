using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
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
        var today = DateTimeOffset.Now;
        AnalyticsStartDatePicker.SelectedDate = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, today.Offset);
        AnalyticsEndDatePicker.SelectedDate = today;
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

    private void OnAccountSwitchRequested(AccountItemViewModel account)
    {
        // 上传页执行中切账号不碰 WebView2。浏览器页打开时会按当前账号重新挂载。
        if (!IsVisible || _browserHost is null)
            return;

        _browserHost.ShowAccount(account, createIfMissing: true);
    }

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

    private async void OnExportAnalyticsClick(object? sender, RoutedEventArgs e)
    {
        var account = _vm?.SelectedAccount;
        if (account is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先在左侧选择账号";
            return;
        }

        var startValue = AnalyticsStartDatePicker.SelectedDate;
        var endValue = AnalyticsEndDatePicker.SelectedDate;
        if (startValue is null || endValue is null)
        {
            AnalyticsStatusText.Text = "请选择开始和结束日期";
            return;
        }

        var start = DateOnly.FromDateTime(startValue.Value.DateTime);
        var end = DateOnly.FromDateTime(endValue.Value.DateTime);
        if (end < start)
        {
            AnalyticsStatusText.Text = "结束日期不能早于开始日期";
            return;
        }

        ExportAnalyticsButton.IsEnabled = false;
        AnalyticsStatusText.Text = "正在获取...";
        try
        {
            TikTokDailyAnalyticsReport report;
            if (TikTokUploadChannels.IsApi(account.Model.TiktokUploadStrategy))
            {
                AnalyticsStatusText.Text = "正在通过接口获取每日播放...";
                report = await TikTokDailyAnalyticsService.FetchViaApiAsync(
                    account.Model, start, end, CancellationToken.None);
            }
            else
            {
                if (_browserHost is null)
                    throw new InvalidOperationException("浏览器尚未初始化。");

                var host = _browserHost.GetOrCreateHost(account);
                _browserHost.ShowAccount(account);
                report = await TikTokDailyAnalyticsService.FetchAsync(
                    host,
                    start,
                    end,
                    message => Avalonia.Threading.Dispatcher.UIThread.Post(() => AnalyticsStatusText.Text = message),
                    CancellationToken.None);
            }

            var outputPath = TikTokDailyAnalyticsExcelService.Export(account.DisplayName, report);
            AnalyticsStatusText.Text = $"已获取 {report.Rows.Count} 天，最新数据 {report.LatestEventDate:M.d}";
            var owner = TopLevel.GetTopLevel(this) as Window;
            await InfoDialog.ShowAsync(
                owner,
                $"已导出 {report.Rows.Count} 天的播放数据。\n最新数据日期：{report.LatestEventDate:yyyy-MM-dd}\n\n{outputPath}",
                "导出成功",
                width: 560,
                height: 230);
        }
        catch (Exception ex)
        {
            AnalyticsStatusText.Text = "获取失败";
            if (_vm is not null) _vm.StatusMessage = ex.Message;
            var owner = TopLevel.GetTopLevel(this) as Window;
            await InfoDialog.ShowAsync(owner, ex.Message, "获取播放统计失败", width: 520, height: 210);
        }
        finally
        {
            ExportAnalyticsButton.IsEnabled = true;
        }
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
