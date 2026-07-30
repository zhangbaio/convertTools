using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record DeletedCopyrightProofProjectRecoveryResult(
    bool Ok,
    string Message,
    QueueProjectItem? Project = null);

/// <summary>
/// Rebuilds a locally deleted, previously uploaded project from its durable execution snapshot.
/// The rebuilt project is only intended for the copyright-proof completion workflow.
/// </summary>
public static class DeletedCopyrightProofProjectRecoveryService
{
    public static async Task<DeletedCopyrightProofProjectRecoveryResult> RecoverAsync(
        string workspaceRoot,
        TikTokExecutionProjectSnapshot snapshot,
        ClientSettings settings,
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(account);

        var workspace = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspace))
            return Fail($"当前工作目录不存在：{workspace}");

        var history = snapshot.Item;
        var newTitle = (history.NewTitle ?? string.Empty).Trim();
        var originalTitle = (history.OriginalTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
            return Fail("历史记录缺少新剧名，无法安全定位 TikTok 项目。");
        if (string.IsNullOrWhiteSpace(originalTitle))
            return Fail($"历史记录缺少原剧名，无法恢复「{newTitle}」所需的原始视频。");

        var existing = WorkspaceQueueService.ScanProjects(workspace)
            .FirstOrDefault(item =>
                !item.Archived &&
                string.Equals(
                    (item.NewTitle ?? string.Empty).Trim(),
                    newTitle,
                    StringComparison.Ordinal));
        if (existing is not null)
            return new DeletedCopyrightProofProjectRecoveryResult(
                true,
                $"项目已在当前队列中恢复：{newTitle}",
                existing);

        log?.Invoke($"恢复已删除版权项目：正在按原剧名精确检索「{originalTitle}」");
        var lookup = await UploadTitleImportService.FindExactDramaAsync(
                originalTitle,
                history.EpisodeCount,
                ct)
            .ConfigureAwait(false);
        if (lookup.Item is null && history.EpisodeCount > 0)
        {
            // Older/over-limit queue records can contain the effective downloaded count
            // instead of the source drama's full count. A unique exact title remains safe
            // to recover even when that historic count no longer matches.
            var titleOnlyLookup = await UploadTitleImportService.FindExactDramaAsync(
                    originalTitle,
                    0,
                    ct)
                .ConfigureAwait(false);
            if (titleOnlyLookup.Item is not null)
            {
                log?.Invoke(
                    $"历史集数 {history.EpisodeCount} 与当前源数据不一致，" +
                    $"已按唯一原剧名「{originalTitle}」恢复。");
                lookup = titleOnlyLookup;
            }
        }
        if (lookup.Item is null)
        {
            return Fail(
                $"无法按历史原剧名「{originalTitle}」重新找到下载源：{lookup.Reason}。" +
                "请重新导入该原剧或提供原视频目录后再补全。");
        }

        var downloadState = DramaDownloadQueueStore.Load();
        var quality = string.IsNullOrWhiteSpace(downloadState.DefaultQuality)
            ? settings.DramaDownloadDefaultQuality
            : downloadState.DefaultQuality;
        var concurrent = downloadState.DownloadConcurrent > 0
            ? downloadState.DownloadConcurrent
            : settings.DramaDownloadConcurrent;
        var episodeNumberMode = string.IsNullOrWhiteSpace(downloadState.DownloadEpisodeNumberMode)
            ? "source"
            : downloadState.DownloadEpisodeNumberMode;

        var beforeBootstrapItems = WorkspaceQueueService.ScanProjects(workspace)
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .ToArray();
        var occupiedOriginal = beforeBootstrapItems.FirstOrDefault(item =>
            string.Equals(
                (item.OriginalTitle ?? string.Empty).Trim(),
                originalTitle,
                StringComparison.Ordinal) &&
            !string.Equals(
                (item.NewTitle ?? string.Empty).Trim(),
                newTitle,
                StringComparison.Ordinal));
        if (occupiedOriginal is not null)
        {
            return Fail(
                $"原剧「{originalTitle}」已被当前队列项目「{occupiedOriginal.NewTitle}」使用，" +
                "为避免覆盖现有项目，已停止自动恢复。");
        }

        var beforeBootstrap = beforeBootstrapItems
            .ToDictionary(
                item => Path.GetFullPath(item.ProjectDir),
                StringComparer.OrdinalIgnoreCase);

        var projectDir = await ShortDramaDramaServices.BootstrapAsync(
                workspace,
                lookup.Item,
                "all",
                quality,
                concurrent,
                episodeNumberMode,
                history.QueueEntryDramaType,
                ct)
            .ConfigureAwait(false);
        projectDir = Path.GetFullPath(projectDir);

        if (beforeBootstrap.TryGetValue(projectDir, out var occupied) &&
            !string.Equals(
                (occupied.NewTitle ?? string.Empty).Trim(),
                newTitle,
                StringComparison.Ordinal))
        {
            return Fail(
                $"原剧「{originalTitle}」已被当前队列项目「{occupied.NewTitle}」占用，" +
                "为避免覆盖现有项目，已停止自动恢复。");
        }

        ProjectWorkspaceService.EnsureWorkflowInfo(
            projectDir,
            Math.Max(1, lookup.Item.EpisodeTotal),
            log);
        WorkspaceQueueService.AddProjectsToQueue(workspace, [projectDir]);

        var scanned = WorkspaceQueueService.ScanProjects(workspace)
            .FirstOrDefault(item =>
                string.Equals(
                    Path.GetFullPath(item.ProjectDir),
                    projectDir,
                    StringComparison.OrdinalIgnoreCase));
        if (scanned is null)
            return Fail($"已重建原剧目录，但未能加入当前队列：{projectDir}");

        if (!string.Equals(
                (scanned.NewTitle ?? string.Empty).Trim(),
                newTitle,
                StringComparison.Ordinal))
        {
            QueueProjectTitleRenameService.RenameNewTitle(workspace, projectDir, newTitle);
        }

        var projects = WorkspaceQueueService.ScanProjects(workspace).ToList();
        var recovered = projects.FirstOrDefault(item =>
            string.Equals(
                Path.GetFullPath(item.ProjectDir),
                projectDir,
                StringComparison.OrdinalIgnoreCase));
        if (recovered is null)
            return Fail($"重建后未找到队列项目：{projectDir}");

        recovered.Enabled = true;
        recovered.Archived = false;
        recovered.AccountProfileId = account.Id;
        recovered.AccountProfileName = account.DisplayName;
        recovered.QueueEntryDramaType = history.QueueEntryDramaType;
        recovered.QueuedAt = DateTimeOffset.Now.ToString("o");
        recovered.UploadCompletedAt = history.UploadCompletedAt;
        recovered.ProofMaterialStatementDate = history.ProofMaterialStatementDate;
        recovered.Remark = string.IsNullOrWhiteSpace(history.Remark)
            ? "由已删除项目历史快照重建，仅用于补全版权证明"
            : history.Remark;
        recovered.CurrentStep = string.Empty;
        recovered.StatusText = QueueStepStatus.Pending;
        recovered.LastError = string.Empty;
        recovered.ManualUploadStatus = string.Empty;
        recovered.StepStates = new Dictionary<string, string>(history.StepStates);
        recovered.NormalizeStepStates();

        var options = WorkspaceQueueService.LoadRunOptions(workspace);
        WorkspaceQueueService.SaveRunOptions(workspace, projects, options);
        log?.Invoke(
            $"已从历史快照重建版权项目：{newTitle}；原剧：{originalTitle}；" +
            "后续只执行证明材料和 TikTok 版权页面编辑。");
        return new DeletedCopyrightProofProjectRecoveryResult(
            true,
            $"已恢复：{newTitle}",
            recovered);
    }

    private static DeletedCopyrightProofProjectRecoveryResult Fail(string message) =>
        new(false, message);
}
