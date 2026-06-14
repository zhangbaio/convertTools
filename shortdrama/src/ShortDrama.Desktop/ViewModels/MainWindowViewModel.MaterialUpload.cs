using CommunityToolkit.Mvvm.ComponentModel;
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

        await PrepareMaterialUploadOverridesAsync(targets);
        await RunCheckedWeixinMaterialUploadCommand.ExecuteAsync(null);
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
        await PrepareMaterialUploadOverridesAsync([project]);
        await RunSelectedWeixinMaterialUploadCommand.ExecuteAsync(null);
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

    private async Task PrepareMaterialUploadOverridesAsync(IEnumerable<ProjectListItemViewModel> projects)
    {
        var refreshed = false;
        foreach (var project in projects)
        {
            if (TryApplyMaterialUploadRuntimeOverrides(project))
            {
                refreshed = true;
            }
        }

        if (refreshed)
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
