using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Desktop;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    private const double ExpandedSidebarWidth = 198;
    private const double CollapsedSidebarWidth = 44;

    private readonly AccountStore _accountStore = new();
    private readonly BrowserSessionHost _browserHost = new();
    private readonly MainViewModel _viewModel;
    private string _activeNavTag = "queue";
    private bool _isSidebarCollapsed;
    private DispatcherTimer? _licenseVerifyTimer;
    private bool _licenseVerifyRunning;
    private bool _licenseLoginDialogOpen;

    public MainWindow()
    {
        var context = new AccountContextService(_accountStore);
        _viewModel = new MainViewModel(_accountStore, context);
        InitializeComponent();
        DataContext = _viewModel;

        _browserHost.Attach(BrowserHostMount);
        _browserHost.PresentationLayoutChanged += () =>
        {
            if (_activeNavTag == "browser")
                ScheduleBrowserHostMountLayout();
        };
        QueueView.Initialize(_viewModel, _browserHost, EnsureBrowserHostMounted);
        BrowserView.Initialize(_browserHost, _viewModel);
        BrowserView.LayoutUpdated += (_, _) =>
        {
            if (_activeNavTag == "browser")
                SyncBrowserHostMountLayout();
        };
        AccountSidebar.DataContext = _viewModel;
        AccountsView.Bind(_viewModel);
        LogView.Bind(_viewModel, _viewModel.Logs);
        SettingsView.Bind(_viewModel.SystemSettings);
        ServicesView.Bind(_viewModel.SystemServices);
        ArchivedView.Bind(_viewModel.ArchivedProjects);
        DownloadView.Bind(_viewModel.DramaDownload, _viewModel.AppendLog);

        QueueView.OpenBrowserRequested += (_, _) => NavigateTo("browser");
        QueueView.PublishBrowserFocusRequested += account =>
        {
            if (_viewModel.SelectedAccount?.Id != account.Id)
                _viewModel.SelectedAccount = account;
            NavigateTo("browser");
        };
        QueueView.OpenLogsRequested += (_, _) => NavigateTo("logs");
        AccountsView.LoginRequested += (_, _) => BeginEmbeddedAccountLoginAsync(forceRelogin: false);
        AccountsView.ReloginRequested += (_, _) => BeginEmbeddedAccountLoginAsync(forceRelogin: true);
        AccountsView.LogoutRequested += (_, _) => _ = LogoutSelectedTikTokAccountAsync();
        _viewModel.EmbeddedLoginRequested += OnEmbeddedLoginRequested;
        _browserHost.AuthSaved += args => _viewModel.HandleEmbeddedAuthSaved(args.Account, args.Result);
        _browserHost.AuthSaveFailed += _viewModel.HandleEmbeddedAuthSaveFailed;
        _browserHost.AuthStatusChanged += _viewModel.HandleEmbeddedAuthStatusChanged;
        LogView.ReturnRequested += (_, _) => NavigateTo("queue");
        LogView.StopRequested += (_, _) => _viewModel.RequestStopQueue();
        _viewModel.NavigatePageRequested += NavigateTo;
        _viewModel.RemoteQueueRunRequested += QueueView.StartQueueRunFromRemoteAsync;
        _viewModel.RemoteAllQueueRunRequested += QueueView.StartAllQueueRunFromRemoteAsync;
        _viewModel.AccountProfileNetworkChanged += profile => _browserHost.InvalidateHostIfNetworkChanged(profile);
        AccountSidebar.NavigatePageRequested += (_, _) => NavigateTo("accounts");
        _viewModel.AccountSwitchRequested += OnAccountSwitchRequested;
        _viewModel.DailyLimitReached += message =>
            _ = ConfirmDialog.ShowAsync(
                this,
                "单日创建剧集已达上限",
                $"{message}\n\n任务队列已自动停止，请明天再继续上传。");

        SetSidebarCollapsed(false);
        NavigateTo("queue");
        Closed += OnWindowClosed;
    }

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            NavigateTo(tag);
    }

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(!_isSidebarCollapsed);

    private void SetSidebarCollapsed(bool collapsed)
    {
        _isSidebarCollapsed = collapsed;

        var width = collapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        TopLayout.ColumnDefinitions[0].Width = new GridLength(width);
        MainLayout.ColumnDefinitions[0].Width = new GridLength(width);

        AccountSidebar.IsVisible = !collapsed;
        CollapsedSidebarRail.IsVisible = collapsed;
        BrandLabel.IsVisible = !collapsed;
        BrandPane.Padding = collapsed ? new Avalonia.Thickness(7, 0) : new Avalonia.Thickness(12, 0, 10, 0);

        SidebarToggleButton.Content = collapsed ? "›" : "‹";
        ToolTip.SetTip(SidebarToggleButton, collapsed ? "展开左侧面板" : "收起左侧面板");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = null;
    }

    public void StartLicenseVerifyTimer()
    {
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(LicenseAuthService.VerifyIntervalHours),
        };
        _licenseVerifyTimer.Tick += OnLicenseVerifyTimerTick;
        _licenseVerifyTimer.Start();
    }

    private async void OnLicenseVerifyTimerTick(object? sender, EventArgs e)
    {
        if (_licenseVerifyRunning)
            return;

        _licenseVerifyRunning = true;
        try
        {
            var state = await LicenseGate.VerifyAsync(forceVerify: true, allowOfflineGrace: true);
            if (state is not null)
            {
                SaveVerifiedLicenseState(state);
                _viewModel.StatusMessage = "软件授权联网校验通过";
                return;
            }

            await RequireLicenseReloginAsync("软件授权联网校验失败，请重新登录后继续使用。");
        }
        finally
        {
            _licenseVerifyRunning = false;
        }
    }

    private async Task RequireLicenseReloginAsync(string message)
    {
        _licenseVerifyTimer?.Stop();
        _viewModel.StatusMessage = message;
        _viewModel.RequestStopQueue();
        HideForLicenseGate();

        var loggedIn = await ShowLicenseLoginDialogAsync(message, standalone: true);
        if (loggedIn)
        {
            _viewModel.StatusMessage = "软件授权登录成功";
            ShowAfterLicenseGate();
            StartLicenseVerifyTimer();
            return;
        }

        Close();
    }

    private async Task<bool> ShowLicenseLoginDialogAsync(string message, bool standalone = false)
    {
        if (_licenseLoginDialogOpen)
            return false;

        _licenseLoginDialogOpen = true;
        try
        {
            var login = LicenseGate.GetLoginDefaults();
            var result = standalone
                ? await LicenseLoginDialog.ShowStandaloneAsync(
                    login.ServerUrl,
                    login.Account,
                    login.Password,
                    message)
                : await LicenseLoginDialog.ShowAsync(
                    this,
                    login.ServerUrl,
                    login.Account,
                    login.Password,
                    message);
            if (result is null)
                return false;

            SaveLicenseLoginResult(result);
            return true;
        }
        finally
        {
            _licenseLoginDialogOpen = false;
        }
    }

    private void SaveLicenseLoginResult(LicenseLoginDialogResult result)
    {
        LicenseGate.SaveLoginResult(result);
        _viewModel.SystemServices.Load();
        _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
    }

    private void SaveVerifiedLicenseState(LicenseState state)
    {
        LicenseGate.SaveVerifiedState(state);
        _viewModel.SystemServices.Load();
        _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
    }

    private void HideForLicenseGate()
    {
        CollapseBrowserHostMount();
        _browserHost.SetPresentationVisible(false);
        Hide();
    }

    private void ShowAfterLicenseGate()
    {
        Show();
        Activate();
        ShowPage(_activeNavTag);
    }

    private void BeginEmbeddedAccountLoginAsync(bool forceRelogin)
    {
        if (_viewModel.SelectedAccount is null)
        {
            _viewModel.StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        _viewModel.BeginAccountLogin(forceRelogin);
    }

    private async void OnEmbeddedLoginRequested(AccountItemViewModel account, bool forceRelogin)
    {
        if (forceRelogin)
        {
            var resetWarning = await _browserHost.ResetAccountAsync(account);
            if (!string.IsNullOrWhiteSpace(resetWarning))
                _viewModel.StatusMessage += $"；{resetWarning}";
        }

        NavigateTo("browser");
        _browserHost.BeginLogin(account, forceRelogin);
    }

    private async Task LogoutSelectedTikTokAccountAsync()
    {
        var account = _viewModel.SelectedAccount;
        if (account is null)
        {
            _viewModel.StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            "确认退出登录",
            $"确认退出「{account.DisplayName}」的 TikTok 登录？\n\n这会删除本机授权文件并清空该账号的内置浏览器会话，账号配置和工作目录会保留。");
        if (!confirmed)
            return;

        var warning = await _browserHost.ResetAccountAsync(account);
        account.Model.TiktokLastLoginEmail = "";
        account.Model.TiktokLastLoginAt = "";
        account.Status = AccountStatus.Offline;
        _viewModel.SaveAccountProfile(account.Model);
        account.RefreshFromModel();
        AccountsView.RefreshSelectedAccount();

        _viewModel.HandleEmbeddedAuthStatusChanged("已退出登录");
        _viewModel.StatusMessage = $"[{account.DisplayName}] 已退出 TikTok 登录";
        if (!string.IsNullOrWhiteSpace(warning))
            _viewModel.StatusMessage += $"；{warning}";
        _viewModel.AppendLog(_viewModel.StatusMessage);
    }

    private void OnAccountSwitchRequested(AccountItemViewModel account)
    {
        if (_activeNavTag == "browser")
            ScheduleBrowserHostMountLayout();
    }

    private void EnsureBrowserHostMounted()
    {
        // 浏览器页正在展示时不得折叠挂载层/关闭渲染（否则页面变成不绘制的黑色原生窗口）。
        if (_activeNavTag == "browser")
            return;

        // 后台上传：WebView2 挂在 1×1 隐藏层，避免原生 HWND 盖住队列页。
        CollapseBrowserHostMount();
        _browserHost.SetPresentationVisible(false);
    }

    private void CollapseBrowserHostMount()
    {
        BrowserHostMount.Margin = new Thickness(0);
        BrowserHostMount.Width = 1;
        BrowserHostMount.Height = 1;
        BrowserHostMount.HorizontalAlignment = HorizontalAlignment.Left;
        BrowserHostMount.VerticalAlignment = VerticalAlignment.Top;
        BrowserHostMount.ZIndex = 5;
    }

    private void SyncBrowserHostMountLayout()
    {
        if (_activeNavTag != "browser" || !BrowserView.IsVisible)
        {
            CollapseBrowserHostMount();
            return;
        }

        var bounds = BrowserView.GetBrowserAreaBoundsIn(ContentHostPanel);
        if (bounds is null or { Width: <= 1 } or { Height: <= 1 })
            return;

        var rect = bounds.Value;
        // 仅在几何真正变化时更新，避免 LayoutUpdated → 改布局 → LayoutUpdated 死循环。
        var current = BrowserHostMount.Margin;
        var unchanged =
            Math.Abs(current.Left - rect.X) < 0.5 &&
            Math.Abs(current.Top - rect.Y) < 0.5 &&
            Math.Abs(BrowserHostMount.Width - rect.Width) < 0.5 &&
            Math.Abs(BrowserHostMount.Height - rect.Height) < 0.5;
        if (unchanged)
        {
            _browserHost.RefreshPresentationBounds();
            return;
        }

        BrowserHostMount.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        BrowserHostMount.Width = rect.Width;
        BrowserHostMount.Height = rect.Height;
        BrowserHostMount.HorizontalAlignment = HorizontalAlignment.Left;
        BrowserHostMount.VerticalAlignment = VerticalAlignment.Top;
        BrowserHostMount.ZIndex = 11;
        _browserHost.RefreshPresentationBounds();
    }

    private bool _mountLayoutSyncPending;

    private void ScheduleBrowserHostMountLayout()
    {
        // 合并多次调度；回调内不得调用 ShowAccount（会再触发 PresentationLayoutChanged 造成死循环）。
        if (_mountLayoutSyncPending)
            return;

        _mountLayoutSyncPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _mountLayoutSyncPending = false;
            SyncBrowserHostMountLayout();
        }, DispatcherPriority.Background);
    }

    private void NavigateTo(string tag)
    {
        _activeNavTag = tag;
        UpdateNavHighlight();
        ShowPage(tag);
    }

    private void UpdateNavHighlight()
    {
        foreach (var child in ModuleNav.Children)
        {
            if (child is not Button btn) continue;
            var tag = btn.Tag?.ToString() ?? "";
            var isActive = string.Equals(tag, _activeNavTag, StringComparison.OrdinalIgnoreCase);
            btn.Classes.Set("topNavItemActive", isActive);
        }
    }

    private void ShowPage(string tag)
    {
        var browserActive = tag == "browser";
        const int activeZ = 10;
        const int inactiveZ = 0;

        DownloadView.IsVisible = tag == "download";
        QueueView.IsVisible = tag == "queue";
        AccountsView.IsVisible = tag == "accounts";
        LogView.IsVisible = tag == "logs";
        SettingsView.IsVisible = tag == "settings";
        ServicesView.IsVisible = tag == "services";
        ArchivedView.IsVisible = tag == "archived";

        DownloadView.ZIndex = tag == "download" ? activeZ : inactiveZ;
        QueueView.ZIndex = tag == "queue" ? activeZ : inactiveZ;
        AccountsView.ZIndex = tag == "accounts" ? activeZ : inactiveZ;
        LogView.ZIndex = tag == "logs" ? activeZ : inactiveZ;
        SettingsView.ZIndex = tag == "settings" ? activeZ : inactiveZ;
        ServicesView.ZIndex = tag == "services" ? activeZ : inactiveZ;
        ArchivedView.ZIndex = tag == "archived" ? activeZ : inactiveZ;

        if (browserActive)
        {
            BrowserView.IsVisible = true;
            BrowserView.IsHitTestVisible = true;
            BrowserView.ZIndex = activeZ;
            _browserHost.ShowAccount(_viewModel.SelectedAccount);
            SyncBrowserHostMountLayout();
            _browserHost.SetPresentationVisible(true);
            ScheduleBrowserHostMountLayout();
        }
        else
        {
            BrowserView.IsVisible = false;
            BrowserView.ZIndex = inactiveZ;
            CollapseBrowserHostMount();
            _browserHost.SetPresentationVisible(false);
        }

        if (tag == "logs")
            _viewModel.RefreshLogSnapshot();
        if (tag == "archived")
            _viewModel.ArchivedProjects.RefreshCommand.Execute(null);
        if (tag == "services")
        {
            _viewModel.SystemServices.Load();
            _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        }
    }
}
