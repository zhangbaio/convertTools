using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;
using Account = ChannelsPublisher.Core.Models.PublishAccount;
using AccountStore = ChannelsPublisher.Core.Services.AccountStore;
using WorkspaceViewModel = PlatformPublisher.Desktop.ViewModels.MainWindowViewModel;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouUploadWorkspace : UserControl, IDisposable
{
    private const string SelectedAccountKey = "ui.kuaishou_active_account";
    private ServiceProvider? _services;
    private FileStream? _lease;
    private WorkspaceViewModel? _viewModel;
    private AccountStore? _accounts;
    private IJsonSettingStore? _settings;
    private YunfanXingeDispatchService? _dispatch;
    private Account? _selectedAccount;
    private string _page = "queue";
    private bool _updatingSelection;
    private bool _changingAccount;
    private bool _changingPage;
    private bool _active;
    private bool _disposed;
    private Task? _initialization;

    public event Action? SettingsRequested;
    public event Action? RuntimeChanged;
    public bool HasActiveUploads => WorkspaceAccounts.Any(IsRunning);

    private IEnumerable<Account> WorkspaceAccounts =>
        _accounts?.Accounts.Where(account => !account.KuaishouRemovedFromWorkspace) ?? [];

    public KuaishouUploadWorkspace()
    {
        InitializeComponent();
        Workflow.UseEmbeddedUploadLayout();
        Configuration.UseWorkspaceNavigation(Workflow.DetachWorkDirectoryEditor());
        Workflow.KuaishouConfigRequested += async (_, _) => await ShowPageAsync("config");
        Workflow.SettingsRequested += (_, _) => SettingsRequested?.Invoke();
        Workflow.ViewLogsRequested += async (_, _) => await ShowPageAsync("logs");
        LogView.ReturnRequested += async (_, _) => await ShowPageAsync("queue");
        ArchiveView.ReturnRequested += async (_, _) => await ShowPageAsync("queue");
        Workflow.ArchiveRequested += async (_, _) => await ArchiveCheckedAsync();
        Workflow.KuaishouAdxRequested += async (_, request) => await OpenAdxAsync(request);
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void MountAccountPanel(Panel host)
    {
        WorkspaceLayout.Children.Remove(Sidebar);
        WorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(0);
        host.Children.Add(Sidebar);
    }

    public void SetAccountPanelVisible(bool visible) => Sidebar.IsVisible = visible;

    public async Task ActivateAsync()
    {
        _active = true;
        if (await EnsureInitializedAsync() && _active && !_disposed)
        {
            _viewModel?.SetKuaishouQueueStatusVisible(Workflow.IsVisible);
            RefreshSidebar();
            RefreshHistory();
        }
    }

    public void Deactivate()
    {
        _active = false;
        _viewModel?.SetKuaishouQueueStatusVisible(false);
    }

    public Task<bool> TryLeaveAsync(Window owner) =>
        Configuration.IsVisible ? Configuration.TryLeaveAsync(owner) : Task.FromResult(true);

    public bool BindAnalytics(AnalyticsView analytics)
    {
        if (_viewModel is not { } viewModel || _services is null)
            return false;
        if (analytics.DataContext is not AnalyticsViewModel)
        {
            var analyticsModel = _services.GetRequiredService<AnalyticsViewModel>();
            analyticsModel.Bind(
                () => viewModel.ListAnalyticsAccounts()
                    .Where(account => account.Platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
                    .ToArray(),
                account => viewModel.IsKuaishouPublishRunning(account.Id, account.Platform));
            analytics.DataContext = analyticsModel;
        }
        return true;
    }

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_disposed)
            return false;
        if (_viewModel is not null && _initialization?.IsCompleted != false)
            return true;
        var initialization = _initialization ??= InitializeAsync();
        await initialization;
        if (_viewModel is not null)
            return !_disposed;
        if (ReferenceEquals(_initialization, initialization))
            _initialization = null;
        return false;
    }

    private async Task InitializeAsync()
    {
        StartupError.Text = "正在加载快手工作区…";
        Unavailable.IsVisible = true;
        ServiceProvider? services = null;
        FileStream? lease = null;
        WorkspaceViewModel? viewModel = null;
        try
        {
            (services, lease) = await Task.Run(() =>
            {
                var oldClients = Process.GetProcessesByName("YunfanPlatformPublisher");
                try
                {
                    if (oldClients.Any(process => process.Id != Environment.ProcessId))
                        throw new InvalidOperationException("请先退出独立云帆客户端，再打开快手上传，以免后台任务重复运行。");
                }
                finally
                {
                    foreach (var process in oldClients) process.Dispose();
                }

                var ownership = KuaishouWorkspaceLease.Acquire();
                ServiceProvider? provider = null;
                try
                {
                    provider = App.BuildServices(embeddedInTikTok: true);
                    _ = provider.GetRequiredService<AnalyticsRepository>();
                    provider.GetRequiredService<LegacyDatabaseImporter>().Import(
                        PlatformPublisherPaths.LegacySettingsDatabasePath,
                        PlatformPublisherPaths.LegacyAnalyticsDatabasePath);
                    return (provider, ownership);
                }
                catch
                {
                    provider?.Dispose();
                    ownership.Dispose();
                    throw;
                }
            });

            if (_disposed)
                return;
            KuaishouPersonalConfig.ConfigureDatabase(
                services.GetRequiredService<AccountJsonSettingStore>(),
                services.GetRequiredService<KuaishouCredentialStore>());
            var accounts = services.GetRequiredService<AccountStore>();
            var settings = services.GetRequiredService<IJsonSettingStore>();
            accounts.Load();
            viewModel = services.GetRequiredService<WorkspaceViewModel>();
            await viewModel.Initialization;
            if (_disposed)
                return;
            _accounts = accounts;
            _settings = settings;
            _services = services;
            _lease = lease;
            _viewModel = viewModel;
            lease = null;
            services = null;
            viewModel = null;

            DataContext = _viewModel;
            Sidebar.DataContext = _viewModel;
            Workflow.Bind(() => _selectedAccount);
            Configuration.UseAccountStore(accounts);
            Configuration.ConfigSaved += OnConfigSavedAsync;
            Configuration.ConfirmAccountEdition += ConfirmEditionAsync;
            Configuration.AccountEditionApplied += ApplyEditionAsync;
            Configuration.AccountsImported += OnAccountsImported;
            var selectedId = settings.Load(SelectedAccountKey, () => "");
            SelectAccount(WorkspaceAccounts.FirstOrDefault(account => account.Id == selectedId) ??
                WorkspaceAccounts.FirstOrDefault());
            _services.GetRequiredService<KuaishouOnlineQueueWorker>().Start();
            _dispatch = new YunfanXingeDispatchService(_viewModel);
            _viewModel.KuaishouDailyUploadLimitReached += OnDailyLimitReached;
            _dispatch.Start();
            ApplyPage();
            RuntimeChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StartupError.Text = $"快手工作区加载失败：{ex.Message}";
            Trace.TraceError(ex.ToString());
            ReleaseRuntime();
            ApplyPage();
            RuntimeChanged?.Invoke();
        }
        finally
        {
            viewModel?.Shutdown();
            services?.Dispose();
            lease?.Dispose();
        }
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await ActivateAsync();

    private async void SubPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
            await ShowPageAsync(page);
    }

    private async Task ShowPageAsync(string page)
    {
        if (_page == page || _changingPage || _viewModel is null)
            return;
        _changingPage = true;
        try
        {
            if (!IsConfigurationPage(page) && Owner is { } owner && !await TryLeaveAsync(owner))
                return;
            _page = page;
            ApplyPage();
        }
        finally
        {
            _changingPage = false;
        }
    }

    private static bool IsConfigurationPage(string page) => page is "config" or "shelf";

    private void ApplyPage()
    {
        var ready = _viewModel is not null;
        Unavailable.IsVisible = !ready;
        SubNavigation.IsEnabled = ready;
        Sidebar.IsEnabled = ready;
        Workflow.IsVisible = ready && _page == "queue";
        Configuration.IsVisible = ready && IsConfigurationPage(_page);
        LogView.IsVisible = ready && _page == "logs";
        ArchiveView.IsVisible = ready && _page == "archived";
        AllHistoryAccounts.IsVisible = LogView.IsVisible || ArchiveView.IsVisible;
        _viewModel?.SetKuaishouQueueStatusVisible(_active && Workflow.IsVisible);
        foreach (var button in SubNavigation.Children.OfType<Button>())
        {
            var active = Equals(button.Tag, _page);
            button.Classes.Set("topNavItemActive", active);
            if (active && _active && button.IsLoaded)
                button.BringIntoView();
        }
        if (Configuration.IsVisible)
            Configuration.SelectWorkspacePage(_page == "shelf");
        RefreshHistory();
    }

    private void OnHistoryScopeChanged(object? sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        if (_viewModel is not { } viewModel)
            return;
        var all = AllHistoryAccounts.IsChecked == true;
        if (_page == "logs")
            LogView.SetAccountScope(all);
        if (_page == "archived")
        {
            viewModel.UseKuaishouArchiveAccountScope = true;
            viewModel.KuaishouArchiveAllAccounts = all;
            ArchiveView.Refresh();
        }
    }

    private void AccountSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshSidebar();

    private void RefreshSidebar()
    {
        if (_accounts is null || AccountList is null)
            return;
        _updatingSelection = true;
        try
        {
            var keyword = AccountSearch.Text?.Trim() ?? "";
            var rows = WorkspaceAccounts
                .Where(account => account.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    account.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    account.Nickname.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Select(account => new SidebarAccount(account, AccountSummary(account)))
                .ToArray();
            AccountList.ItemsSource = rows;
            AccountList.SelectedItem = rows.FirstOrDefault(row => row.Account.Id == _selectedAccount?.Id);
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private string AccountSummary(Account account)
    {
        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return "未设置账号类型";
        var enterprise = platform == PublishPlatform.KuaishouEnterpriseRevenue;
        var key = enterprise ? "kuaishou.enterprise.config" : "kuaishou.personal.config";
        var path = _services?.GetRequiredService<AccountJsonSettingStore>()
            .TryLoad<KuaishouPersonalConfig>(account.Id, key, out var config) == true
            ? config?.AuthStatePath
            : null;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(PlatformPublisherPaths.DataRoot, "kuaishou-personal", "accounts", account.Id,
                enterprise ? "kuaishou_enterprise_kdj_auth_state.json" : "kuaishou_personal_kdj_auth_state.json");
        return $"{(enterprise ? "企业版" : "个人版")} · {(File.Exists(path) ? "已保存授权" : "待授权")}";
    }

    private async void AccountList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _changingAccount || AccountList.SelectedItem is not SidebarAccount row ||
            row.Account.Id == _selectedAccount?.Id)
            return;
        _changingAccount = true;
        try
        {
            if (Owner is not { } owner || await TryLeaveAsync(owner))
                SelectAccount(row.Account);
            else
                RefreshSidebar();
        }
        finally
        {
            _changingAccount = false;
        }
    }

    private void SelectAccount(Account? account)
    {
        _selectedAccount = account;
        _viewModel!.UseGlobalAccounts(WorkspaceAccounts, account);
        _viewModel.EnterKuaishouPage();
        Configuration.LoadAccount(account, _viewModel.SelectedPlatform.Value);
        _settings!.Save(SelectedAccountKey, account?.Id ?? "");
        RefreshSidebar();
        RefreshHistory();
    }

    private async void AddAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (_accounts is null || _changingAccount || Owner is not { } owner)
            return;
        _changingAccount = true;
        try
        {
            if (!await TryLeaveAsync(owner)) return;
            var account = _accounts.Add($"快手账号{_accounts.Accounts.Count + 1}");
            AccountSearch.Text = "";
            SelectAccount(account);
            await ShowPageAsync("config");
        }
        finally
        {
            _changingAccount = false;
        }
    }

    private async void RemoveAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (_accounts is null || _selectedAccount is not { } account || _changingAccount || Owner is not { } owner)
            return;
        _changingAccount = true;
        try
        {
            if (IsRunning(account))
            {
                _viewModel!.StatusMessage = "请先停止该账号的上传任务。";
                return;
            }
            if (!await TryLeaveAsync(owner) ||
                !await ConfirmDialog.ShowAsync(owner, "移除快手账号", $"从快手上传移除「{account.Name}」？该账号将停止接收快手任务和自动上架，项目文件和其他平台的账号数据将保留。") ||
                IsRunning(account))
                return;
            account.KuaishouRemovedFromWorkspace = true;
            _accounts.Update(account);
            SelectAccount(WorkspaceAccounts.FirstOrDefault());
        }
        finally
        {
            _changingAccount = false;
        }
    }

    private bool IsRunning(Account account) =>
        _viewModel?.IsKuaishouPublishRunning(account.Id, PublishPlatform.KuaishouPersonalRevenue) == true ||
        _viewModel?.IsKuaishouPublishRunning(account.Id, PublishPlatform.KuaishouEnterpriseRevenue) == true;

    private Task OnConfigSavedAsync(string accountId, PublishPlatform platform, string path)
    {
        _viewModel?.RememberKuaishouConfigPath(accountId, platform, path);
        RefreshSidebar();
        return Task.CompletedTask;
    }

    private Task<bool> ConfirmEditionAsync(Account account, string edition) =>
        Owner is { } owner && _viewModel is { } viewModel
            ? viewModel.ConfirmKuaishouEditionChangeAsync(account, edition,
                message => ConfirmDialog.ShowAsync(owner, "修改快手分账账号类型", message, 520, 220))
            : Task.FromResult(false);

    private async Task ApplyEditionAsync(Account account, string edition)
    {
        await _viewModel!.ApplyKuaishouEditionAsync(account, edition);
        RefreshSidebar();
    }

    private void OnAccountsImported(IReadOnlyList<Account> accounts)
    {
        foreach (var account in accounts)
            account.KuaishouRemovedFromWorkspace = false;
        _accounts?.Save();
        SelectAccount(accounts.LastOrDefault() ?? WorkspaceAccounts.FirstOrDefault());
    }

    private void OnDailyLimitReached(string accountId, string message) =>
        _dispatch?.ReportDailyUploadLimit(accountId);

    private async Task OpenAdxAsync(KuaishouAdxRequestedEventArgs request)
    {
        if (_viewModel?.GetKuaishouAdxProjectContext() is not { } context ||
            string.IsNullOrWhiteSpace(context.OriginalTitle) || string.IsNullOrWhiteSpace(context.NewTitle) ||
            _services is null || Owner is not { } owner)
        {
            if (_viewModel is not null) _viewModel.StatusMessage = "请选择包含原剧名和新剧名的快手项目。";
            return;
        }
        var dialog = new KuaishouAdxMaterialsDialog(_services.GetRequiredService<AdxAutomationService>(),
            _services.GetRequiredService<AdxBatchStore>(), _services.GetRequiredService<KuaishouAdxBatchResolver>(),
            context, request.TopCount, request.PublishLocal, _viewModel.QueueKuaishouAdxPublishAsync);
        await dialog.ShowDialog(owner);
    }

    private async Task ArchiveCheckedAsync()
    {
        var count = _viewModel?.VisibleJobs.Count(row => row.IsChecked) ?? 0;
        if (count == 0)
        {
            if (_viewModel is not null) _viewModel.StatusMessage = "请先勾选要归档的项目。";
            return;
        }
        if (Owner is not { } owner || !await ConfirmDialog.ShowAsync(owner, "确认归档",
            $"归档 {count} 个快手分账项目？归档后保留 source/workflow 目录，并删除原视频、转码视频和素材视频。", 520, 220))
            return;
        var errors = await _viewModel!.ArchiveCheckedKuaishouAsync();
        if (errors.Count > 0)
            await ConfirmDialog.ShowAsync(owner, "归档失败", string.Join(Environment.NewLine, errors), 600, 260);
    }

    public Task PrepareAsync() => EnsureInitializedAsync();

    internal async Task<IServiceProvider?> GetEmbeddedPlatformServicesAsync() =>
        await EnsureInitializedAsync() ? _services : null;

    public IReadOnlyList<TikTokQueueImportTarget> GetDownloadTargets() =>
        WorkspaceAccounts.Select(CreateTarget).OfType<TikTokQueueImportTarget>().ToArray();

    public TikTokQueueImportTarget? SelectedDownloadTarget =>
        _selectedAccount is { } account ? CreateTarget(account) : null;

    private TikTokQueueImportTarget? CreateTarget(Account account)
    {
        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return null;
        var enterprise = platform == PublishPlatform.KuaishouEnterpriseRevenue;
        var directory = enterprise ? account.KuaishouEnterpriseWorkRootDirectory : account.KuaishouPersonalWorkRootDirectory;
        if (!Directory.Exists(directory) &&
            _services?.GetRequiredService<AccountJsonSettingStore>()
                .TryLoad<KuaishouPersonalConfig>(account.Id, enterprise ? "kuaishou.enterprise.config" : "kuaishou.personal.config", out var config) == true &&
            Directory.Exists(config?.LastWorkspace))
            directory = config!.LastWorkspace;
        if (!Directory.Exists(directory))
            directory = account.WorkRootDirectory;
        return Directory.Exists(directory)
            ? new TikTokQueueImportTarget(account.Id, account.Name, Path.GetFullPath(directory),
                DramaQueuePlatforms.Kuaishou, "快手")
            : null;
    }

    public async Task ImportDownloadedProjectsAsync(TikTokQueueImportRequest request)
    {
        if (!await EnsureInitializedAsync())
            throw new InvalidOperationException(StartupError.Text ?? "快手工作区未就绪。");
        if (await _viewModel!.ImportDownloadedProjectsAsync(request) == 0)
            throw new InvalidOperationException(_viewModel.StatusMessage);
    }

    public void Dispose()
    {
        _disposed = true;
        ReleaseRuntime();
    }

    private void ReleaseRuntime()
    {
        _dispatch?.Stop();
        _dispatch = null;
        if (_viewModel is not null)
        {
            _viewModel.KuaishouDailyUploadLimitReached -= OnDailyLimitReached;
            _viewModel.Shutdown();
            _viewModel = null;
        }
        _services?.Dispose();
        _services = null;
        _lease?.Dispose();
        _lease = null;
    }

    private sealed record SidebarAccount(Account Account, string Summary)
    {
        public string Name => Account.Name;
    }
}
