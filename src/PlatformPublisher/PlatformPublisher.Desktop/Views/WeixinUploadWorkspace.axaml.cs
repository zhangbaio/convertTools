using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChannelsPublisher.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;
using Account = ChannelsPublisher.Core.Models.PublishAccount;
using AccountStore = ChannelsPublisher.Core.Services.AccountStore;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinUploadWorkspace : UserControl
{
    private readonly WeixinSeriesUploadView _series = new();
    private KuaishouUploadWorkspace? _platformWorkspace;
    private Panel? _accountPanel;
    private MainWindowViewModel? _platformViewModel;
    private ServiceProvider? _workflowServices;
    private FileStream? _lease;
    private Task? _initialization;
    private bool _accountPanelVisible;
    private bool _closed;
    private bool _active;
    private bool _ready;
    private string _lastStatus = string.Empty;

    public WeixinUploadWorkspace()
    {
        InitializeComponent();
        Publisher.RuntimeChanged += () => UpdateStatus(Publisher.RuntimeStatus);
        _series.RuntimeChanged += () => UpdateStatus(_series.RuntimeStatus);
    }

    private bool IsWorkflowRunning => _platformViewModel?.IsBusy == true;
    public bool IsMaterialPublishVisible { get; } = false;
    public bool HasActiveUploads => Publisher.HasActivePublish || _series.IsRunning || IsWorkflowRunning;
    public string RuntimeStatus => _lastStatus;
    public event Action? RuntimeChanged;
    public event Action? SettingsRequested;

    public void Bind(Panel accountPanel, KuaishouUploadWorkspace platformWorkspace)
    {
        _accountPanel = accountPanel;
        _platformWorkspace = platformWorkspace;
    }

    public async Task ActivateAsync()
    {
        _active = true;
        Publisher.SetWorkspaceVisible(true);
        if (_ready)
        {
            Publisher.RefreshWorkspaceAccounts();
            ApplyWorkflowAccount(Publisher.SelectedAccountProfile);
            _platformViewModel?.RefreshDramaSourceStatus();
            return;
        }
        var initialization = _initialization ??= InitializeAsync();
        await initialization;
        if (!_ready && ReferenceEquals(initialization, _initialization))
            _initialization = null;
    }

    private async Task InitializeAsync()
    {
        if (_platformWorkspace is null || _accountPanel is null || _closed)
            return;

        FileStream? lease = null;
        ServiceProvider? workflowServices = null;
        MainWindowViewModel? viewModel = null;
        RetryButton.IsVisible = false;
        StartupMessage.Text = "正在加载视频号工作区…";
        try
        {
            var services = await _platformWorkspace.GetEmbeddedPlatformServicesAsync();
            if (_closed)
                return;
            if (services is null)
                throw new InvalidOperationException("平台工作区初始化失败。请确认独立云帆客户端已退出后重试。");

            lease = ChannelsWorkspaceLease.Acquire();
            workflowServices = App.BuildServices(embeddedInTikTok: true, embeddedKuaishou: services);
            viewModel = workflowServices.GetRequiredService<MainWindowViewModel>();
            await viewModel.Initialization;
            if (_closed)
                return;
            var adapter = (WeixinChannelPublishAdapter)workflowServices.GetRequiredService<PlatformPublishCoordinator>()
                .GetAdapter(PublishPlatform.WeixinChannel);
            var workflow = new WeixinWorkflowView { DataContext = viewModel };
            workflow.BindWorkspaceAccountActions(Publisher.ShowAccountSettings);
            workflow.SettingsRequested += (_, _) => SettingsRequested?.Invoke();
            workflow.ViewLogsRequested += (_, _) => Publisher.ShowRuntimeLog();
            viewModel.BindEmbeddedWeixinWork(() => !Publisher.HasActivePublish && !_series.IsRunning,
                Publisher.PrepareSeriesSessionAsync, ConfirmQueueUploadAsync);
            var queuePath = Path.Combine(PlatformPublisherPaths.DataRoot, "weixin", "material-queue.json");
            Publisher.UseUploadWorkspace(_accountPanel, services.GetRequiredService<AccountStore>(), queuePath, workflow,
                () => !_series.IsRunning && !IsWorkflowRunning, ConfirmMaterialPublishAsync, ConfirmPendingOutcomeAsync,
                id => AccountBrowserPortAllocator.Allocate("weixin:" + id),
                id => AccountBrowserPortAllocator.Release("weixin:" + id),
                showMaterialPublish: IsMaterialPublishVisible, hasWeixinJobs: viewModel.HasWeixinJobs);
            _series.BindWorkspace(adapter, () => Publisher.SelectedAccountProfile,
                () => !Publisher.HasActivePublish && !IsWorkflowRunning,
                Publisher.PrepareSeriesSessionAsync);
            var seriesForm = new WeixinSeriesPresetView();
            seriesForm.Bind(workflowServices.GetRequiredService<WeixinSeriesPresetStore>(),
                workflowServices.GetRequiredService<WeixinSeriesFormService>());
            _series.BindSeriesForm((directory, config, mode) =>
            {
                seriesForm.OpenProject(directory, config, mode);
                Publisher.ShowSeriesFormConfig();
            });
            Publisher.SetSeriesFormConfigContent(seriesForm);
            Publisher.SetSeriesPublishContent(_series);
            Publisher.SetWorkspaceRunLogs(new WeixinRuntimeLogView { DataContext = viewModel });
            _platformViewModel = viewModel;
            _workflowServices = workflowServices;
            viewModel = null;
            workflowServices = null;
            void LoadSeriesAccount(Account? account)
            {
                var workDirectory = Publisher.SelectedAccountWorkDirectory;
                _series.SetAccount(account, workDirectory);
                seriesForm.SetAccount(account, workDirectory);
            }
            Publisher.SelectedAccountChanged += LoadSeriesAccount;
            _platformViewModel.ActiveAccountWorkRootDirectoryChanged += () =>
            {
                Publisher.RefreshSelectedAccountWorkDirectory();
                LoadSeriesAccount(Publisher.SelectedAccountProfile);
            };
            Publisher.SelectedAccountChanged += ApplyWorkflowAccount;
            _platformViewModel.PropertyChanged += OnWorkflowPropertyChanged;
            LoadSeriesAccount(Publisher.SelectedAccountProfile);
            ApplyWorkflowAccount(Publisher.SelectedAccountProfile);
            _ready = true;
            _lease = lease;
            lease = null;
            SetAccountPanelVisible(_accountPanelVisible && _active);
            StartupPanel.IsVisible = false;
            Publisher.IsVisible = true;
        }
        catch (Exception ex)
        {
            StartupMessage.Text = $"视频号工作区加载失败：{ex.Message}";
            RetryButton.IsVisible = true;
        }
        finally
        {
            viewModel?.Shutdown();
            if (workflowServices is not null)
                await workflowServices.DisposeAsync();
            lease?.Dispose();
        }
    }

    public void Deactivate()
    {
        _active = false;
        Publisher.SetWorkspaceVisible(false);
    }

    public void SetAccountPanelVisible(bool visible)
    {
        _accountPanelVisible = visible;
        if (_ready)
            Publisher.SetWorkspaceAccountPanelVisible(visible);
    }

    public async Task<string?> QueueClippedMaterialsAsync(IReadOnlyList<string> paths)
    {
        if (!IsMaterialPublishVisible)
            return "视频号素材发布暂未开放。";
        if (!_ready)
            return "视频号工作区尚未就绪，请打开入口检查状态。";
        if (Publisher.SelectedAccountProfile is not { } account || TopLevel.GetTopLevel(this) is not Window owner)
            return "请先在视频号工作区选择发布账号，再发送剪辑成片。";
        if (!await ConfirmDialog.ShowAsync(owner, "发送成片到视频号",
                $"将 {paths.Count} 条成片加入「{account.Name}」的素材发布队列。"))
            return "已取消发送成片。";
        return Publisher.QueueWorkspaceMaterials(account.Id, paths);
    }

    public void StopUploads()
    {
        Publisher.StopWorkspace();
        _series.Stop();
        _platformViewModel?.Stop();
    }

    public async Task CloseWorkspaceAsync()
    {
        _closed = true;
        _series.Stop();
        if (_platformViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= OnWorkflowPropertyChanged;
            viewModel.Shutdown();
            Publisher.SelectedAccountChanged -= ApplyWorkflowAccount;
        }
        try
        {
            await Publisher.DisposeWorkspaceAsync();
            while (IsWorkflowRunning)
                await Task.Delay(100);
        }
        finally
        {
            if (_workflowServices is not null)
                await _workflowServices.DisposeAsync();
            _workflowServices = null;
            _lease?.Dispose();
            _lease = null;
        }
    }

    private void UpdateStatus(string status)
    {
        _lastStatus = status;
        RuntimeChanged?.Invoke();
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e) => await ActivateAsync();

    private Task<bool> ConfirmMaterialPublishAsync(int count) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? ConfirmDialog.ShowAsync(owner, "确认发表到微信视频号", $"将按任务绑定的账号正式发表 {count} 条素材。继续发表？")
            : Task.FromResult(false);

    private Task<bool> ConfirmQueueUploadAsync(int count) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? ConfirmDialog.ShowAsync(owner, "执行视频号剧集队列",
                $"将按项目配置上传 {count} 部剧集。启用了自动提审的项目会提交审核。继续执行？")
            : Task.FromResult(false);

    private Task<bool?> ConfirmPendingOutcomeAsync(string videoName) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? ConfirmDialog.ShowChoiceAsync(owner, "核对视频号提交记录",
                $"请在该任务账号的视频号后台核对「{videoName}」的作品或草稿。只有确认平台没有记录时，才恢复为待发布。",
                "平台已有记录", "平台没有记录")
            : Task.FromResult<bool?>(null);

    private void ApplyWorkflowAccount(Account? account)
    {
        if (_platformViewModel is { IsBusy: false } viewModel)
            viewModel.UseEmbeddedWeixinAccount(Publisher.AccountProfiles, account);
    }

    private void OnWorkflowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
        {
            Publisher.SetWorkspaceAccountPanelEnabled(!IsWorkflowRunning);
            ApplyWorkflowAccount(Publisher.SelectedAccountProfile);
        }
        if (e.PropertyName is nameof(MainWindowViewModel.IsBusy) or nameof(MainWindowViewModel.StatusMessage) &&
            _platformViewModel is { } viewModel)
            UpdateStatus(viewModel.StatusMessage);
    }
}
