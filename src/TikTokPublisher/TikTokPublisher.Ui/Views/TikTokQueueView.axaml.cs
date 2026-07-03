using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TikTokPublisher.Core.Config;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokQueueView : UserControl
{
    private MainViewModel? _vm;
    private BrowserSessionHost? _browserHost;
    private readonly TikTokPlaywrightAutomation _automation = new();
    private PublishScheduler? _scheduler;
    private bool _ready;
    private TikTokPublishConfig _publishConfig = TikTokPublishConfig.Load();
    private CancellationTokenSource? _publishCts;
    private readonly PublishRunStateStore _runState = PublishRunStateStore.Load();

    public event EventHandler? OpenBrowserRequested;
    public event EventHandler? OpenLogsRequested;

    public TikTokQueueView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public void Initialize(MainViewModel vm, BrowserSessionHost browserHost)
    {
        _vm = vm;
        _browserHost = browserHost;
        DataContext = vm;
        vm.NavigateRequested += OnNavigateRequested;
        vm.AccountSwitchRequested += OnAccountSwitchRequested;
        vm.PropertyChanged += OnManualInterventionPropertyChanged;
        RefreshManualInterventionButtons();
    }

    private void OnManualInterventionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ManualInterventionPending))
            Dispatcher.UIThread.Post(RefreshManualInterventionButtons);
    }

    private void RefreshManualInterventionButtons()
    {
        var pending = _vm?.ManualInterventionPending == true;
        if (ManualSuccessButton is not null) ManualSuccessButton.IsEnabled = pending;
        if (ManualFailButton is not null) ManualFailButton.IsEnabled = pending;
    }

    private void OnManualInterventionSuccessClick(object? sender, RoutedEventArgs e)
        => _vm?.ResolveManualIntervention("success");

    private void OnManualInterventionFailClick(object? sender, RoutedEventArgs e)
        => _vm?.ResolveManualIntervention("failed");

    public async void OpenAccountSettings() => await ShowAccountSettingsDialogAsync();

    public void SyncWithPython() => OnSyncWithPythonClick(null, null!);

    public void ImportFromPython() => OnImportFromPythonClick(null, null!);

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ready = true;
        ApplyConfigToVm();
        if (!string.IsNullOrWhiteSpace(_vm?.WorkspacePath))
            _vm.RefreshWorkspaceProjects();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NavigateRequested -= OnNavigateRequested;
            _vm.AccountSwitchRequested -= OnAccountSwitchRequested;
        }

        if (_vm is null && DataContext is MainViewModel vm)
            Initialize(vm, _browserHost ?? new BrowserSessionHost());
    }

    private void ApplyConfigToVm()
    {
        var vm = _vm;
        if (vm is null) return;
        var fa = _publishConfig.FinalAction switch
        {
            "publish" => FinalAction.Publish,
            "draft" => FinalAction.Draft,
            _ => FinalAction.None,
        };
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
    }

    private void ApplyConfigDefaults(PublishItem item)
    {
        var c = _publishConfig;
        if (c.FillDescription && string.IsNullOrEmpty(item.Description) && !string.IsNullOrWhiteSpace(c.DescriptionTemplate))
            item.Description = c.DescriptionTemplate.Trim();
        if (string.IsNullOrEmpty(item.DramaName) && !string.IsNullOrWhiteSpace(c.DramaName))
            item.DramaName = c.DramaName.Trim();
        if (string.IsNullOrEmpty(item.Title))
        {
            item.Title = !string.IsNullOrWhiteSpace(item.DramaName)
                ? item.DramaName!
                : Path.GetFileNameWithoutExtension(item.VideoPath);
        }
        if (c.ReplaceCover && item.CoverPath is null && !string.IsNullOrWhiteSpace(c.CoverImagePath))
            item.CoverPath = c.CoverImagePath.Trim();
    }

    private void OnAccountSwitchRequested(AccountItemViewModel account) =>
        _browserHost?.ShowAccount(account);

    private void OnNavigateRequested(AccountItemViewModel account, string url)
    {
        if (!_ready || _browserHost is null) return;
        var host = _browserHost.GetOrCreateHost(account);
        _browserHost.ShowAccount(account);
        host.Navigate(url);
        OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPickWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (Storage is null || _vm is null) return;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 TikTok 上传工作目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        _vm.SetWorkspacePath(path);

        var boundId = WorkspaceBindingService.ResolveAccountProfileId(path);
        if (!string.IsNullOrWhiteSpace(boundId))
        {
            var bound = _vm.FindAccount(boundId);
            if (bound is not null && bound.Id != _vm.SelectedAccount?.Id)
            {
                _vm.SelectedAccount = bound;
                _vm.StatusMessage = $"工作目录已绑定账号「{bound.DisplayName}」，已自动切换";
            }
        }
    }

    private async Task ShowAccountSettingsDialogAsync()
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            if (vm is not null) vm.StatusMessage = "请先选择要编辑的账号";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var ok = await new AccountSettingsDialog(account.Model).ShowDialog<bool>(owner);
        if (!ok) return;

        vm.SaveAccountProfile(account.Model);
        vm.StatusMessage = $"已保存账号「{account.DisplayName}」的设置";
    }

    private void OnSyncWithPythonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            if (!File.Exists(AppPaths.PythonDatabaseFile))
                _vm.StatusMessage = $"未找到 Python 数据库，将创建：{AppPaths.PythonDatabaseFile}";
            _vm.SyncWithPythonClient(merge: true);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Python 账号同步失败：{ex.Message}";
        }
    }

    private void OnImportFromPythonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            if (!File.Exists(AppPaths.PythonDatabaseFile))
            {
                _vm.StatusMessage = $"未找到 Python 数据库：{AppPaths.PythonDatabaseFile}";
                return;
            }
            _vm.ImportFromPythonClient(merge: true);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Python 导入失败：{ex.Message}";
        }
    }

    private void OnScanWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (string.IsNullOrWhiteSpace(_vm.WorkspacePath))
        {
            _vm.StatusMessage = "请先选择工作目录";
            return;
        }
        _vm.RefreshWorkspaceProjects();
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedAccount is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先选择账号";
            return;
        }

        _vm.BeginAccountLogin(forceRelogin: false);
        OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSelectAllQueueClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectAll();
    }

    private void OnClearQueueSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectedItems.Clear();
    }

    private void OnBindSelectedAccountClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            vm!.StatusMessage = "请先选择账号";
            return;
        }

        var projects = new List<QueueProjectItem>();
        if (QueueProjectList?.SelectedItems is { Count: > 0 } queueSelected)
        {
            foreach (var selected in queueSelected)
            {
                if (selected is QueueProjectRowViewModel row)
                    projects.Add(row.Item);
            }
        }

        if (projects.Count == 0)
        {
            vm.StatusMessage = "请先在队列表格中选择要绑定的项目";
            return;
        }

        if (!vm.BindAccountToProjects(account, projects))
        {
            vm.StatusMessage = "绑定失败（工作目录未设置？）";
            return;
        }

        vm.StatusMessage = $"已将 {projects.Count} 个项目绑定到「{account.DisplayName}」";
    }

    private void OnBindAllPendingClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            vm!.StatusMessage = "请先选择账号";
            return;
        }

        var pending = vm.GetPendingUploadProjects();
        if (pending.Count == 0)
        {
            vm.StatusMessage = "没有待上传项目";
            return;
        }

        if (!vm.BindAccountToProjects(account, pending))
        {
            vm.StatusMessage = "绑定失败";
            return;
        }

        vm.StatusMessage = $"已将 {pending.Count} 个待上传项目绑定到「{account.DisplayName}」";
    }

    private IEnumerable<QueueProjectRowViewModel> GetSelectedQueueRows() =>
        QueueProjectList.SelectedItems?.OfType<QueueProjectRowViewModel>() ?? Enumerable.Empty<QueueProjectRowViewModel>();

    private async void OnArchiveSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ArchiveSelectedQueueProjectsAsync(GetSelectedQueueRows());
    }

    private void OnRemoveSelectedClick(object? sender, RoutedEventArgs e)
    {
        _vm?.RemoveSelectedQueueProjects(GetSelectedQueueRows());
    }

    private async void OnStartQueueClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (vm.IsQueueRunning)
        {
            vm.StatusMessage = "队列已在运行中";
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.WorkspacePath))
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }

        if (vm.FilteredQueueProjectRows.Count == 0)
        {
            vm.StatusMessage = "队列为空，请先刷新项目";
            return;
        }

        var host = CreateQueuePublishHost();
        var ct = vm.BeginQueueRun();
        SetQueueRunning(true);
        vm.StatusMessage = "TikTok 队列执行中…";
        try
        {
            var summary = await vm.RunQueueWorkerAsync(
                host,
                p => Dispatcher.UIThread.Post(() => vm.HandleQueueWorkerProgress(p)),
                items => Dispatcher.UIThread.Post(() => vm.ApplyPersistedQueueItems(items)),
                ct);
            if (summary is not null && !summary.Stopped)
                vm.StatusMessage = $"队列结束：成功 {summary.SuccessCount}，失败 {summary.FailedCount}";
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "队列已停止";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"队列出错：{ex.Message}";
        }
        finally
        {
            vm.EndQueueRun();
            SetQueueRunning(false);
        }
    }

    private void OnStopQueueClick(object? sender, RoutedEventArgs e)
    {
        _vm?.RequestStopQueue();
        if (StopQueueButton is not null) StopQueueButton.IsEnabled = false;
        if (_vm is not null) _vm.StatusMessage = "正在停止队列…";
    }

    private void SetQueueRunning(bool running)
    {
        if (StartQueueButton is not null) StartQueueButton.IsEnabled = !running;
        if (StopQueueButton is not null) StopQueueButton.IsEnabled = running;
    }

    private QueuePublishHost CreateQueuePublishHost() => new(
        EnsureAccountBrowserReadyAsync,
        PublishQueueProjectAsync);

    private async Task<bool> EnsureAccountBrowserReadyAsync(TikTokAccountProfile account, CancellationToken ct)
    {
        if (_browserHost is null || _vm is null) return false;

        var accountVm = _vm.FindAccount(account.Id) ?? _vm.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (accountVm is null) return false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _browserHost.GetOrCreateHost(accountVm);
            _browserHost.ShowAccount(accountVm);
        });

        return await _browserHost.EnsureReadyAsync(account, ct);
    }

    private async Task<PublishResult> PublishQueueProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        Action<string> log,
        CancellationToken ct)
    {
        var host = _browserHost?.TryGetHost(account.Id);
        if (host?.CdpEndpoint is null)
            return PublishResult.Fail("浏览器 CDP 未就绪，请先在「浏览器」页登录");

        var item = QueuePublishHost.ToPublishItem(project);
        if (string.IsNullOrWhiteSpace(item.VideoPath))
            return PublishResult.Fail("项目没有可用视频");

        ApplyConfigDefaults(item);
        return await _automation.PublishAsync(account, item, host.CdpEndpoint, finalAction, log, ct);
    }

    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { vm!.StatusMessage = "请先选择账号"; return; }
        if (_browserHost?.TryGetHost(account.Id)?.CdpEndpoint is not { } cdp)
        {
            vm.StatusMessage = "浏览器未就绪，请先打开「浏览器」页登录";
            OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发布的视频",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        var item = new PublishItem { VideoPath = file.Path.LocalPath, Description = "【TikTok 发布测试】" };
        ApplyConfigDefaults(item);
        _scheduler ??= new PublishScheduler(_automation);
        var job = new AccountPublishJob(account.Model, cdp, new[] { item });

        vm.StatusMessage = $"[{account.DisplayName}] 发布中：{item.DisplayName}…";
        try
        {
            await _scheduler.RunAsync(new[] { job }, FinalAction.None, maxParallelAccounts: 1,
                p => Dispatcher.UIThread.Post(() => vm.StatusMessage = $"[{p.AccountName}] {p.ItemName}：{p.Message}"),
                CancellationToken.None);
        }
        catch (Exception ex) { vm.StatusMessage = $"发布失败：{ex.Message}"; }
    }

    private async void OnPublishConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new PublishConfigDialog().ShowDialog<bool>(owner);
        if (ok)
        {
            _publishConfig = TikTokPublishConfig.Load();
            ApplyConfigToVm();
            if (_vm != null) _vm.StatusMessage = "发布配置已保存";
        }
    }

    private async void OnAddMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { vm!.StatusMessage = "请先选择账号"; return; }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择素材视频（可多选）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var n = 0;
        foreach (var f in files)
        {
            var item = new PublishItem { VideoPath = f.Path.LocalPath };
            ApplyConfigDefaults(item);
            vm.Tasks.Add(new PublishTaskItemViewModel(item, account));
            n++;
        }
        if (n > 0) vm.StatusMessage = $"已添加 {n} 条素材到「{account.DisplayName}」";
    }

    private async void OnImportTasksClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || Storage is null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入发布任务清单(JSON)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        PublishTaskFile taskFile;
        try { taskFile = PublishTaskFile.Load(file.Path.LocalPath); }
        catch (Exception ex) { vm.StatusMessage = $"导入失败：{ex.Message}"; return; }

        var added = 0;
        var skipped = 0;
        foreach (var dto in taskFile.Tasks)
        {
            var acct = vm.FindAccount(dto.Account);
            if (acct is null) { skipped++; continue; }
            vm.Tasks.Add(new PublishTaskItemViewModel(dto.ToItem(), acct));
            added++;
        }
        var fa = taskFile.ResolveFinalAction();
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
        vm.StatusMessage = $"已导入 {added} 条任务" + (skipped > 0 ? $"，跳过 {skipped} 条（账号未匹配）" : "");
    }

    private void OnClearDoneClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var done = vm.Tasks.Where(t => t.Status == PublishTaskStatus.Done).ToList();
        foreach (var t in done) vm.Tasks.Remove(t);
        vm.StatusMessage = $"已清空 {done.Count} 条完成任务";
    }

    private async void OnStartPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (_publishCts is not null) { vm.StatusMessage = "发布进行中…"; return; }

        var strategy = (_publishConfig.RunStrategy ?? "all").Trim().ToLowerInvariant();
        var candidates = (strategy == "retry_failed"
                ? vm.Tasks.Where(t => t.Status == PublishTaskStatus.Failed)
                : vm.Tasks.Where(t => t.Status is PublishTaskStatus.Pending or PublishTaskStatus.Failed))
            .ToList();
        if (candidates.Count == 0) { vm.StatusMessage = "没有待发布任务"; return; }

        var resumed = 0;
        if (strategy == "resume")
        {
            var remaining = new List<PublishTaskItemViewModel>();
            foreach (var t in candidates)
            {
                if (_runState.IsDone(PublishRunStateStore.SignatureFor(t.Account.Id, t.Item)))
                {
                    t.Status = PublishTaskStatus.Done;
                    t.Message = "已发布·续传跳过";
                    resumed++;
                }
                else remaining.Add(t);
            }
            candidates = remaining;
            if (candidates.Count == 0)
            {
                vm.StatusMessage = resumed > 0 ? $"全部 {resumed} 条已在续传记录中" : "没有待发布任务";
                return;
            }
        }

        var jobs = new List<AccountPublishJob>();
        foreach (var group in candidates.GroupBy(t => t.Account.Id))
        {
            var acctVm = group.First().Account;
            var cdp = _browserHost?.TryGetHost(acctVm.Id)?.CdpEndpoint;
            if (cdp is null)
            {
                foreach (var t in group) { t.Status = PublishTaskStatus.Failed; t.Message = "浏览器未就绪"; }
                continue;
            }
            foreach (var t in group) { t.Status = PublishTaskStatus.Pending; t.Message = "排队中"; }
            jobs.Add(new AccountPublishJob(acctVm.Model, cdp, group.Select(t => t.Item).ToList()));
        }
        if (jobs.Count == 0) { vm.StatusMessage = "无可发布账号（请先在浏览器页登录）"; return; }

        _scheduler ??= new PublishScheduler(_automation);
        var finalAction = vm.SelectedFinalAction?.Value ?? FinalAction.None;
        _publishCts = new CancellationTokenSource();
        SetPublishing(true);
        vm.StatusMessage = $"开始发布：{jobs.Count} 账号 / {candidates.Count} 素材（并发 {vm.MaxParallel}）";
        try
        {
            await _scheduler.RunAsync(jobs, finalAction, vm.MaxParallel,
                p => Dispatcher.UIThread.Post(() => UpdateTaskProgress(p)), _publishCts.Token);
            vm.StatusMessage = "发布结束";
        }
        catch (OperationCanceledException)
        {
            foreach (var t in vm.Tasks.Where(t => t.Status is PublishTaskStatus.Running or PublishTaskStatus.Pending))
            {
                t.Status = PublishTaskStatus.Pending;
                t.Message = "已停止";
            }
            vm.StatusMessage = "已停止";
        }
        catch (Exception ex) { vm.StatusMessage = $"发布出错：{ex.Message}"; }
        finally
        {
            _publishCts?.Dispose();
            _publishCts = null;
            SetPublishing(false);
        }
    }

    private void OnStopPublishClick(object? sender, RoutedEventArgs e)
    {
        _publishCts?.Cancel();
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = false;
        if (_vm != null) _vm.StatusMessage = "正在停止…";
    }

    private void OnClearResumeClick(object? sender, RoutedEventArgs e)
    {
        var n = _runState.Count;
        _runState.Reset();
        if (_vm != null) _vm.StatusMessage = $"已清除续传记录（{n} 条）";
    }

    private void SetPublishing(bool running)
    {
        if (StartPublishButton is not null) StartPublishButton.IsEnabled = !running;
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = running;
    }

    private void UpdateTaskProgress(PublishProgress p)
    {
        var vm = _vm;
        if (vm is null) return;
        var task = vm.Tasks.FirstOrDefault(t =>
            t.Account.Id == p.AccountId && t.VideoName == p.ItemName && t.Status != PublishTaskStatus.Done);
        if (task is null) return;
        task.Message = p.Message;
        task.Status = p.Done ? (p.Ok ? PublishTaskStatus.Done : PublishTaskStatus.Failed) : PublishTaskStatus.Running;
        if (p.Done && p.Ok)
        {
            _runState.MarkDone(PublishRunStateStore.SignatureFor(task.Account.Id, task.Item), task.VideoName, task.AccountName);
            if (!string.IsNullOrWhiteSpace(task.Item.ProjectDir) && !string.IsNullOrWhiteSpace(vm.WorkspacePath))
                vm.MarkProjectUploadCompleted(task.Item.ProjectDir);
        }
    }
}
