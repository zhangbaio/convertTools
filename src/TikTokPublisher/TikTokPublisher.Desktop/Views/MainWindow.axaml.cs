using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly AccountStore _accountStore = new();
    private readonly BrowserSessionHost _browserHost = new();
    private readonly MainViewModel _viewModel;
    private string _activeNavTag = "queue";

    public MainWindow()
    {
        var context = new AccountContextService(_accountStore);
        _viewModel = new MainViewModel(_accountStore, context);
        InitializeComponent();
        DataContext = _viewModel;

        QueueView.Initialize(_viewModel, _browserHost);
        BrowserView.Initialize(_browserHost, _viewModel);
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
            _browserHost.ShowAccount(account);
            NavigateTo("browser");
        };
        QueueView.OpenLogsRequested += (_, _) => NavigateTo("logs");
        AccountsView.LoginRequested += (_, _) => BeginEmbeddedAccountLoginAsync(forceRelogin: false);
        AccountsView.ReloginRequested += (_, _) => BeginEmbeddedAccountLoginAsync(forceRelogin: true);
        _viewModel.EmbeddedLoginRequested += OnEmbeddedLoginRequested;
        _browserHost.AuthSaved += args => _viewModel.HandleEmbeddedAuthSaved(args.Account, args.Result);
        _browserHost.AuthSaveFailed += _viewModel.HandleEmbeddedAuthSaveFailed;
        _browserHost.AuthStatusChanged += _viewModel.HandleEmbeddedAuthStatusChanged;
        LogView.ReturnRequested += (_, _) => NavigateTo("queue");
        LogView.StopRequested += (_, _) => _viewModel.RequestStopQueue();
        _viewModel.NavigatePageRequested += NavigateTo;
        _viewModel.RemoteQueueRunRequested += QueueView.StartQueueRunFromRemoteAsync;
        _viewModel.AccountProfileNetworkChanged += profile => _browserHost.InvalidateHostIfNetworkChanged(profile);
        AccountSidebar.NavigatePageRequested += (_, _) => NavigateTo("accounts");

        NavigateTo("queue");
        Opened += OnWindowOpened;
    }

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            NavigateTo(tag);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var settings = ClientSettingsStore.Load();
        var state = LicenseAuthService.LoadUsableState(settings.AuthServerUrl, verifyIfDue: false);
        if (state is null)
        {
            _viewModel.StatusMessage = "尚未登录授权账号，请前往「系统服务」登录。";
        }
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

        _browserHost.BeginLogin(account, forceRelogin);
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
        DownloadView.IsVisible = tag == "download";
        QueueView.IsVisible = tag == "queue";
        AccountsView.IsVisible = tag == "accounts";
        LogView.IsVisible = tag == "logs";
        SettingsView.IsVisible = tag == "settings";
        ServicesView.IsVisible = tag == "services";
        ArchivedView.IsVisible = tag == "archived";
        BrowserView.IsVisible = tag == "browser";
        if (tag == "logs")
            _viewModel.RefreshLogSnapshot();
        if (tag == "archived")
            _viewModel.ArchivedProjects.RefreshCommand.Execute(null);
        if (tag == "services")
            _viewModel.SystemServices.Load();
    }
}
