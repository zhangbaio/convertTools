using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Publishing;
using PlatformPublisher.Core.Services;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly PublishJobStore _store;
    private readonly PlatformPublishCoordinator _coordinator;
    private readonly List<PublishJob> _jobs = [];
    private CancellationTokenSource? _operationCts;

    public MainWindowViewModel(PublishJobStore store, PlatformPublishCoordinator coordinator)
    {
        _store = store;
        _coordinator = coordinator;
        Platforms =
        [
            new(PublishPlatform.WeixinChannel, "视频号", "剧集上传、提交与断点恢复"),
            new(PublishPlatform.KuaishouPersonalRevenue, "快手分账 · 个人", "独立个人分账任务通道"),
            new(PublishPlatform.KuaishouEnterpriseRevenue, "快手分账 · 企业", "独立企业分账任务通道"),
        ];
        JobKinds =
        [
            new(PublishJobKind.Series, "剧集上传", "使用项目内剧集配置创建并上传分集"),
            new(PublishJobKind.DirectoryMaterials, "目录批量发表", "每个一级子目录发表一条视频"),
        ];
        _selectedPlatform = Platforms[0];
        _selectedJobKind = JobKinds[0];
        AddJobCommand = new AsyncRelayCommand(AddJobAsync, CanAddJob);
        RunSelectedCommand = new AsyncRelayCommand(RunSelectedAsync, CanRunSelected);
        OpenLoginCommand = new AsyncRelayCommand(OpenLoginAsync, CanOpenLogin);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedJob is not null && !IsBusy);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        _ = LoadAsync();
    }

    public IReadOnlyList<PlatformOptionViewModel> Platforms { get; }
    public IReadOnlyList<PublishJobKindOptionViewModel> JobKinds { get; }
    public ObservableCollection<PublishJobRowViewModel> VisibleJobs { get; } = [];
    public IAsyncRelayCommand AddJobCommand { get; }
    public IAsyncRelayCommand RunSelectedCommand { get; }
    public IAsyncRelayCommand OpenLoginCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }
    public IRelayCommand StopCommand { get; }

    [ObservableProperty]
    private PlatformOptionViewModel _selectedPlatform;

    [ObservableProperty]
    private PublishJobKindOptionViewModel _selectedJobKind;

    [ObservableProperty]
    private PublishJobRowViewModel? _selectedJob;

    [ObservableProperty]
    private string _draftProjectDirectory = string.Empty;

    [ObservableProperty]
    private string _draftConfigPath = string.Empty;

    [ObservableProperty]
    private string _draftAccountName = string.Empty;

    [ObservableProperty]
    private bool _draftDeclareOriginal = true;

    [ObservableProperty]
    private bool _draftHideLocation = true;

    [ObservableProperty]
    private bool _draftAllowDuplicatePublish;

    [ObservableProperty]
    private string _statusMessage = "多平台发布助手已启动，数据与 TikTok 助手完全隔离。";

    [ObservableProperty]
    private bool _isBusy;

    public string SelectedPlatformCapability =>
        _coordinator.GetAdapter(SelectedPlatform.Value).AvailabilityMessage;

    public string QueueSummary => $"当前平台 {VisibleJobs.Count} 条任务，共 {_jobs.Count} 条独立任务";

    partial void OnSelectedPlatformChanged(PlatformOptionViewModel value)
    {
        RefreshVisibleJobs();
        OnPropertyChanged(nameof(SelectedPlatformCapability));
        NotifyCommands();
    }

    partial void OnSelectedJobChanged(PublishJobRowViewModel? value) => NotifyCommands();
    partial void OnDraftProjectDirectoryChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();

    private async Task LoadAsync()
    {
        try
        {
            _jobs.AddRange(await _store.LoadAsync());
            RefreshVisibleJobs();
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取独立任务队列失败：{ex.Message}";
        }
    }

    private bool CanAddJob() => !IsBusy && Directory.Exists(DraftProjectDirectory);

    private async Task AddJobAsync()
    {
        var directory = Path.GetFullPath(DraftProjectDirectory);
        var adapter = _coordinator.GetAdapter(SelectedPlatform.Value);
        var job = new PublishJob
        {
            Platform = SelectedPlatform.Value,
            Kind = SelectedJobKind.Value,
            ProjectName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ProjectDirectory = directory,
            ConfigPath = DraftConfigPath.Trim(),
            AccountName = DraftAccountName.Trim(),
            DeclareOriginal = DraftDeclareOriginal,
            HideLocation = DraftHideLocation,
            AllowDuplicatePublish = DraftAllowDuplicatePublish,
            Status = adapter.IsAvailable ? PublishJobStatus.Pending : PublishJobStatus.Blocked,
            StatusMessage = adapter.IsAvailable ? "等待执行" : adapter.AvailabilityMessage,
        };
        _jobs.Add(job);
        await PersistAsync();
        RefreshVisibleJobs(job.Id);
        StatusMessage = $"已加入{job.Platform.DisplayName()}任务：{job.ProjectName}";
    }

    private bool CanRunSelected() => SelectedJob is not null && !IsBusy;
    private bool CanOpenLogin() => SelectedJob is not null && !IsBusy;

    private async Task RunSelectedAsync()
    {
        if (SelectedJob is null)
            return;

        var row = SelectedJob;
        var job = row.Model;
        var adapter = _coordinator.GetAdapter(job.Platform);
        if (!adapter.IsAvailable)
        {
            job.Status = PublishJobStatus.Blocked;
            job.StatusMessage = adapter.AvailabilityMessage;
            row.Refresh();
            StatusMessage = adapter.AvailabilityMessage;
            await PersistAsync();
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            job.Status = PublishJobStatus.Running;
            job.StatusMessage = "正在启动发布流程…";
            job.UpdatedAt = DateTimeOffset.Now;
            row.Refresh();
            await PersistAsync(cancellationToken);

            var progress = new Progress<string>(message =>
            {
                job.StatusMessage = message;
                row.Refresh();
                StatusMessage = $"[{job.ProjectName}] {message}";
            });

            try
            {
                await adapter.RunAsync(job, progress, cancellationToken);
                job.Status = PublishJobStatus.Succeeded;
                job.StatusMessage = "发布流程执行完成";
                StatusMessage = $"[{job.ProjectName}] 发布完成";
            }
            catch (OperationCanceledException)
            {
                job.Status = PublishJobStatus.Pending;
                job.StatusMessage = "已停止，可继续执行";
                StatusMessage = $"[{job.ProjectName}] 已停止";
            }
            catch (Exception ex)
            {
                job.Status = PublishJobStatus.Failed;
                job.StatusMessage = ex.Message;
                StatusMessage = $"[{job.ProjectName}] 发布失败：{ex.Message}";
            }
            finally
            {
                job.UpdatedAt = DateTimeOffset.Now;
                row.Refresh();
                await PersistAsync();
            }
        });
    }

    private async Task OpenLoginAsync()
    {
        if (SelectedJob is null)
            return;

        var job = SelectedJob.Model;
        var adapter = _coordinator.GetAdapter(job.Platform);
        if (!adapter.IsAvailable)
        {
            StatusMessage = adapter.AvailabilityMessage;
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            StatusMessage = $"[{job.ProjectName}] 已打开登录窗口，关闭浏览器后返回助手。";
            try
            {
                await adapter.OpenLoginAsync(job, cancellationToken);
                StatusMessage = $"[{job.ProjectName}] 登录窗口已关闭，登录态已保存。";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"[{job.ProjectName}] 登录操作已停止。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"[{job.ProjectName}] 打开登录窗口失败：{ex.Message}";
            }
        });
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedJob is null)
            return;

        var name = SelectedJob.ProjectName;
        _jobs.Remove(SelectedJob.Model);
        await PersistAsync();
        RefreshVisibleJobs();
        StatusMessage = $"已从独立队列移除：{name}（项目文件未删除）";
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _operationCts = new CancellationTokenSource();
        NotifyCommands();
        try
        {
            await action(_operationCts.Token);
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            IsBusy = false;
            NotifyCommands();
        }
    }

    public void Stop() => _operationCts?.Cancel();

    private async Task PersistAsync(CancellationToken cancellationToken = default) =>
        await _store.SaveAsync(_jobs, cancellationToken);

    private void RefreshVisibleJobs(string? selectId = null)
    {
        void Refresh()
        {
            VisibleJobs.Clear();
            foreach (var job in _jobs
                         .Where(job => job.Platform == SelectedPlatform.Value)
                         .OrderByDescending(job => job.CreatedAt))
            {
                VisibleJobs.Add(new PublishJobRowViewModel(job));
            }

            SelectedJob = selectId is null
                ? VisibleJobs.FirstOrDefault()
                : VisibleJobs.FirstOrDefault(row => row.Id == selectId);
            OnPropertyChanged(nameof(QueueSummary));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private void NotifyCommands()
    {
        AddJobCommand.NotifyCanExecuteChanged();
        RunSelectedCommand.NotifyCanExecuteChanged();
        OpenLoginCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
