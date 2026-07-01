using System.Security.Cryptography;
using System.Text;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly IReadOnlyDictionary<string, string> XingeStepKeyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["download"] = "download",
            ["rewrite"] = "rewrite_info",
            ["poster-rename"] = "generate_poster",
            ["cost-report"] = "generate_cost_report",
            ["project-image"] = "generate_project_images",
            ["material-convert"] = "material_transcode",
            ["weixin-upload"] = "upload_series",
            ["transcode"] = "video_transcode",
            ["batch-file-rename"] = "batch_file_rename",
            ["weixin-material-upload"] = "upload_materials"
        };

    private async Task SyncCheckedProjectsToXingeAsync()
    {
        var checkedProjects = Projects
            .Where(item => item.IsChecked)
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceProjectDir))
            .ToArray();
        if (checkedProjects.Length == 0)
        {
            return;
        }

        var payload = BuildXingeQueueSelectionPayload(checkedProjects);
        await RunBusyAsync($"正在同步 {checkedProjects.Length} 条勾选任务到 Xinge...", async cancellationToken =>
        {
            var global = _configService.LoadGlobal();
            var result = await _xingeRemoteControlService.SyncQueueSelectionAsync(global, payload, cancellationToken);
            var credentialsRefreshed =
                !string.Equals(global.XingeClientId, result.UpdatedGlobalConfig.XingeClientId, StringComparison.Ordinal) ||
                !string.Equals(global.XingeClientToken, result.UpdatedGlobalConfig.XingeClientToken, StringComparison.Ordinal);

            if (credentialsRefreshed)
            {
                _configService.SaveGlobal(result.UpdatedGlobalConfig);
                AppendLog("已自动刷新 Xinge 客户端凭证。");
            }

            StatusMessage =
                $"已同步 {Math.Max(result.ItemCount, checkedProjects.Length)} 条任务到 Xinge。"
                + (result.SnapshotId > 0 ? $" 快照 #{result.SnapshotId}" : string.Empty)
                + (!string.IsNullOrWhiteSpace(result.UpdatedAt) ? $" / 更新时间 {result.UpdatedAt}" : string.Empty);
            AppendLog(StatusMessage);
        });
    }

    private IReadOnlyDictionary<string, object?> BuildXingeQueueSelectionPayload(
        IReadOnlyList<ProjectListItemViewModel> checkedProjects)
    {
        var enabledSteps = GetTaskQueueSelectedSteps();
        var itemPayloads = checkedProjects
            .Select(project => BuildXingeQueueSelectionItem(project, enabledSteps))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["queue_type"] = "video_channel",
            ["workspace_path"] = RootDir,
            ["account_profile_name"] = string.Empty,
            ["enabled_steps"] = enabledSteps.Select(step => MapXingeStepKey(step.Key)).ToArray(),
            ["queue_options"] = new Dictionary<string, object?>
            {
                ["on_project_error"] = "skip",
                ["max_parallel_projects"] = ResolveTaskQueueMaxParallelProjects(),
                ["auto_archive_after_upload"] = false,
                ["force_rerun_completed_steps"] = false,
                ["prefer_upload_when_ready"] = false,
                ["sync_management_on_upload_success"] = false
            },
            ["runtime_summary"] = new Dictionary<string, object?>
            {
                ["is_running"] = IsBusy,
                ["project_count"] = Projects.Count,
                ["filtered_count"] = FilteredProjects.Count,
                ["checked_count"] = checkedProjects.Count,
                ["enabled_step_count"] = enabledSteps.Length,
                ["root_dir"] = RootDir,
                ["execution_mode"] = SelectedExecutionModeOption?.Key ?? ExecutionModeSerial
            },
            ["items"] = itemPayloads
        };
    }

    private IReadOnlyDictionary<string, object?> BuildXingeQueueSelectionItem(
        ProjectListItemViewModel project,
        IReadOnlyList<(string Key, string Label)> enabledSteps)
    {
        var projectPath = Path.GetFullPath(project.SourceProjectDir);
        var stepStates = BuildXingeStepStates(project, enabledSteps);
        var stepProgress = BuildXingeStepProgress(project, enabledSteps);

        return new Dictionary<string, object?>
        {
            ["project_key"] = ComputeSha256Hex(projectPath.ToLowerInvariant()),
            ["project_path"] = projectPath,
            ["project_name"] = FirstNonEmpty(project.NewTitle, project.OriginalTitle, project.DisplayName, Path.GetFileName(projectPath)),
            ["original_name"] = project.OriginalTitle,
            ["new_name"] = project.NewTitle,
            ["source"] = project.SourceSummary,
            ["episode_count"] = ParseIntOrDefault(project.EpisodeCountText),
            ["video_size"] = project.VideoSizeSummary,
            ["enqueue_at"] = project.CreatedAtSummary,
            ["overall_status"] = project.SchedulingStatus,
            ["current_step"] = ResolveXingeCurrentStepText(project, enabledSteps),
            ["step_states"] = stepStates,
            ["step_progress"] = stepProgress,
            ["failure_reason"] = ResolveXingeFailureReason(project)
        };
    }

    private Dictionary<string, string> BuildXingeStepStates(
        ProjectListItemViewModel project,
        IReadOnlyList<(string Key, string Label)> enabledSteps)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stepKey, _) in enabledSteps)
        {
            states[MapXingeStepKey(stepKey)] = ResolveXingeStepStatus(project, stepKey);
        }

        return states;
    }

    private Dictionary<string, string> BuildXingeStepProgress(
        ProjectListItemViewModel project,
        IReadOnlyList<(string Key, string Label)> enabledSteps)
    {
        var progress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stepKey, _) in enabledSteps)
        {
            progress[MapXingeStepKey(stepKey)] = ResolveXingeStepProgress(project, stepKey);
        }

        return progress;
    }

    private string ResolveXingeCurrentStepText(
        ProjectListItemViewModel project,
        IReadOnlyList<(string Key, string Label)> enabledSteps)
    {
        foreach (var (stepKey, stepLabel) in enabledSteps)
        {
            if (string.Equals(ResolveXingeStepStatus(project, stepKey), "进行中", StringComparison.Ordinal))
            {
                return stepLabel;
            }
        }

        foreach (var (stepKey, stepLabel) in enabledSteps)
        {
            var status = ResolveXingeStepStatus(project, stepKey);
            if (status is "失败" or "已停止" or "待继续")
            {
                return stepLabel;
            }
        }

        foreach (var (stepKey, stepLabel) in enabledSteps)
        {
            if (!string.Equals(ResolveXingeStepStatus(project, stepKey), "已完成", StringComparison.Ordinal))
            {
                return stepLabel;
            }
        }

        return string.Equals(project.SchedulingStatus, "已完成", StringComparison.Ordinal)
            ? "全流程完成"
            : project.SchedulingStatus;
    }

    private static string ResolveXingeFailureReason(ProjectListItemViewModel project)
    {
        return string.Equals(project.SchedulingStatus, "失败", StringComparison.Ordinal)
            ? FirstNonEmpty(project.FailedStep, project.ResumeFrom, project.SchedulingStatus)
            : string.Empty;
    }

    private static string ResolveXingeStepStatus(ProjectListItemViewModel project, string stepKey)
    {
        return stepKey switch
        {
            "download" => project.DownloadStepStatus,
            "transcode" => project.TranscodeStepStatus,
            "rewrite" => project.RewriteStepStatus,
            "poster-rename" => project.PosterRenameStepStatus,
            "project-image" => project.ProjectImageStepStatus,
            "cost-report" => project.CostReportStepStatus,
            "batch-file-rename" => project.BatchFileRenameStepStatus,
            "material-convert" => project.MaterialConvertStepStatus,
            "weixin-upload" => project.EpisodeUploadStepStatus,
            "weixin-material-upload" => project.MaterialUploadStepStatus,
            _ => "未开始"
        };
    }

    private static string ResolveXingeStepProgress(ProjectListItemViewModel project, string stepKey)
    {
        return stepKey switch
        {
            "download" => project.DownloadProgressText,
            "transcode" => project.GetProjectMaterialStepSummary("transcode"),
            "rewrite" => project.GetProjectMaterialStepSummary("rewrite"),
            "poster-rename" => project.GetProjectMaterialStepSummary("poster-rename"),
            "project-image" => project.GetProjectMaterialStepSummary("project-image"),
            "cost-report" => project.GetProjectMaterialStepSummary("cost-report"),
            "batch-file-rename" => project.GetProjectMaterialStepSummary("batch-file-rename"),
            "material-convert" => project.GetProjectMaterialStepSummary("material-convert"),
            "weixin-upload" => FirstNonEmpty(project.EpisodeUploadStageText, project.EpisodeUploadSubmitStatusText, project.EpisodeUploadNodeStatus),
            "weixin-material-upload" => FirstNonEmpty(project.MaterialUploadSelectionSummary, project.MaterialUploadStrategySummary, project.MaterialUploadNodeStatus),
            _ => string.Empty
        };
    }

    private int ResolveTaskQueueMaxParallelProjects()
    {
        return string.Equals(SelectedExecutionModeOption?.Key, ExecutionModeConcurrent2, StringComparison.Ordinal)
            ? 2
            : 1;
    }

    private static string MapXingeStepKey(string stepKey)
    {
        return XingeStepKeyMap.TryGetValue(stepKey, out var mapped)
            ? mapped
            : stepKey.Replace("-", "_", StringComparison.Ordinal);
    }

    private static int ParseIntOrDefault(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string ComputeSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
