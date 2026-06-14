using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Desktop.Services;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<ProjectListItemViewModel> MaterialUploadProjects { get; } = [];

    [ObservableProperty]
    private string materialUploadFilterText = string.Empty;

    [ObservableProperty]
    private bool materialUploadAllowDuplicatePublish;

    [ObservableProperty]
    private bool materialUploadGenerateHighlights = true;

    partial void OnMaterialUploadFilterTextChanged(string value)
    {
        ApplyMaterialUploadFilter();
        RefreshCommandStates();
    }

    public string MaterialUploadQueueButtonText =>
        $"上传素材队列 ({MaterialUploadProjects.Count(item => item.IsChecked)})";

    public string MaterialUploadSummary =>
        $"项目数: {MaterialUploadProjects.Count} | 已勾选: {MaterialUploadProjects.Count(item => item.IsChecked)} | 当前项目: {SelectedProject?.DisplayName ?? "未选择"}";

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
    }

    public void SetAllMaterialUploadProjectsChecked(bool isChecked)
    {
        foreach (var project in MaterialUploadProjects)
        {
            project.IsChecked = isChecked;
        }

        OnPropertyChanged(nameof(MaterialUploadQueueButtonText));
        OnPropertyChanged(nameof(MaterialUploadSummary));
    }

    public void ActivateMaterialUploadProject(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        SelectedProject = project;
        TaskQueueDetailMode = TaskQueueDetailMaterialUpload;
        SyncProjectLogFilterToSelection();
        SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
            ?? SelectedStepLogFilter;
        ActivityTitle = $"素材上传日志 · {project.DisplayName}";
        RefreshCommandStates();
        OnPropertyChanged(nameof(MaterialUploadSummary));
    }

    public async Task RunCheckedMaterialUploadQueueFromPageAsync()
    {
        var targets = MaterialUploadProjects.Where(item => item.IsChecked).ToArray();
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

            await PrepareMaterialUploadOverridesAsync(targets, refreshAfter: false);

            var mode = SelectedExecutionModeOption?.Key ?? ExecutionModeSerial;
            if (string.Equals(mode, ExecutionModeConcurrent2, StringComparison.Ordinal))
            {
                await ExecuteMaterialUploadBatchConcurrentAsync(targets, cancellationToken);
            }
            else
            {
                await ExecuteMaterialUploadBatchSerialAsync(targets, cancellationToken);
            }

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

            await PrepareMaterialUploadOverridesAsync([project], refreshAfter: false);
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
        project ??= SelectedProject;
        if (project is null)
        {
            return;
        }

        var configPath = ResolveMaterialPublishConfigPath(project);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            StatusMessage = $"未找到素材上传发表配置：{project.DisplayName}";
            AppendLog(StatusMessage, project.ProjectKey, project.DisplayName, "material-upload", "素材上传", isFailure: true);
            return;
        }

        _shellService.TryRevealPath(configPath, out _);
    }

    public void ShowMaterialUploadLogs(ProjectListItemViewModel? project)
    {
        if (project is not null)
        {
            ActivateMaterialUploadProject(project);
        }
        else if (SelectedProject is not null)
        {
            ActivateMaterialUploadProject(SelectedProject);
        }
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
        IEnumerable<ProjectListItemViewModel> projects,
        bool refreshAfter = true)
    {
        var refreshed = false;
        foreach (var project in projects)
        {
            if (TryApplyMaterialUploadRuntimeOverrides(project))
            {
                refreshed = true;
            }
        }

        if (refreshed && refreshAfter)
        {
            await RefreshProjectListAsync();
        }
    }

    private bool TryApplyMaterialUploadRuntimeOverrides(ProjectListItemViewModel project)
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
            if (videoPublish["enabled"] is null)
            {
                videoPublish["enabled"] = true;
            }

            File.WriteAllText(configPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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
               || Contains(project.SourceSummary, token)
               || Contains(project.MaterialUploadStrategySummary, token)
               || Contains(project.MaterialUploadSelectionSummary, token)
               || Contains(project.MaterialPublishUploadedSummary, token)
               || Contains(project.MaterialUploadNodeStatus, token)
               || Contains(project.WorkflowProjectDir, token)
               || Contains(project.SourceProjectDir, token);
    }
}
