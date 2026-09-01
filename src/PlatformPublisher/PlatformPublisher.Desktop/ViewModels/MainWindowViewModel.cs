using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly PublishJobStore _store;
    private readonly PublishAccountStore _accountStore;
    private readonly PlatformPublishCoordinator _coordinator;
    private readonly List<PublishJob> _jobs = [];
    private readonly List<PublishAccount> _accounts = [];
    private readonly DispatcherTimer _scheduleTimer;
    private CancellationTokenSource? _operationCts;
    private bool _scheduleTickRunning;

    public MainWindowViewModel(
        PublishJobStore store,
        PublishAccountStore accountStore,
        PlatformPublishCoordinator coordinator)
    {
        _store = store;
        _accountStore = accountStore;
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
            new(PublishJobKind.SystemHighlight, "系统高光发表", "按剧名选择平台系统高光并发表"),
        ];
        _selectedPlatform = Platforms[0];
        _selectedJobKind = JobKinds[0];
        AddJobCommand = new AsyncRelayCommand(AddJobAsync, CanAddJob);
        RunSelectedCommand = new AsyncRelayCommand(RunSelectedAsync, CanRunSelected);
        RunRunnableCommand = new AsyncRelayCommand(RunRunnableAsync, CanRunRunnable);
        NewAccountCommand = new RelayCommand(BeginNewAccount, () => !IsBusy);
        SaveAccountCommand = new AsyncRelayCommand(SaveAccountAsync, CanSaveAccount);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync, () => SelectedAccount is not null && !IsBusy);
        OpenLoginCommand = new AsyncRelayCommand(OpenLoginAsync, CanOpenLogin);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedJob is not null && !IsBusy);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += OnScheduleTimerTick;
        _scheduleTimer.Start();
        _ = LoadAsync();
    }

    public IReadOnlyList<PlatformOptionViewModel> Platforms { get; }
    public IReadOnlyList<PublishJobKindOptionViewModel> JobKinds { get; }
    public ObservableCollection<PublishJobRowViewModel> VisibleJobs { get; } = [];
    public ObservableCollection<PublishAccountItemViewModel> VisibleAccounts { get; } = [];
    public IAsyncRelayCommand AddJobCommand { get; }
    public IAsyncRelayCommand RunSelectedCommand { get; }
    public IAsyncRelayCommand RunRunnableCommand { get; }
    public IRelayCommand NewAccountCommand { get; }
    public IAsyncRelayCommand SaveAccountCommand { get; }
    public IAsyncRelayCommand DeleteAccountCommand { get; }
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
    private PublishAccountItemViewModel? _selectedAccount;

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
    private bool _draftScheduleEnabled;

    [ObservableProperty]
    private string _draftScheduleText = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private string _draftDramaTitle = string.Empty;

    [ObservableProperty]
    private int _draftPublishCount = 1;

    [ObservableProperty]
    private string _draftPublishVideoTypes = "混剪,解说,切片";

    [ObservableProperty]
    private bool _draftRegenerateHighlightsAfterPublish;

    [ObservableProperty]
    private string _statusMessage = "多平台发布助手已启动，数据与 TikTok 助手完全隔离。";

    [ObservableProperty]
    private bool _isBusy;

    public string SelectedPlatformCapability =>
        _coordinator.GetAdapter(SelectedPlatform.Value).AvailabilityMessage;

    public string QueueSummary =>
        $"当前平台 {VisibleJobs.Count} 条任务、{VisibleAccounts.Count} 个账号，共 {_jobs.Count} 条独立任务";

    partial void OnSelectedPlatformChanged(PlatformOptionViewModel value)
    {
        RefreshVisibleJobs();
        RefreshVisibleAccounts();
        OnPropertyChanged(nameof(SelectedPlatformCapability));
        NotifyCommands();
    }

    partial void OnSelectedJobChanged(PublishJobRowViewModel? value) => NotifyCommands();
    partial void OnSelectedAccountChanged(PublishAccountItemViewModel? value)
    {
        if (value is not null)
        {
            DraftAccountName = value.Model.Name;
            DraftConfigPath = value.Model.BaseConfigPath;
        }
        NotifyCommands();
    }
    partial void OnSelectedJobKindChanged(PublishJobKindOptionViewModel value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftProjectDirectoryChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftDramaTitleChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftAccountNameChanged(string value) => SaveAccountCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleEnabledChanged(bool value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleTextChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();

    private async Task LoadAsync()
    {
        try
        {
            var loadJobs = _store.LoadAsync();
            var loadAccounts = _accountStore.LoadAsync();
            await Task.WhenAll(loadJobs, loadAccounts);
            _jobs.AddRange(await loadJobs);
            _accounts.AddRange(await loadAccounts);
            var recovered = PublishSchedulePolicy.RecoverInterrupted(_jobs);
            if (recovered > 0)
            {
                await PersistAsync();
                StatusMessage = $"已恢复 {recovered} 条上次意外中断的任务。";
            }
            RefreshVisibleJobs();
            RefreshVisibleAccounts();
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取独立任务队列失败：{ex.Message}";
        }
    }

    private bool CanAddJob() =>
        !IsBusy &&
        Directory.Exists(DraftProjectDirectory) &&
        (SelectedJobKind.Value != PublishJobKind.SystemHighlight || !string.IsNullOrWhiteSpace(DraftDramaTitle)) &&
        (!DraftScheduleEnabled || PublishSchedulePolicy.TryParseLocal(DraftScheduleText, out _));

    private async Task AddJobAsync()
    {
        var directory = Path.GetFullPath(DraftProjectDirectory);
        var adapter = _coordinator.GetAdapter(SelectedPlatform.Value);
        DateTimeOffset? scheduledAt = null;
        if (DraftScheduleEnabled)
        {
            if (!PublishSchedulePolicy.TryParseLocal(DraftScheduleText, out var parsedSchedule))
            {
                StatusMessage = "定时时间格式无效，请使用 yyyy-MM-dd HH:mm。";
                return;
            }

            scheduledAt = parsedSchedule;
        }

        var job = new PublishJob
        {
            Platform = SelectedPlatform.Value,
            Kind = SelectedJobKind.Value,
            ProjectName = SelectedJobKind.Value == PublishJobKind.SystemHighlight
                ? DraftDramaTitle.Trim()
                : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ProjectDirectory = directory,
            ConfigPath = DraftConfigPath.Trim(),
            AccountId = SelectedAccount?.Model.Id ?? string.Empty,
            AccountName = DraftAccountName.Trim(),
            DeclareOriginal = DraftDeclareOriginal,
            HideLocation = DraftHideLocation,
            AllowDuplicatePublish = DraftAllowDuplicatePublish,
            DramaTitle = DraftDramaTitle.Trim(),
            PublishCount = Math.Clamp(DraftPublishCount, 1, 100),
            PublishVideoTypes = DraftPublishVideoTypes.Trim(),
            RegenerateHighlightsAfterPublish = DraftRegenerateHighlightsAfterPublish,
            ScheduledAt = scheduledAt,
            Status = adapter.IsAvailable ? PublishJobStatus.Pending : PublishJobStatus.Blocked,
            StatusMessage = adapter.IsAvailable
                ? scheduledAt is null ? "等待执行" : $"计划于 {scheduledAt:yyyy-MM-dd HH:mm} 执行"
                : adapter.AvailabilityMessage,
        };
        _jobs.Add(job);
        await PersistAsync();
        RefreshVisibleJobs(job.Id);
        StatusMessage = $"已加入{job.Platform.DisplayName()}任务：{job.ProjectName}";
    }

    private bool CanRunSelected() => SelectedJob is not null && !IsBusy;
    private bool CanRunRunnable() => !IsBusy && VisibleJobs.Any(row =>
        PublishSchedulePolicy.CanRunNow(row.Model, DateTimeOffset.Now) &&
        _coordinator.GetAdapter(row.Platform).IsAvailable);
    private bool CanOpenLogin() => SelectedJob is not null && !IsBusy;

    private bool CanSaveAccount() => !IsBusy && !string.IsNullOrWhiteSpace(DraftAccountName);

    private void BeginNewAccount()
    {
        SelectedAccount = null;
        DraftAccountName = string.Empty;
        DraftConfigPath = string.Empty;
        StatusMessage = $"正在新增{SelectedPlatform.Name}账号档案。";
    }

    private async Task SaveAccountAsync()
    {
        var account = SelectedAccount?.Model;
        if (account is null || account.Platform != SelectedPlatform.Value)
        {
            account = new PublishAccount
            {
                Platform = SelectedPlatform.Value,
                CreatedAt = DateTimeOffset.Now,
            };
            _accounts.Add(account);
        }

        account.Name = DraftAccountName.Trim();
        account.BaseConfigPath = DraftConfigPath.Trim();
        account.UpdatedAt = DateTimeOffset.Now;
        await _accountStore.SaveAsync(_accounts);
        RefreshVisibleAccounts(account.Id);
        StatusMessage = $"已保存{account.Platform.DisplayName()}账号：{account.Name}";
    }

    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount is null)
            return;

        var account = SelectedAccount.Model;
        _accounts.Remove(account);
        await _accountStore.SaveAsync(_accounts);
        RefreshVisibleAccounts();
        StatusMessage = $"已删除账号档案：{account.Name}；授权文件和历史任务均未删除。";
    }

    private async Task RunSelectedAsync()
    {
        if (SelectedJob is null)
            return;

        await RunRowsAsync([SelectedJob], clearSchedule: true);
    }

    private async Task RunRunnableAsync()
    {
        var now = DateTimeOffset.Now;
        var rows = VisibleJobs
            .Where(row => PublishSchedulePolicy.CanRunNow(row.Model, now))
            .Where(row => _coordinator.GetAdapter(row.Platform).IsAvailable)
            .ToArray();
        await RunRowsAsync(rows, clearSchedule: true);
    }

    private async Task RunRowsAsync(IReadOnlyList<PublishJobRowViewModel> rows, bool clearSchedule)
    {
        if (rows.Count == 0)
            return;

        await RunBusyAsync(async cancellationToken =>
        {
            try
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StatusMessage = $"批量执行 {index + 1}/{rows.Count}：{rows[index].ProjectName}";
                    await ExecuteJobAsync(rows[index], clearSchedule, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "批量执行已停止，未开始的任务仍保留在队列中。";
            }
        });
    }

    private async Task ExecuteJobAsync(
        PublishJobRowViewModel row,
        bool clearSchedule,
        CancellationToken cancellationToken)
    {
        var job = row.Model;
        var adapter = _coordinator.GetAdapter(job.Platform);
        if (!adapter.IsAvailable)
        {
            job.Status = PublishJobStatus.Blocked;
            job.StatusMessage = adapter.AvailabilityMessage;
            row.Refresh();
            StatusMessage = adapter.AvailabilityMessage;
            await PersistAsync(cancellationToken);
            return;
        }

        if (clearSchedule)
            job.ScheduledAt = null;
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

    public void Shutdown()
    {
        _scheduleTimer.Stop();
        Stop();
    }

    private async void OnScheduleTimerTick(object? sender, EventArgs e)
    {
        if (_scheduleTickRunning || IsBusy)
            return;

        var dueJobs = _jobs
            .Where(job => PublishSchedulePolicy.IsDue(job, DateTimeOffset.Now))
            .OrderBy(job => job.ScheduledAt)
            .ToArray();
        if (dueJobs.Length == 0)
            return;

        _scheduleTickRunning = true;
        try
        {
            var rows = dueJobs.Select(job =>
                VisibleJobs.FirstOrDefault(row => row.Id == job.Id) ?? new PublishJobRowViewModel(job)).ToArray();
            StatusMessage = $"检测到 {rows.Length} 条到期任务，开始自动执行。";
            await RunRowsAsync(rows, clearSchedule: true);
            RefreshVisibleJobs(SelectedJob?.Id);
        }
        finally
        {
            _scheduleTickRunning = false;
        }
    }

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

    private void RefreshVisibleAccounts(string? selectId = null)
    {
        void Refresh()
        {
            VisibleAccounts.Clear();
            foreach (var account in _accounts
                         .Where(account => account.Platform == SelectedPlatform.Value)
                         .OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                VisibleAccounts.Add(new PublishAccountItemViewModel(account));
            }

            SelectedAccount = selectId is null
                ? VisibleAccounts.FirstOrDefault()
                : VisibleAccounts.FirstOrDefault(account => account.Id == selectId);
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
        RunRunnableCommand.NotifyCanExecuteChanged();
        NewAccountCommand.NotifyCanExecuteChanged();
        SaveAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        OpenLoginCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
