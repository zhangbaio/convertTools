using Avalonia.Threading;
using ChannelsPublisher.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Desktop.Services;
using ShortDrama.Desktop.Views;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private const string ProjectMetadataFileName = "shortdrama-project.json";
    private const string WorkspaceAccountProfileConfigName = ".weixin-channel-workspace.json";
    private static readonly System.Text.Json.JsonSerializerOptions MaterialUploadJsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
    private static readonly string[] MaterialUploadProjectAccountKeys =
    [
        "materialUploadAccountProfileId",
        "material_upload_account_profile_id"
    ];
    private static readonly string[] MaterialUploadWorkspaceAccountKeys =
    [
        "material_upload_account_profile_id",
        "materialUploadAccountProfileId"
    ];
    private bool _applyingMaterialUploadPageState;
    private DispatcherTimer? _materialSystemHighlightScheduleTimer;
    private bool _checkingMaterialSystemHighlightSchedule;
    private bool _applyingMaterialUploadAccountSelection;

    public ObservableCollection<ProjectListItemViewModel> MaterialUploadProjects { get; } = [];
    public ObservableCollection<MaterialUploadAccountItemViewModel> MaterialUploadAccounts { get; } = [];
    public ObservableCollection<MaterialUploadAccountItemViewModel> VisibleMaterialUploadAccounts { get; } = [];

    [ObservableProperty]
    private string materialUploadFilterText = string.Empty;

    [ObservableProperty]
    private string materialUploadAccountFilterText = string.Empty;

    [ObservableProperty]
    private MaterialUploadAccountItemViewModel? selectedMaterialUploadAccount;

    [ObservableProperty]
    private bool materialUploadAllowDuplicatePublish;

    [ObservableProperty]
    private bool materialUploadGenerateHighlights = true;

    [ObservableProperty]
    private int materialUploadMaxParallelAccounts = 2;

    partial void OnMaterialUploadAllowDuplicatePublishChanged(bool value) => PersistMaterialUploadPageState();
    partial void OnMaterialUploadGenerateHighlightsChanged(bool value) => PersistMaterialUploadPageState();

    partial void OnMaterialUploadMaxParallelAccountsChanged(int value)
    {
        var normalized = Math.Clamp(value <= 0 ? 2 : value, 1, 8);
        if (normalized != value)
        {
            MaterialUploadMaxParallelAccounts = normalized;
            return;
        }

        PersistMaterialUploadPageState();
        OnPropertyChanged(nameof(MaterialUploadSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
    }

    partial void OnMaterialUploadAccountFilterTextChanged(string value) => RefreshVisibleMaterialUploadAccounts();

    partial void OnSelectedMaterialUploadAccountChanged(MaterialUploadAccountItemViewModel? value)
    {
        if (!_applyingMaterialUploadAccountSelection)
        {
            PersistMaterialUploadWorkspaceAccount(value);
            RefreshMaterialUploadAccountNames();
        }

        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
    }

    partial void OnMaterialUploadFilterTextChanged(string value)
    {
        ApplyMaterialUploadFilter();
        RefreshCommandStates();
    }

    private void LoadMaterialUploadPageState()
    {
        var state = _stateService.LoadMaterialUploadPageState(RootDir);
        _applyingMaterialUploadPageState = true;
        try
        {
            MaterialUploadGenerateHighlights = state.GenerateHighlights;
            QueueStepMaterialUploadEnabled = state.MaterialUploadEnabled;
            MaterialUploadAllowDuplicatePublish = state.AllowDuplicatePublish;
            MaterialUploadMaxParallelAccounts = state.MaxParallelAccounts;
        }
        finally
        {
            _applyingMaterialUploadPageState = false;
        }
    }

    private void PersistMaterialUploadPageState()
    {
        if (_applyingMaterialUploadPageState || string.IsNullOrWhiteSpace(RootDir))
        {
            return;
        }

        _stateService.SaveMaterialUploadPageState(
            RootDir,
            new DesktopStateService.MaterialUploadPageState(
                MaterialUploadGenerateHighlights,
                QueueStepMaterialUploadEnabled,
                MaterialUploadAllowDuplicatePublish,
                MaterialUploadMaxParallelAccounts));
    }

    public string MaterialUploadQueueButtonText
    {
        get
        {
            var selectedCount = MaterialUploadProjects.Count(item => item.IsMaterialUploadChecked);
            return selectedCount > 0
                ? $"▶ 上传素材队列 ({selectedCount})"
                : "▶ 上传素材队列";
        }
    }

    public string MaterialUploadSummary =>
        $"项目数: {MaterialUploadProjects.Count} | 已勾选: {MaterialUploadProjects.Count(item => item.IsMaterialUploadChecked)} | 当前项目: {SelectedProject?.DisplayName ?? "未选择"}";

    public string MaterialPublishPlanStatus
    {
        get
        {
            var selectedCount = MaterialUploadProjects.Count(item => item.IsMaterialUploadChecked);
            var plan = SelectedProject?.MaterialUploadStrategySummary;
            if (string.IsNullOrWhiteSpace(plan) || string.Equals(plan, "未配置", StringComparison.Ordinal))
            {
                plan = "待生成";
            }

            var publishDir = ResolveMaterialPublishDisplayPath(SelectedProject);
            return $"计划：{plan} | {selectedCount} 个素材 | {publishDir}";
        }
    }

    public string CurrentMaterialUploadAccountSummary =>
        BuildMaterialUploadAccountSummary();

    private string BuildMaterialUploadAccountSummary()
    {
        var current = SelectedMaterialUploadAccount?.DisplayName ?? "未选择";
        var workspaceId = ResolveWorkspaceMaterialUploadAccountProfileId();
        var workspaceAccount = FindMaterialUploadAccount(workspaceId);
        var workspace = workspaceAccount?.DisplayName
                        ?? (!string.IsNullOrWhiteSpace(workspaceId) ? workspaceId : "跟随当前账号");

        var projectId = SelectedProject?.MaterialUploadAccountProfileId;
        var projectAccount = FindMaterialUploadAccount(projectId);
        var project = projectAccount?.DisplayName
                      ?? (!string.IsNullOrWhiteSpace(projectId) ? projectId : "跟随工作区账号");

        var effective = ResolveEffectiveMaterialUploadAccount(SelectedProject)?.DisplayName ?? "未选择";

        return $"素材账号：当前={current} | 工作区={workspace} | 项目={project} | 生效={effective}";
    }

    private MaterialUploadAccountItemViewModel? ResolveEffectiveMaterialUploadAccount(ProjectListItemViewModel? project)
    {
        var projectProfileId = project?.MaterialUploadAccountProfileId;
        if (project is not null && string.IsNullOrWhiteSpace(projectProfileId))
        {
            projectProfileId = ResolveProjectMaterialUploadAccountProfileId(project);
        }

        var projectAccount = FindMaterialUploadAccount(projectProfileId);
        if (projectAccount is not null)
        {
            return projectAccount;
        }

        return ResolveMaterialUploadWorkspaceAccount();
    }

    private MaterialUploadAccountItemViewModel? ResolveMaterialUploadWorkspaceAccount()
    {
        return FindMaterialUploadAccount(ResolveWorkspaceMaterialUploadAccountProfileId())
               ?? SelectedMaterialUploadAccount
               ?? GetActiveMaterialUploadAccount()
               ?? MaterialUploadAccounts.FirstOrDefault();
    }

    private string ResolveMaterialPublishDisplayPath(ProjectListItemViewModel? project)
    {
        var baseDir = project?.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = project?.SourceProjectDir;
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = RootDir;
        }

        return string.IsNullOrWhiteSpace(baseDir)
            ? "-"
            : Path.Combine(baseDir, "素材发布");
    }

    public void LoadMaterialUploadAccounts()
    {
        MaterialUploadAccounts.Clear();
        VisibleMaterialUploadAccounts.Clear();

        var state = _stateService.LoadMaterialUploadAccountsState();
        var profiles = state.Profiles ?? [];
        if (profiles.Length == 0)
        {
            profiles =
            [
                DesktopStateService.CreateMaterialUploadAccount("账号1", [])
            ];
        }

        var activeId = profiles.Any(item => string.Equals(item.Id, state.ActiveAccountProfileId, StringComparison.OrdinalIgnoreCase))
            ? state.ActiveAccountProfileId
            : profiles[0].Id;
        foreach (var profile in profiles)
        {
            MaterialUploadAccounts.Add(new MaterialUploadAccountItemViewModel(
                profile.Id,
                profile.Name,
                profile.AuthFile,
                profile.BrowserProfileDir,
                string.Equals(profile.Id, activeId, StringComparison.OrdinalIgnoreCase)));
        }

        _applyingMaterialUploadAccountSelection = true;
        try
        {
            SelectedMaterialUploadAccount = MaterialUploadAccounts.FirstOrDefault(item => item.IsActive)
                                            ?? MaterialUploadAccounts.FirstOrDefault();
            RefreshVisibleMaterialUploadAccounts();
        }
        finally
        {
            _applyingMaterialUploadAccountSelection = false;
        }

        ApplyMaterialUploadWorkspaceAccountSelection();
        SaveMaterialUploadAccounts();
        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
        OnPropertyChanged(nameof(MaterialUploadSummary));
    }

    public void RefreshVisibleMaterialUploadAccounts()
    {
        var selectedId = SelectedMaterialUploadAccount?.Id;
        var filter = (MaterialUploadAccountFilterText ?? string.Empty).Trim();
        var accounts = string.IsNullOrWhiteSpace(filter)
            ? MaterialUploadAccounts
            : MaterialUploadAccounts.Where(account =>
                Contains(account.DisplayName, filter) ||
                Contains(account.Id, filter) ||
                Contains(account.AuthFile, filter));

        VisibleMaterialUploadAccounts.Clear();
        foreach (var account in accounts)
        {
            account.RefreshFileState();
            VisibleMaterialUploadAccounts.Add(account);
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _applyingMaterialUploadAccountSelection = true;
            try
            {
                SelectedMaterialUploadAccount = VisibleMaterialUploadAccounts.FirstOrDefault(item =>
                    string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    ?? SelectedMaterialUploadAccount;
            }
            finally
            {
                _applyingMaterialUploadAccountSelection = false;
            }
        }
    }

    public void AddMaterialUploadAccount(string? name = null)
    {
        var accountName = string.IsNullOrWhiteSpace(name)
            ? $"账号{MaterialUploadAccounts.Count + 1}"
            : name.Trim();
        var state = DesktopStateService.CreateMaterialUploadAccount(
            accountName,
            MaterialUploadAccounts.Select(item => item.Id));
        var account = new MaterialUploadAccountItemViewModel(
            state.Id,
            state.Name,
            state.AuthFile,
            state.BrowserProfileDir);
        MaterialUploadAccounts.Add(account);
        SelectedMaterialUploadAccount = account;
        SetMaterialUploadAccountActive(account);
        RefreshVisibleMaterialUploadAccounts();
        StatusMessage = $"已新增素材上传账号：{account.DisplayName}";
    }

    public void RenameSelectedMaterialUploadAccount(string name)
    {
        var account = SelectedMaterialUploadAccount;
        if (account is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        account.Name = name.Trim();
        SaveMaterialUploadAccounts();
        RefreshVisibleMaterialUploadAccounts();
        RefreshMaterialUploadAccountNames();
        StatusMessage = $"已重命名素材上传账号：{account.DisplayName}";
    }

    public void DeleteSelectedMaterialUploadAccount()
    {
        var account = SelectedMaterialUploadAccount;
        if (account is null)
        {
            return;
        }

        if (MaterialUploadAccounts.Count <= 1)
        {
            StatusMessage = "至少需要保留一个素材上传账号。";
            AppendLog(StatusMessage);
            return;
        }

        MaterialUploadAccounts.Remove(account);
        if (account.IsActive)
        {
            SetMaterialUploadAccountActive(MaterialUploadAccounts.FirstOrDefault());
        }

        SelectedMaterialUploadAccount = MaterialUploadAccounts.FirstOrDefault(item => item.IsActive)
                                        ?? MaterialUploadAccounts.FirstOrDefault();
        SaveMaterialUploadAccounts();
        RefreshVisibleMaterialUploadAccounts();
        RefreshMaterialUploadAccountNames();
        StatusMessage = $"已删除素材上传账号：{account.DisplayName}";
    }

    public void SetSelectedMaterialUploadAccountActive()
    {
        SetMaterialUploadAccountActive(SelectedMaterialUploadAccount);
    }

    public void SetSelectedMaterialUploadAccountAuthFile(string path)
    {
        if (SelectedMaterialUploadAccount is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SelectedMaterialUploadAccount.AuthFile = path.Trim();
        SaveMaterialUploadAccounts();
        RefreshVisibleMaterialUploadAccounts();
    }

    public void SaveSelectedMaterialUploadAccountConfig()
    {
        SaveMaterialUploadAccounts();
        SelectedMaterialUploadAccount?.RefreshFileState();
        RefreshVisibleMaterialUploadAccounts();
        RefreshMaterialUploadAccountNames();
        StatusMessage = SelectedMaterialUploadAccount is null
            ? "未选择素材上传账号。"
            : $"已保存账号配置：{SelectedMaterialUploadAccount.DisplayName}";
    }

    public void BindCheckedMaterialUploadProjectsToSelectedAccount()
    {
        var account = SelectedMaterialUploadAccount ?? GetActiveMaterialUploadAccount();
        if (account is null)
        {
            StatusMessage = "请先选择素材上传账号。";
            AppendLog(StatusMessage);
            return;
        }

        var targets = MaterialUploadProjects.Where(item => item.IsMaterialUploadChecked).ToArray();
        if (targets.Length == 0 && SelectedProject is not null)
        {
            targets = [SelectedProject];
        }

        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要绑定账号的素材项目。";
            AppendLog(StatusMessage);
            return;
        }

        foreach (var project in targets)
        {
            BindMaterialUploadProjectToAccount(project, account);
        }

        OnPropertyChanged(nameof(MaterialUploadSummary));
        StatusMessage = $"已绑定 {targets.Length} 个素材项目到账号：{account.DisplayName}";
        AppendLog(StatusMessage);
    }

    public void BindCurrentMaterialUploadProjectToSelectedAccount()
    {
        var target = ResolveMaterialUploadTargetProject(null);
        if (target is null)
        {
            StatusMessage = "请先选择、勾选或扫描出一个素材项目。";
            AppendLog(StatusMessage, string.Empty, string.Empty, "material-upload", "素材上传", isFailure: true);
            return;
        }

        var account = SelectedMaterialUploadAccount ?? GetActiveMaterialUploadAccount();
        if (account is null)
        {
            StatusMessage = "请先选择素材上传账号。";
            AppendLog(StatusMessage, target.ProjectKey, target.DisplayName, "material-upload", "素材上传", isFailure: true);
            return;
        }

        BindMaterialUploadProjectToAccount(target, account);
        ActivateMaterialUploadProject(target);
        OnPropertyChanged(nameof(MaterialUploadSummary));
        StatusMessage = $"已绑定当前素材项目到账号：{target.DisplayName} -> {account.DisplayName}";
        AppendLog(StatusMessage, target.ProjectKey, target.DisplayName, "material-upload", "素材上传");
    }

    public void ClearMaterialUploadProjectAccountBinding()
    {
        var targets = MaterialUploadProjects.Where(item => item.IsMaterialUploadChecked).ToArray();
        if (targets.Length == 0)
        {
            var target = ResolveMaterialUploadTargetProject(null);
            targets = target is null ? [] : [target];
        }

        if (targets.Length == 0)
        {
            StatusMessage = "请先选择或勾选要清除绑定的素材项目。";
            AppendLog(StatusMessage, string.Empty, string.Empty, "material-upload", "素材上传", isFailure: true);
            return;
        }

        foreach (var project in targets)
        {
            ClearMaterialUploadProjectAccountBinding(project);
        }

        OnPropertyChanged(nameof(MaterialUploadSummary));
        StatusMessage = $"已清除 {targets.Length} 个素材项目的账号绑定。";
        AppendLog(StatusMessage, string.Empty, string.Empty, "material-upload", "素材上传");
    }

    public Task OpenSelectedMaterialUploadAccountBrowserAsync(bool relogin)
    {
        var account = ResolveMaterialUploadWorkspaceAccount();
        if (account is null)
        {
            StatusMessage = "请先选择素材上传账号。";
            AppendLog(StatusMessage);
            return Task.CompletedTask;
        }

        if (IsWeixinBrowserSessionRunning)
        {
            StatusMessage = "微信浏览器已在运行，请先关闭当前浏览器窗口。";
            AppendLog(StatusMessage);
            return Task.CompletedTask;
        }

        if (relogin)
        {
            TryDeleteFile(account.AuthFile);
            account.RefreshFileState();
        }

        var configPath = EnsureMaterialUploadAccountBrowserConfig(account);
        var projectDir = Directory.Exists(RootDir) ? RootDir : Path.GetDirectoryName(configPath)!;
        IsWeixinBrowserSessionRunning = true;
        StatusMessage = $"正在打开素材上传账号浏览器：{account.DisplayName}";
        AppendLog(StatusMessage, string.Empty, string.Empty, "weixin-browser", "打开浏览器");

        _ = Task.Run(async () =>
        {
            try
            {
                await _weixinBrowserSessionLauncher.OpenHomeAsync(configPath, projectDir, CancellationToken.None);
                Dispatcher.UIThread.Post(() =>
                {
                    account.RefreshFileState();
                    RefreshVisibleMaterialUploadAccounts();
                    AppendLog(
                        $"素材上传账号浏览器会话已结束：{account.DisplayName}",
                        string.Empty,
                        string.Empty,
                        "weixin-browser",
                        "打开浏览器");
                    IsWeixinBrowserSessionRunning = false;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AppendLog(
                        $"打开素材上传账号浏览器失败：{ex.Message}",
                        string.Empty,
                        string.Empty,
                        "weixin-browser",
                        "打开浏览器",
                        isFailure: true);
                    StatusMessage = ex.Message;
                    IsWeixinBrowserSessionRunning = false;
                });
            }
        });

        return Task.CompletedTask;
    }

    public async Task RunMaterialDirectoryBatchPublishAsync(
        string workspacePath,
        bool hideLocation,
        bool declareOriginal,
        bool aiRewriteDescription)
    {
        if (IsBusy)
        {
            StatusMessage = "当前已有任务正在运行，请等待结束后再启动目录批量发表。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        var account = ResolveMaterialUploadWorkspaceAccount();
        if (account is null)
        {
            StatusMessage = "请先选择素材上传账号。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            StatusMessage = "目录批量发表：请选择一个存在的工作目录。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        var displayName = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ActivityTitle = $"素材上传日志 · 目录批量发表 · {displayName}";
        SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
            ?? SelectedStepLogFilter;

        await RunBusyAsync($"正在目录批量发表：{workspacePath}", async cancellationToken =>
        {
            var progress = new Progress<string>(message =>
            {
                AppendExternalLog(
                    message,
                    projectLabel: displayName,
                    stepKey: "weixin-material-upload",
                    stepLabel: "素材上传");
                StatusMessage = message;
            });

            var result = await _materialDirectoryPublishService.PublishAsync(
                new MaterialDirectoryPublishOptions(
                    WorkspacePath: workspacePath,
                    AuthFilePath: ExpandMaterialUploadPath(account.AuthFile),
                    BrowserProfileDir: ExpandMaterialUploadPath(account.BrowserProfileDir),
                    AccountId: account.Id,
                    AccountDisplayName: account.DisplayName,
                    HideLocation: hideLocation,
                    DeclareOriginal: declareOriginal,
                    AiRewriteDescription: aiRewriteDescription,
                    AllowDuplicatePublish: MaterialUploadAllowDuplicatePublish),
                progress,
                cancellationToken);

            var message = $"目录批量发表流程结束：共 {result.Total} 条。输出目录 {result.OutputDirectory}";
            StatusMessage = message;
            AppendExternalLog(
                message,
                projectLabel: displayName,
                stepKey: "weixin-material-upload",
                stepLabel: "素材上传");
            account.RefreshFileState();
            RefreshVisibleMaterialUploadAccounts();
            await RefreshProjectListAsync();
        });
    }

    public async Task RunMaterialSystemHighlightBatchPublishAsync(MaterialSystemHighlightBatchPublishDialogResult dialog)
    {
        await RunMaterialSystemHighlightBatchPublishAsync(
            dialog,
            ResolveMaterialUploadWorkspaceAccount(),
            RootDir,
            scheduleRule: null);
    }

    private async Task RunMaterialSystemHighlightBatchPublishAsync(
        MaterialSystemHighlightBatchPublishDialogResult dialog,
        MaterialUploadAccountItemViewModel? account,
        string workspacePath,
        MaterialSystemHighlightScheduleRule? scheduleRule)
    {
        if (IsBusy)
        {
            StatusMessage = "当前已有任务正在运行，请等待结束后再启动系统高光发布。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        if (account is null)
        {
            StatusMessage = "请先选择素材上传账号。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            StatusMessage = "系统高光发布：请选择一个存在的工作目录。";
            AppendExternalLog(StatusMessage, stepKey: "weixin-material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        ActivityTitle = "素材上传日志 · 系统高光发布";
        SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
            ?? SelectedStepLogFilter;

        await RunBusyAsync("正在发布系统高光视频...", async cancellationToken =>
        {
            var progress = new Progress<string>(message =>
            {
                AppendExternalLog(
                    message,
                    stepKey: "weixin-material-upload",
                    stepLabel: "素材上传");
                StatusMessage = message;
            });

            var result = await _materialSystemHighlightBatchPublishService.PublishAsync(
                new MaterialSystemHighlightBatchPublishOptions(
                    WorkspacePath: workspacePath,
                    TitlesText: dialog.TitlesText,
                    DefaultDescription: dialog.DefaultDescription,
                    PublishCount: dialog.PublishCount,
                    PublishTargetMode: dialog.PublishTargetMode,
                    PublishVideoTypes: dialog.PublishVideoTypes,
                    RegenerateAfterPublish: dialog.RegenerateAfterPublish,
                    RegenerateVideoTypes: dialog.RegenerateVideoTypes,
                    AuthFilePath: ExpandMaterialUploadPath(account.AuthFile),
                    BrowserProfileDir: ExpandMaterialUploadPath(account.BrowserProfileDir),
                    AccountId: account.Id,
                    AccountDisplayName: account.DisplayName,
                    AllowDuplicatePublish: MaterialUploadAllowDuplicatePublish),
                progress,
                cancellationToken);

            var message = $"系统高光发布流程结束：成功 {result.Succeeded} 个，失败 {result.Failed} 个。";
            StatusMessage = message;
            AppendExternalLog(message, stepKey: "weixin-material-upload", stepLabel: "素材上传");
            if (scheduleRule is not null)
            {
                _materialSystemHighlightScheduleService.UpdateState(scheduleRule, message, DateTimeOffset.Now);
            }

            await RefreshProjectListAsync();
        });
    }

    public async Task HandleMaterialSystemHighlightScheduleDialogResultAsync(MaterialSystemHighlightScheduleDialogResult result)
    {
        var message = $"已保存系统高光定时配置：{result.Rules.Count} 条规则。";
        StatusMessage = message;
        AppendExternalLog(message, stepKey: "weixin-material-upload", stepLabel: "素材上传");

        StartMaterialSystemHighlightScheduler();
        if (!string.IsNullOrWhiteSpace(result.RunNowRuleId))
        {
            var rule = result.Rules.FirstOrDefault(item =>
                string.Equals(item.Id, result.RunNowRuleId, StringComparison.OrdinalIgnoreCase));
            if (rule is not null)
            {
                await RunMaterialSystemHighlightScheduleRuleAsync(rule, reason: "立即执行");
            }
        }
    }

    private void StartMaterialSystemHighlightScheduler()
    {
        if (_materialSystemHighlightScheduleTimer is null)
        {
            _materialSystemHighlightScheduleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _materialSystemHighlightScheduleTimer.Tick += async (_, _) =>
                await CheckMaterialSystemHighlightScheduleAsync(startup: false);
        }

        if (!_materialSystemHighlightScheduleTimer.IsEnabled)
        {
            _materialSystemHighlightScheduleTimer.Start();
        }

        _ = CheckMaterialSystemHighlightScheduleAsync(startup: true);
    }

    private async Task CheckMaterialSystemHighlightScheduleAsync(bool startup)
    {
        if (_checkingMaterialSystemHighlightSchedule || IsBusy)
        {
            return;
        }

        _checkingMaterialSystemHighlightSchedule = true;
        try
        {
            var now = DateTimeOffset.Now;
            var stateMap = _materialSystemHighlightScheduleService.LoadStateMap();
            foreach (var rule in _materialSystemHighlightScheduleService.LoadRules().Where(item => item.Enabled))
            {
                if (rule.OnlyWhenIdle && IsBusy)
                {
                    continue;
                }

                stateMap.TryGetValue(rule.Id, out var state);
                if (!IsMaterialSystemHighlightScheduleDue(rule, state, now, startup))
                {
                    continue;
                }

                await RunMaterialSystemHighlightScheduleRuleAsync(rule, reason: startup ? "启动补跑" : "定时触发");
                break;
            }
        }
        catch (Exception ex)
        {
            AppendExternalLog(
                $"系统高光定时检查失败：{ex.Message}",
                stepKey: "weixin-material-upload",
                stepLabel: "素材上传",
                isFailure: true);
        }
        finally
        {
            _checkingMaterialSystemHighlightSchedule = false;
        }
    }

    private static bool IsMaterialSystemHighlightScheduleDue(
        MaterialSystemHighlightScheduleRule rule,
        MaterialSystemHighlightScheduleState? state,
        DateTimeOffset now,
        bool startup)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        if (rule.TriggerMode == "interval")
        {
            if (DateTimeOffset.TryParse(state?.LastRunAt, out var lastRunAt) &&
                now - lastRunAt < TimeSpan.FromMinutes(Math.Max(1, rule.IntervalMinutes)))
            {
                return false;
            }

            return !startup || rule.CatchUpOnStartup || string.IsNullOrWhiteSpace(state?.LastRunAt);
        }

        var parts = (rule.Time ?? "09:00").Split(':');
        var hour = parts.Length == 2 && int.TryParse(parts[0], out var parsedHour) ? parsedHour : 9;
        var minute = parts.Length == 2 && int.TryParse(parts[1], out var parsedMinute) ? parsedMinute : 0;
        var scheduled = new DateTimeOffset(now.Date.AddHours(hour).AddMinutes(minute), now.Offset);
        if (now < scheduled)
        {
            return false;
        }

        if (rule.ScheduleMode == "weekly")
        {
            var weekday = ((int)now.DayOfWeek + 6) % 7 + 1;
            var weekdays = (rule.Weekdays ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => int.TryParse(item, out _))
                .Select(int.Parse)
                .ToHashSet();
            if (weekdays.Count > 0 && !weekdays.Contains(weekday))
            {
                return false;
            }
        }

        var runKey = rule.TriggerMode == "interval" ? $"{now:yyyyMMddHHmm}" : $"{now:yyyyMMdd}-{rule.Time}";
        return !string.Equals(state?.LastRunKey, runKey, StringComparison.OrdinalIgnoreCase) &&
               (!startup || rule.CatchUpOnStartup);
    }

    private async Task RunMaterialSystemHighlightScheduleRuleAsync(
        MaterialSystemHighlightScheduleRule rule,
        string reason)
    {
        rule = _materialSystemHighlightScheduleService.NormalizeRule(rule);
        var workspacePath = !string.IsNullOrWhiteSpace(rule.WorkspacePath) && Directory.Exists(rule.WorkspacePath)
            ? rule.WorkspacePath
            : RootDir;
        var account = FindMaterialUploadAccount(rule.ProfileId)
                      ?? SelectedMaterialUploadAccount
                      ?? GetActiveMaterialUploadAccount();
        var titles = rule.Dramas
            .Where(item => item.Enabled)
            .Select(item => item.Title)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (titles.Length == 0)
        {
            AppendExternalLog(
                $"系统高光定时{reason}：规则“{rule.Name}”没有可发布剧名。",
                stepKey: "weixin-material-upload",
                stepLabel: "素材上传",
                isFailure: true);
            return;
        }

        AppendExternalLog(
            $"系统高光定时{reason}：{rule.Name}，共 {titles.Length} 部。",
            stepKey: "weixin-material-upload",
            stepLabel: "素材上传");

        await RunMaterialSystemHighlightBatchPublishAsync(
            new MaterialSystemHighlightBatchPublishDialogResult(
                TitlesText: string.Join(Environment.NewLine, titles),
                DefaultDescription: rule.DefaultDescription,
                PublishCount: Math.Max(1, rule.PublishCount),
                PublishTargetMode: rule.PublishTargetMode,
                PublishVideoTypes: rule.PublishVideoTypes,
                RegenerateAfterPublish: rule.RegenerateAfterPublish,
                RegenerateVideoTypes: rule.RegenerateVideoTypes),
            account,
            workspacePath,
            rule);
    }

    public void ApplyMaterialUploadFilter()
    {
        var selectedProjectKey = SelectedProject?.ProjectKey;
        var filter = (MaterialUploadFilterText ?? string.Empty).Trim();
        var matches = string.IsNullOrWhiteSpace(filter)
            ? Projects
            : Projects.Where(project => MatchesMaterialUploadFilter(project, filter));

        MaterialUploadProjects.Clear();
        foreach (var project in matches)
        {
            MaterialUploadProjects.Add(project);
        }

        if (selectedProjectKey is not null &&
            MaterialUploadProjects.All(item => !string.Equals(item.ProjectKey, selectedProjectKey, StringComparison.Ordinal)))
        {
            SelectedProject = MaterialUploadProjects.FirstOrDefault();
        }

        OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
        OnPropertyChanged(nameof(MaterialUploadSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
    }

    public void SetAllMaterialUploadProjectsChecked(bool isChecked)
    {
        foreach (var project in MaterialUploadProjects)
        {
            project.IsMaterialUploadChecked = isChecked;
        }

        OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
        OnPropertyChanged(nameof(MaterialUploadSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
    }

    public void ResetMaterialUploadWorkspace()
    {
        RootDir = string.Empty;
        Projects.Clear();
        FilteredProjects.Clear();
        MaterialUploadProjects.Clear();
        SelectedProject = null;
        TotalProjects = 0;
        PendingProjects = 0;
        StatusMessage = "已重置发布素材工作目录。";
        PersistState();
        OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
        OnPropertyChanged(nameof(MaterialUploadSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
        RefreshCommandStates();
    }

    public void ActivateMaterialUploadProject(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            TaskQueueDetailMode = TaskQueueDetailMaterialUpload;
            SelectedProject = project;
            SyncProjectLogFilterToSelection();
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"素材上传日志 · {project.DisplayName}";
            RefreshCommandStates();
            OnPropertyChanged(nameof(MaterialUploadSummary));
            OnPropertyChanged(nameof(MaterialPublishPlanStatus));
            OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
        }
        catch (Exception ex)
        {
            StatusMessage = $"切换素材上传项目失败：{ex.Message}";
        }
    }

    public async Task RunCheckedMaterialUploadQueueFromPageAsync()
    {
        var targets = MaterialUploadProjects.Where(item => item.IsMaterialUploadChecked).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要上传素材的项目。";
            AppendLog(StatusMessage);
            return;
        }

        if (!MaterialUploadGenerateHighlights && !QueueStepMaterialUploadEnabled)
        {
            StatusMessage = "请至少启用一个步骤：生成素材高光或素材上传。";
            AppendLog(StatusMessage);
            return;
        }

        ActivityTitle = "素材上传日志";
        await RunBusyAsync($"正在执行素材上传队列，共 {targets.Length} 个项目...", async cancellationToken =>
        {
            foreach (var target in targets)
            {
                target.MarkQueued();
                ClearLogsForProject(target.ProjectKey);
            }

            if (MaterialUploadGenerateHighlights)
            {
                await GenerateMaterialHighlightsForProjectsAsync(targets, cancellationToken);
            }

            if (!QueueStepMaterialUploadEnabled)
            {
                foreach (var target in targets)
                {
                    target.MarkCompleted();
                }

                await RefreshProjectListAsync();
                StatusMessage = $"素材高光生成完成，共处理 {targets.Length} 个项目。";
                AppendLog(StatusMessage);
                return;
            }

            var executionTargets = ResolveMaterialUploadExecutionTargets(targets, bindMissing: true);
            if (executionTargets.Length == 0)
            {
                StatusMessage = "未找到可用的素材上传账号。";
                AppendLog(StatusMessage, string.Empty, string.Empty, "material-upload", "素材上传", isFailure: true);
                return;
            }

            await PrepareMaterialUploadOverridesAsync(executionTargets, refreshAfter: false);
            await ExecuteMaterialUploadBatchByAccountAsync(executionTargets, cancellationToken);

            await RefreshProjectListAsync();
            StatusMessage = $"素材上传完成，共处理 {targets.Length} 个项目。";
            AppendLog(StatusMessage);
            await TryNotifyFeishuQueueSummaryAsync(targets, "素材上传队列", cancellationToken);
        });
        OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
        OnPropertyChanged(nameof(MaterialUploadSummary));
    }

    public async Task RunMaterialUploadProjectFromPageAsync(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        ActivateMaterialUploadProject(project);
        if (!MaterialUploadGenerateHighlights && !QueueStepMaterialUploadEnabled)
        {
            StatusMessage = "请至少启用一个步骤：生成素材高光或素材上传。";
            AppendLog(StatusMessage, project.ProjectKey, project.DisplayName, "weixin-material-upload", "素材上传", isFailure: true);
            return;
        }

        ClearLogsForProject(project.ProjectKey);
        ActivityTitle = $"素材上传日志 · {project.DisplayName}";
        await RunBusyAsync($"正在处理素材上传：{project.DisplayName}", async cancellationToken =>
        {
            project.MarkRunning(MaterialUploadGenerateHighlights && !QueueStepMaterialUploadEnabled ? "生成素材高光" : "素材上传");

            if (MaterialUploadGenerateHighlights)
            {
                await GenerateMaterialHighlightsForProjectsAsync([project], cancellationToken);
            }

            if (!QueueStepMaterialUploadEnabled)
            {
                project.MarkCompleted();
                await RefreshAfterExecutionAsync(project.ProjectKey);
                StatusMessage = $"素材高光生成完成：{project.DisplayName}";
                AppendLog(StatusMessage, project.ProjectKey, project.DisplayName, "weixin-material-upload", "素材上传");
                return;
            }

            var executionTargets = ResolveMaterialUploadExecutionTargets([project], bindMissing: true);
            if (executionTargets.Length == 0)
            {
                StatusMessage = "未找到可用的素材上传账号。";
                AppendLog(StatusMessage, project.ProjectKey, project.DisplayName, "material-upload", "素材上传", isFailure: true);
                return;
            }

            await PrepareMaterialUploadOverridesAsync(executionTargets, refreshAfter: false);
            await ExecuteProjectBatchItemAsync(
                project,
                "weixin-material-upload",
                "微信上传素材",
                1,
                1,
                cancellationToken,
                clearLogs: false);
            await RefreshAfterExecutionAsync(project.ProjectKey);
        });
    }

    public void OpenMaterialPublishConfig(ProjectListItemViewModel? project)
    {
        var target = ResolveMaterialPublishConfigTarget(project);
        if (target is null)
        {
            return;
        }

        _shellService.TryRevealPath(target.ConfigPath, out _);
    }

    public MaterialPublishConfigTarget? ResolveMaterialPublishConfigTarget(ProjectListItemViewModel? project)
    {
        var targetProject = ResolveMaterialPublishConfigProject(project);
        if (targetProject is null)
        {
            StatusMessage = "请先选择或勾选已有 workflow 目录的素材项目。";
            return null;
        }

        try
        {
            var configPath = EnsureMaterialPublishConfigPath(targetProject);
            return new MaterialPublishConfigTarget(targetProject, configPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开素材发表配置失败：{ex.Message}";
            return null;
        }
    }

    public void RefreshMaterialPublishConfigAfterSave(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            project.RefreshMaterialPublishSummary();
            OnPropertyChanged(nameof(MaterialUploadSummary));
            OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
            RefreshCommandStates();
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新素材发表配置失败：{ex.Message}";
        }
    }

    private ProjectListItemViewModel? ResolveMaterialPublishConfigProject(ProjectListItemViewModel? project)
    {
        if (project is not null)
        {
            return HasMaterialPublishWorkflow(project) ? project : null;
        }

        if (SelectedProject is not null &&
            MaterialUploadProjects.Contains(SelectedProject) &&
            HasMaterialPublishWorkflow(SelectedProject))
        {
            return SelectedProject;
        }

        return MaterialUploadProjects.FirstOrDefault(item => item.IsMaterialUploadChecked && HasMaterialPublishWorkflow(item))
               ?? MaterialUploadProjects.FirstOrDefault(HasMaterialPublishWorkflow);
    }

    private static bool HasMaterialPublishWorkflow(ProjectListItemViewModel project) =>
        !string.IsNullOrWhiteSpace(project.WorkflowProjectDir);

    private ProjectListItemViewModel? ResolveMaterialUploadTargetProject(ProjectListItemViewModel? project)
    {
        if (project is not null)
        {
            return project;
        }

        if (SelectedProject is not null && MaterialUploadProjects.Contains(SelectedProject))
        {
            return SelectedProject;
        }

        return MaterialUploadProjects.FirstOrDefault(item => item.IsMaterialUploadChecked)
               ?? MaterialUploadProjects.FirstOrDefault();
    }

    private string EnsureMaterialPublishConfigPath(ProjectListItemViewModel project)
    {
        var existing = ResolveMaterialPublishConfigPath(project);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(project.WorkflowProjectDir))
        {
            throw new InvalidOperationException($"项目没有 workflow 目录：{project.DisplayName}");
        }

        Directory.CreateDirectory(project.WorkflowProjectDir);
        var configPath = Path.Combine(project.WorkflowProjectDir, "weixin-channel-publish.json");
        File.WriteAllText(configPath, BuildDefaultMaterialPublishConfigJson(project));
        StatusMessage = $"已创建素材发表配置：{project.DisplayName}";
        AppendLog(StatusMessage, project.ProjectKey, project.DisplayName, "material-upload", "素材上传");
        return configPath;
    }

    public void ShowMaterialUploadLogs(ProjectListItemViewModel? project)
    {
        ShowMaterialRunLogTab(project ?? SelectedProject);
    }

    private async Task GenerateMaterialHighlightsForProjectsAsync(
        IReadOnlyList<ProjectListItemViewModel> projects,
        CancellationToken cancellationToken)
    {
        var clipSourceProjects = 0;
        var generatedCount = 0;
        var existingCount = 0;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = new Progress<string>(message =>
            {
                AppendExternalLog(
                    message,
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-material-upload",
                    "素材上传");
                StatusMessage = message;
            });

            var result = await _materialHighlightGenerationService.GenerateAsync(
                new MaterialHighlightProjectRequest(
                    project.ProjectKey,
                    project.DisplayName,
                    project.SourceProjectDir,
                    project.WorkflowProjectDir,
                    ResolveMaterialPublishConfigPath(project)),
                progress,
                cancellationToken);

            if (!result.UsesMaterialClipSource)
            {
                continue;
            }

            clipSourceProjects++;
            generatedCount += result.GeneratedClipCount;
            existingCount += result.ExistingClipCount;
        }

        var summary = clipSourceProjects == 0
            ? "素材高光：当前所选项目未启用 material_clips，已跳过预处理。"
            : $"素材高光预处理完成：{clipSourceProjects} 个项目，新增 {generatedCount} 条，复用 {existingCount} 条。";
        AppendLog(summary);
        StatusMessage = summary;
    }

    private async Task ExecuteMaterialUploadBatchByAccountAsync(
        IReadOnlyList<MaterialUploadExecutionTarget> targets,
        CancellationToken cancellationToken)
    {
        var total = targets.Count;
        var groups = targets
            .GroupBy(item => item.Account.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Index).ToArray())
            .ToArray();

        if (groups.Length <= 1 || MaterialUploadMaxParallelAccounts <= 1)
        {
            foreach (var target in targets.OrderBy(item => item.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteMaterialUploadTargetAsync(target, total, cancellationToken);
            }

            return;
        }

        using var gate = new SemaphoreSlim(Math.Clamp(MaterialUploadMaxParallelAccounts, 1, 8));
        var tasks = groups.Select(group => RunMaterialUploadAccountGroupAsync(group, total, gate, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunMaterialUploadAccountGroupAsync(
        IReadOnlyList<MaterialUploadExecutionTarget> targets,
        int total,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteMaterialUploadTargetAsync(target, total, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteMaterialUploadTargetAsync(
        MaterialUploadExecutionTarget target,
        int total,
        CancellationToken cancellationToken)
    {
        await ExecuteProjectBatchItemAsync(
            target.Project,
            "weixin-material-upload",
            "微信上传素材",
            target.Index,
            total,
            cancellationToken,
            clearLogs: false);
    }

    private async Task ExecuteMaterialUploadBatchSerialAsync(
        ProjectListItemViewModel[] targets,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < targets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteProjectBatchItemAsync(
                targets[index],
                "weixin-material-upload",
                "微信上传素材",
                index + 1,
                targets.Length,
                cancellationToken,
                clearLogs: false);
        }
    }

    private async Task ExecuteMaterialUploadBatchConcurrentAsync(
        ProjectListItemViewModel[] targets,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(2);
        var tasks = targets.Select((project, index) => RunMaterialUploadBatchConcurrentItemAsync(
            project,
            index + 1,
            targets.Length,
            gate,
            cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunMaterialUploadBatchConcurrentItemAsync(
        ProjectListItemViewModel project,
        int index,
        int total,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ExecuteProjectBatchItemAsync(
                project,
                "weixin-material-upload",
                "微信上传素材",
                index,
                total,
                cancellationToken,
                clearLogs: false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PrepareMaterialUploadOverridesAsync(
        IEnumerable<MaterialUploadExecutionTarget> targets,
        bool refreshAfter = true)
    {
        var refreshed = false;
        foreach (var target in targets)
        {
            if (TryApplyMaterialUploadRuntimeOverrides(target.Project, target.Account))
            {
                refreshed = true;
            }
        }

        if (refreshed && refreshAfter)
        {
            await RefreshProjectListAsync();
        }
    }

    private bool TryApplyMaterialUploadRuntimeOverrides(
        ProjectListItemViewModel project,
        MaterialUploadAccountItemViewModel? account)
    {
        var configPath = ResolveMaterialPublishConfigPath(project);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject();
            var videoPublish = root["video_publish"] as JsonObject ?? new JsonObject();
            root["video_publish"] = videoPublish;
            videoPublish["_runtime_allow_duplicate_material_publish"] = MaterialUploadAllowDuplicatePublish;
            ApplyGlobalMaterialClipPublishOptions(videoPublish);
            if (account is not null)
            {
                root["auth_file"] = account.AuthFile;
                var browser = root["browser"] as JsonObject ?? new JsonObject();
                root["browser"] = browser;
                browser["user_data_dir"] = account.BrowserProfileDir;
                videoPublish["_runtime_account_profile_id"] = account.Id;
                videoPublish["_runtime_account_profile_name"] = account.DisplayName;
                videoPublish["state_file"] = BuildAccountScopedStateFile(ReadJsonString(videoPublish, "state_file"), account.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(ExpandMaterialUploadPath(account.AuthFile)) ?? ".");
                Directory.CreateDirectory(ExpandMaterialUploadPath(account.BrowserProfileDir));
            }

            if (videoPublish["enabled"] is null)
            {
                videoPublish["enabled"] = true;
            }

            File.WriteAllText(configPath, root.ToJsonString(MaterialUploadJsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(
                $"更新素材上传配置失败：{ex.Message}",
                project.ProjectKey,
                project.DisplayName,
                "material-upload",
                "素材上传",
                isFailure: true);
            return false;
        }
    }

    public void ApplyMaterialUploadAccountContext(ProjectListItemViewModel project)
    {
        var profileId = ResolveProjectMaterialUploadAccountProfileId(project);
        var account = FindMaterialUploadAccount(profileId);
        project.MaterialUploadAccountProfileId = profileId;
        project.MaterialUploadAccountName = account?.DisplayName ?? profileId;
    }

    private MaterialUploadExecutionTarget[] ResolveMaterialUploadExecutionTargets(
        IReadOnlyList<ProjectListItemViewModel> projects,
        bool bindMissing)
    {
        var result = new List<MaterialUploadExecutionTarget>(projects.Count);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var account = ResolveMaterialUploadAccountForProject(project, bindMissing);
            if (account is null)
            {
                AppendLog(
                    $"素材上传账号未配置，已跳过：{project.DisplayName}",
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-material-upload",
                    "素材上传",
                    isFailure: true);
                continue;
            }

            result.Add(new MaterialUploadExecutionTarget(project, account, index + 1));
        }

        return result.ToArray();
    }

    private MaterialUploadAccountItemViewModel? ResolveMaterialUploadAccountForProject(
        ProjectListItemViewModel project,
        bool bindMissing)
    {
        var profileId = project.MaterialUploadAccountProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profileId = ResolveProjectMaterialUploadAccountProfileId(project);
            project.MaterialUploadAccountProfileId = profileId;
        }

        var account = FindMaterialUploadAccount(profileId);
        if (account is not null)
        {
            project.MaterialUploadAccountName = account.DisplayName;
            return account;
        }

        account = ResolveMaterialUploadWorkspaceAccount();
        if (account is not null && (bindMissing || string.IsNullOrWhiteSpace(profileId)))
        {
            BindMaterialUploadProjectToAccount(project, account);
            return account;
        }

        return account;
    }

    private void BindMaterialUploadProjectToAccount(
        ProjectListItemViewModel project,
        MaterialUploadAccountItemViewModel account)
    {
        project.MaterialUploadAccountProfileId = account.Id;
        project.MaterialUploadAccountName = account.DisplayName;
        foreach (var metadataPath in EnumerateProjectMetadataPaths(project))
        {
            try
            {
                var root = File.Exists(metadataPath)
                    ? JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                root["materialUploadAccountProfileId"] = account.Id;
                root.Remove("material_upload_account_profile_id");
                File.WriteAllText(metadataPath, root.ToJsonString(MaterialUploadJsonOptions));
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"写入素材上传账号绑定失败：{ex.Message}",
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-material-upload",
                    "素材上传",
                    isFailure: true);
            }
        }
    }

    private void ClearMaterialUploadProjectAccountBinding(ProjectListItemViewModel project)
    {
        project.MaterialUploadAccountProfileId = string.Empty;
        project.MaterialUploadAccountName = string.Empty;
        foreach (var metadataPath in EnumerateProjectMetadataPaths(project))
        {
            try
            {
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject ?? new JsonObject();
                root.Remove("materialUploadAccountProfileId");
                root.Remove("material_upload_account_profile_id");
                File.WriteAllText(metadataPath, root.ToJsonString(MaterialUploadJsonOptions));
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"清除素材上传账号绑定失败：{ex.Message}",
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-material-upload",
                    "素材上传",
                    isFailure: true);
            }
        }
    }

    private IEnumerable<string> EnumerateProjectMetadataPaths(ProjectListItemViewModel project)
    {
        var dirs = new[]
        {
            project.SourceProjectDir,
            project.WorkflowProjectDir ?? string.Empty
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            var path = Path.Combine(dir, ProjectMetadataFileName);
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private string ResolveProjectMaterialUploadAccountProfileId(ProjectListItemViewModel project)
    {
        foreach (var path in EnumerateProjectMetadataPaths(project))
        {
            var id = ReadAccountProfileId(path, MaterialUploadProjectAccountKeys);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return string.Empty;
    }

    private string ResolveWorkspaceMaterialUploadAccountProfileId()
    {
        if (string.IsNullOrWhiteSpace(RootDir))
        {
            return string.Empty;
        }

        var workspacePath = Path.Combine(RootDir, WorkspaceAccountProfileConfigName);
        return ReadAccountProfileId(workspacePath, MaterialUploadWorkspaceAccountKeys);
    }

    private void PersistMaterialUploadWorkspaceAccount(MaterialUploadAccountItemViewModel? account)
    {
        if (account is null || string.IsNullOrWhiteSpace(RootDir) || !Directory.Exists(RootDir))
        {
            return;
        }

        var workspacePath = Path.Combine(RootDir, WorkspaceAccountProfileConfigName);
        try
        {
            var root = File.Exists(workspacePath)
                ? JsonNode.Parse(File.ReadAllText(workspacePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            root["materialUploadAccountProfileId"] = account.Id;
            root.Remove("material_upload_account_profile_id");
            File.WriteAllText(workspacePath, root.ToJsonString(MaterialUploadJsonOptions));
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存工作区素材账号失败：{ex.Message}";
        }
    }

    private void ApplyMaterialUploadWorkspaceAccountSelection()
    {
        var workspaceProfileId = ResolveWorkspaceMaterialUploadAccountProfileId();
        if (string.IsNullOrWhiteSpace(workspaceProfileId))
        {
            return;
        }

        var account = FindMaterialUploadAccount(workspaceProfileId);
        if (account is null || ReferenceEquals(account, SelectedMaterialUploadAccount))
        {
            return;
        }

        _applyingMaterialUploadAccountSelection = true;
        try
        {
            SelectedMaterialUploadAccount = account;
        }
        finally
        {
            _applyingMaterialUploadAccountSelection = false;
        }

        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
        OnPropertyChanged(nameof(MaterialPublishPlanStatus));
    }

    private static string ReadAccountProfileId(string path, IEnumerable<string> keys)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
            {
                return string.Empty;
            }

            foreach (var key in keys)
            {
                var value = ReadJsonString(root, key)?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string? ReadJsonString(JsonObject root, string key)
    {
        return root[key] is JsonValue value &&
               value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static void ApplyGlobalMaterialClipPublishOptions(JsonObject videoPublish)
    {
        var clip = ClipConfig.Load();
        videoPublish["publish_originality_enabled"] = clip.OrigEnabled;
        videoPublish["publish_originality_reuse_across_runs"] = true;
        videoPublish["publish_originality_zoom"] = clip.OrigZoom;
        videoPublish["publish_originality_color"] = clip.OrigColor;
        videoPublish["publish_originality_speed"] = clip.OrigSpeed;
        videoPublish["publish_originality_fade"] = clip.OrigFade;
        videoPublish["publish_originality_sticker_dir"] = clip.OrigStickerDir ?? string.Empty;

        videoPublish["material_clip_originality_enabled"] = clip.OrigEnabled;
        videoPublish["material_clip_originality_zoom"] = clip.OrigZoom;
        videoPublish["material_clip_originality_color"] = clip.OrigColor;
        videoPublish["material_clip_originality_speed"] = clip.OrigSpeed;
        videoPublish["material_clip_originality_fade"] = clip.OrigFade;
        videoPublish["material_clip_originality_sticker_dir"] = clip.OrigStickerDir ?? string.Empty;
    }

    private void SaveMaterialUploadAccounts()
    {
        var activeId = GetActiveMaterialUploadAccount()?.Id
                       ?? SelectedMaterialUploadAccount?.Id
                       ?? MaterialUploadAccounts.FirstOrDefault()?.Id
                       ?? string.Empty;
        _stateService.SaveMaterialUploadAccountsState(new DesktopStateService.MaterialUploadAccountsState(
            activeId,
            MaterialUploadAccounts.Select(item => item.ToState()).ToArray()));
        foreach (var account in MaterialUploadAccounts)
        {
            account.RefreshFileState();
        }

        OnPropertyChanged(nameof(MaterialUploadSummary));
    }

    private void SetMaterialUploadAccountActive(MaterialUploadAccountItemViewModel? account)
    {
        if (account is null)
        {
            return;
        }

        foreach (var item in MaterialUploadAccounts)
        {
            item.IsActive = ReferenceEquals(item, account);
        }

        SelectedMaterialUploadAccount = account;
        SaveMaterialUploadAccounts();
        RefreshVisibleMaterialUploadAccounts();
        OnPropertyChanged(nameof(CurrentMaterialUploadAccountSummary));
    }

    private MaterialUploadAccountItemViewModel? GetActiveMaterialUploadAccount() =>
        MaterialUploadAccounts.FirstOrDefault(item => item.IsActive);

    private MaterialUploadAccountItemViewModel? FindMaterialUploadAccount(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return MaterialUploadAccounts.FirstOrDefault(item =>
            string.Equals(item.Id, profileId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshMaterialUploadAccountNames()
    {
        foreach (var project in Projects)
        {
            if (string.IsNullOrWhiteSpace(project.MaterialUploadAccountProfileId))
            {
                continue;
            }

            project.MaterialUploadAccountName = FindMaterialUploadAccount(project.MaterialUploadAccountProfileId)?.DisplayName
                                                ?? project.MaterialUploadAccountProfileId;
        }
    }

    private string EnsureMaterialUploadAccountBrowserConfig(MaterialUploadAccountItemViewModel account)
    {
        var profileRoot = Path.GetDirectoryName(ExpandMaterialUploadPath(account.AuthFile))
                          ?? DesktopStateService.MaterialUploadProfilesRoot();
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(ExpandMaterialUploadPath(account.BrowserProfileDir));
        var outputDir = Path.Combine(profileRoot, "output");
        Directory.CreateDirectory(outputDir);
        var configPath = Path.Combine(profileRoot, "browser-login.json");
        var root = new JsonObject
        {
            ["base_url"] = "https://channels.weixin.qq.com",
            ["auth_file"] = account.AuthFile,
            ["output_dir"] = outputDir,
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["slow_mo_ms"] = 50,
                ["keep_open_seconds"] = 0,
                ["user_data_dir"] = account.BrowserProfileDir
            },
            ["login"] = new JsonObject
            {
                ["timeout_seconds"] = 300
            },
            ["debug"] = new JsonObject
            {
                ["log_file"] = Path.Combine(outputDir, "browser-login.log"),
                ["save_html"] = true,
                ["save_text"] = true
            }
        };
        File.WriteAllText(configPath, root.ToJsonString(MaterialUploadJsonOptions));
        return configPath;
    }

    private static string BuildAccountScopedStateFile(string? stateFile, string accountId)
    {
        var safeId = SanitizeAccountId(accountId);
        var value = string.IsNullOrWhiteSpace(stateFile)
            ? ".weixin-channel-publish-state.json"
            : stateFile.Trim();
        var fileName = Path.GetFileName(value);
        if (fileName.Contains(safeId, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var directory = Path.GetDirectoryName(value);
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        var scoped = $"{stem}-{safeId}{extension}";
        return string.IsNullOrWhiteSpace(directory) ? scoped : Path.Combine(directory, scoped);
    }

    private static string SanitizeAccountId(string value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '_');
        return string.IsNullOrWhiteSpace(safe) ? "account" : safe;
    }

    private static string BuildDefaultMaterialPublishConfigJson(ProjectListItemViewModel project)
    {
        var root = new JsonObject
        {
            ["task_type"] = "publish_videos",
            ["pause_on_error"] = true,
            ["video_publish"] = new JsonObject
            {
                ["enabled"] = true,
                ["run_strategy"] = "resume",
                ["state_file"] = ".weixin-channel-publish-state.json",
                ["allow_duplicate_publish"] = false,
                ["publish_video_source_mode"] = "project",
                ["video_source_mode"] = "project",
                ["episode_selection_mode"] = "all",
                ["start_episode_index"] = 1,
                ["publish_count"] = Math.Max(1, project.VideoCount),
                ["episode_indexes"] = new JsonArray(),
                ["fill_description"] = true,
                ["fill_short_title"] = false,
                ["description_template"] = "{新剧名}",
                ["prepend_hash_to_description"] = true,
                ["location_option_text"] = "不显示",
                ["link_option_text"] = "视频号剧集",
                ["link_picker_button_text"] = "选择需要添加的剧集",
                ["link_dialog_title"] = "选择需要关联的视频号剧集",
                ["link_search_placeholder"] = "搜索内容",
                ["activity_option_text"] = "不参与活动",
                ["timing_option_text"] = "不定时",
                ["short_title_max_length"] = 15,
                ["final_action"] = "publish",
                ["single_test_final_action"] = "publish",
                ["pause_on_error"] = true,
                ["video_upload_action"] = new JsonObject
                {
                    ["input_selector"] = "input[type='file'][accept*='video'], input[type='file']"
                }
            }
        };

        if (root["video_publish"] is JsonObject videoPublish)
        {
            ApplyGlobalMaterialClipPublishOptions(videoPublish);
        }

        return root.ToJsonString(MaterialUploadJsonOptions);
    }

    private static string ExpandMaterialUploadPath(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            text = Path.Combine(home, text.TrimStart('~', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        try
        {
            return Path.GetFullPath(text);
        }
        catch
        {
            return text;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            var fullPath = ExpandMaterialUploadPath(path);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
        }
    }

    private sealed record MaterialUploadExecutionTarget(
        ProjectListItemViewModel Project,
        MaterialUploadAccountItemViewModel Account,
        int Index);

    public sealed record MaterialPublishConfigTarget(
        ProjectListItemViewModel Project,
        string ConfigPath);

    private string? ResolveMaterialPublishConfigPath(ProjectListItemViewModel project)
    {
        foreach (var name in WeixinMaterialUploadConfigNames)
        {
            if (string.IsNullOrWhiteSpace(project.WorkflowProjectDir))
            {
                continue;
            }

            var candidate = Path.Combine(project.WorkflowProjectDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool MatchesMaterialUploadFilter(ProjectListItemViewModel project, string filter)
    {
        var token = filter.Trim();
        if (token.Length == 0)
        {
            return true;
        }

        return Contains(project.OriginalTitle, token)
               || Contains(project.NewTitle, token)
               || Contains(project.MaterialUploadNewTitleDisplay, token)
               || Contains(project.SourceSummary, token)
               || Contains(project.MaterialUploadStrategySummary, token)
               || Contains(project.MaterialUploadSelectionSummary, token)
               || Contains(project.MaterialPublishUploadedSummary, token)
               || Contains(project.MaterialUploadAccountProfileId, token)
               || Contains(project.MaterialUploadAccountName, token)
               || Contains(project.MaterialUploadAccountDisplay, token)
               || Contains(project.MaterialUploadNodeStatus, token)
               || Contains(project.WorkflowProjectDir, token)
               || Contains(project.SourceProjectDir, token);
    }
}
