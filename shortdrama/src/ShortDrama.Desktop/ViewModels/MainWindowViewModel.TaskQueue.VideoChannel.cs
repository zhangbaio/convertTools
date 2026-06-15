using ShortDrama.Core.Models;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private ProjectListItemViewModel[] OrderProjectsForQueueExecution(ProjectListItemViewModel[] projects)
    {
        if (!QueuePreferUploadWhenReadyEnabled || !QueueStepEpisodeUploadEnabled)
        {
            return projects;
        }

        return projects
            .OrderBy(project => ResolveQueueUploadPriority(project))
            .ThenBy(project => project.CreatedAtSummary, StringComparer.Ordinal)
            .ToArray();
    }

    private int ResolveQueueUploadPriority(ProjectListItemViewModel project)
    {
        var uploadPending = !string.Equals(project.EpisodeUploadStepStatus, "已完成", StringComparison.Ordinal);
        var downloadReady = string.Equals(project.DownloadStepStatus, "已完成", StringComparison.Ordinal);
        return uploadPending && downloadReady ? 0 : 1;
    }

    private async Task<bool?> ExecuteQueueSpecialStepAsync(
        ProjectListItemViewModel project,
        string stepKey,
        string stepLabel,
        CancellationToken cancellationToken)
    {
        switch (stepKey)
        {
            case QueueStepMaterialAutoRepairKey:
                return await ExecuteMaterialAutoRepairQueueStepAsync(project, cancellationToken);
            case QueueStepAutoFillInfoKey:
                return await ExecuteAutoFillInfoQueueStepAsync(project, cancellationToken);
            case QueueStepMaterialValidateKey:
                return await ExecuteMaterialValidateQueueStepAsync(project, cancellationToken);
            case QueueStepUploadRemuxKey:
                return await ExecuteUploadRemuxQueueStepAsync(project, cancellationToken);
            default:
                return null;
        }
    }

    private async Task<bool> ExecuteMaterialAutoRepairQueueStepAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken)
    {
        var workflowDir = project.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(workflowDir))
        {
            AppendLog("一键修复跳过：未找到 workflow 目录。", project.ProjectKey, project.DisplayName, "material-auto-repair", "一键修复", isFailure: true);
            return false;
        }

        var validation = await _materialValidationService.ValidateAsync(workflowDir, cancellationToken);
        var fixableCodes = validation.Issues.Where(issue => issue.CanAutoFix).Select(issue => issue.Code).Distinct(StringComparer.Ordinal).ToArray();
        if (fixableCodes.Length == 0)
        {
            AppendLog("一键修复跳过：未发现可自动修复的素材问题。", project.ProjectKey, project.DisplayName, "material-auto-repair", "一键修复");
            return true;
        }

        AppendLog($"开始一键修复：共 {fixableCodes.Length} 类问题。", project.ProjectKey, project.DisplayName, "material-auto-repair", "一键修复");

        if (fixableCodes.Contains("info-missing") || fixableCodes.Contains("info-invalid"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "rewrite", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("video-bitrate-low") || fixableCodes.Contains("videos-dir-missing") || fixableCodes.Contains("video-bitrate-unreadable"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "transcode", QueueForceRerunCompletedStepsEnabled, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("poster-missing"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "poster-rename", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("project-images-missing"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "project-image", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("material-video-title-mismatch"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "material-convert", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("cost-missing"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "cost-report", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("video-title-mismatch"))
        {
            await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "batch-file-rename", true, CreateBufferedProgress(), cancellationToken);
        }

        if (fixableCodes.Contains("weixin-upload-config-missing"))
        {
            await _workService.EnsureWeixinUploadConfigAsync(project.SourceProjectDir, null, cancellationToken);
        }

        if (fixableCodes.Contains("weixin-title-mismatch"))
        {
            await _workService.RefreshWeixinConfigsAsync(project.SourceProjectDir, null, cancellationToken);
        }

        var after = await _materialValidationService.ValidateAsync(workflowDir, cancellationToken);
        var ok = !after.HasErrors;
        AppendLog(
            ok ? "一键修复完成。" : "一键修复后仍存在未通过的素材问题。",
            project.ProjectKey,
            project.DisplayName,
            "material-auto-repair",
            "一键修复",
            isFailure: !ok);
        return ok;
    }

    private async Task<bool> ExecuteAutoFillInfoQueueStepAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken)
    {
        var result = await _workService.AutoFillProjectInfoAsync(project.SourceProjectDir, null, cancellationToken);
        AppendLog(
            result.Changed
                ? $"补齐字段完成：{string.Join(" / ", result.UpdatedFields)}"
                : "补齐字段跳过：结构化字段已完整。",
            project.ProjectKey,
            project.DisplayName,
            "auto-fill-info",
            "补齐字段");
        return true;
    }

    private async Task<bool> ExecuteMaterialValidateQueueStepAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken)
    {
        var workflowDir = project.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(workflowDir))
        {
            AppendLog("素材校验失败：未找到 workflow 目录。", project.ProjectKey, project.DisplayName, "material-validate", "素材校验", isFailure: true);
            return false;
        }

        var result = await _materialValidationService.ValidateAsync(workflowDir, cancellationToken);
        foreach (var issue in result.Issues)
        {
            AppendLog(
                $"[{issue.Severity}] {issue.Message}",
                project.ProjectKey,
                project.DisplayName,
                "material-validate",
                "素材校验",
                isFailure: string.Equals(issue.Severity, "错误", StringComparison.Ordinal));
        }

        if (!result.HasErrors)
        {
            AppendLog("素材校验通过。", project.ProjectKey, project.DisplayName, "material-validate", "素材校验");
        }

        return !result.HasErrors;
    }

    private async Task<bool> ExecuteUploadRemuxQueueStepAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken)
    {
        var result = await _workService.RemuxUploadVideosAsync(project.SourceProjectDir, null, CreateBufferedProgress(), cancellationToken);
        AppendLog(
            result.Message,
            project.ProjectKey,
            project.DisplayName,
            "upload-remux",
            "无损重封装",
            isFailure: !result.Ok);
        return result.Ok;
    }

    private async Task HandlePostEpisodeUploadQueueActionsAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken)
    {
        if (QueueSyncManagementOnUploadSuccessEnabled)
        {
            AppendLog(
                "同步管理系统已启用，但 shortdrama 当前还未接入对应后端，已跳过。",
                project.ProjectKey,
                project.DisplayName,
                "sync-management",
                "同步管理系统");
        }

        if (QueueAutoArchiveAfterUploadEnabled)
        {
            AppendLog(
                "上传完成后自动归档已启用，正在归档当前项目。",
                project.ProjectKey,
                project.DisplayName,
                "archive",
                "自动归档");
            await ArchiveProjectsCoreAsync([project.ProjectKey], Array.Empty<int>(), cancellationToken);
        }
    }
}
