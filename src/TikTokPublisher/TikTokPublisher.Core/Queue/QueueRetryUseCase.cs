using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record QueueRetryProject(string ProjectPath, long AssignmentId = 0, TikTokUploadSeriesSpec? ClientDownloadMigration = null);

public sealed record QueueRetryPreparation(IReadOnlyList<QueueProjectItem> AllItems, QueueRunOptions RunOptions);

public sealed record QueueRetryResult(string WorkspaceRoot, string Message, bool Accepted = false, bool Deferred = false);

/// <summary>Retries selected queued projects without resetting their completed steps.</summary>
public sealed class QueueRetryUseCase(QueueExecutionUseCase execution, WorkspaceQueueOrchestrator runner)
{
    public async Task<QueueRetryResult> ExecuteAsync(string? accountId, AccountStore accounts,
        IReadOnlyList<QueueRetryProject> projects, Func<string, bool> shouldDefer,
        Func<WorkspaceQueueTarget, Task<bool>> start,
        Action<string, QueueRetryPreparation> onPrepared, Action<string>? report = null)
    {
        if (projects.Count == 0)
            return new("", "重试命令没有指定剧集。");

        var profile = accounts.Accounts.FirstOrDefault(item => string.Equals(item.Id, accountId?.Trim(), StringComparison.Ordinal));
        if (profile is null)
            return new("", $"未找到重试命令指定的 TikTok 账号：{accountId}");
        var workspace = profile.ResolveWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
            return new("", $"账号「{profile.DisplayName}」没有配置有效工作目录。");

        var resolved = QueueExecutionUseCase.ResolveTarget(new WorkspaceQueueTarget(
            workspace, $"{profile.DisplayName} · {workspace}", profile.Id), accounts);
        var account = resolved.Account;
        var root = resolved.Target.WorkspaceRoot;
        if (shouldDefer(root))
            return new(root, "本地勾选队列正在运行，重试将在收尾后处理。", Deferred: true);

        var existing = new List<QueueRetryProject>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var retry in projects)
        {
            if (!TryResolve(root, retry.ProjectPath, out var path))
            {
                missing.Add(retry.ProjectPath);
                continue;
            }
            if (seen.Add(path))
                existing.Add(retry with { ProjectPath = path });
        }
        if (existing.Count == 0)
            return new(root, $"要重试的项目不在账号「{account.DisplayName}」的工作目录中或已删除：{string.Join("、", missing.Select(Path.GetFileName).Take(5))}");

        QueueRetryResult Accepted(string message)
        {
            if (missing.Count > 0)
                message += $"，{missing.Count} 个项目已不存在、已删除或不属于此账号，已跳过";
            return new(root, message + "。", Accepted: true);
        }

        if (execution.IsOwnedBy(root, account.Id) && existing.All(item => item.ClientDownloadMigration is null))
        {
            var candidates = SelectCandidates(WorkspaceQueueService.ScanProjects(root), root, existing, account, report);
            var appended = runner.TryAppendRetryItemsToRunningWorkspace(root, candidates);
            if (appended > 0)
                return Accepted($"已把 {appended} 部重试剧集追加到「{account.DisplayName}」运行中的队列末尾");
        }

        if (!await execution.WaitForIdleAsync(root, TimeSpan.FromSeconds(3)).ConfigureAwait(true))
            return new(root, "目标队列正在准备、运行或收尾，暂不能接纳重试，请稍后再试。");

        List<QueueProjectItem> idleCandidates;
        using (var reservation = execution.TryReserveWorkspaceForDeletion(root))
        {
            if (reservation is null)
                return new(root, "目标工作目录已被其它任务占用，暂不能准备重试。");

            var items = WorkspaceQueueService.ScanProjects(root).ToList();
            idleCandidates = SelectCandidates(items, root, existing, account, report);
            if (idleCandidates.Count == 0)
                return new(root, "要重试的项目未出现在当前账号队列中。");

            missing.AddRange(existing.Select(item => item.ProjectPath).Except(idleCandidates.Select(item => item.ProjectDir),
                StringComparer.OrdinalIgnoreCase));
            var options = WorkspaceQueueService.LoadRunOptions(root);
            WorkspaceQueueService.SaveRunOptions(root, items, options);
            onPrepared(root, new QueueRetryPreparation(items, options));
        }

        var retryTarget = resolved.Target with
        {
            ProjectDirFilter = idleCandidates.Select(item => item.ProjectDir).ToArray(),
            PreserveCompletedSteps = true,
        };
        if (await start(retryTarget).ConfigureAwait(true))
            return Accepted($"已按「执行勾选队列」重新执行「{account.DisplayName}」的 {idleCandidates.Count} 部剧");

        if (execution.IsOwnedBy(root, account.Id))
        {
            var appended = runner.TryAppendRetryItemsToRunningWorkspace(root, idleCandidates);
            if (appended > 0)
                return Accepted($"已把 {appended} 部重试剧集追加到「{account.DisplayName}」运行中的队列末尾");
        }
        return new(root, "重试队列未启动：没有可执行的步骤，或工作目录已被其它任务占用。");
    }

    private static List<QueueProjectItem> SelectCandidates(IEnumerable<QueueProjectItem> items, string root,
        IReadOnlyCollection<QueueRetryProject> retries, TikTokAccountProfile account, Action<string>? report)
    {
        var byPath = items.Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.ProjectDir))
            .GroupBy(item => QueueExecutionUseCase.NormalizePath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selected = new List<QueueProjectItem>();
        foreach (var (path, assignmentId, migration) in retries)
        {
            if (WorkspaceQueueService.IsDeletedProjectDir(root, path) || !byPath.TryGetValue(path, out var item) ||
                (!string.IsNullOrWhiteSpace(item.AccountProfileId) && item.AccountProfileId.Trim() != account.Id))
                continue;

            if (migration is not null)
            {
                var chain = VisibleDramaSource.ChainFor(ClientSettingsStore.Load(), account.TiktokDramaMarket);
                if (!RefreshAssignment(path, assignmentId, account.Id, report, migration, chain))
                    continue;
            }
            else if (assignmentId > 0)
            {
                RefreshAssignment(path, assignmentId, account.Id, report);
            }
            item.Enabled = true;
            if (string.IsNullOrWhiteSpace(item.AccountProfileId))
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }
            selected.Add(item);
        }
        return selected;
    }

    private static bool TryResolve(string root, string projectPath, out string path)
    {
        path = "";
        if (!WorkspaceQueueService.IsProjectInsideWorkspace(root, projectPath))
            return false;
        path = QueueExecutionUseCase.NormalizePath(projectPath);
        return Directory.Exists(path) && !WorkspaceQueueService.IsDeletedProjectDir(root, path);
    }

    public static void RemoveReleasedProject(string workspaceRoot, string accountId, TikTokRetryQueueItem project)
    {
        var path = project.ProjectPath;
        if (!WorkspaceQueueService.IsProjectInsideWorkspace(workspaceRoot, path))
            throw new InvalidOperationException("项目不在该账号的工作目录中。");
        if (WorkspaceQueueService.IsDeletedProjectDir(workspaceRoot, path) ||
            WorkspaceQueueService.IsRemovedByXinge(path))
            return;
        if (!Directory.Exists(path))
            return;
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("项目路径包含重解析点，无法安全移出。");
        }

        var item = WorkspaceQueueService.ScanProjects(workspaceRoot).FirstOrDefault(candidate =>
            string.Equals(QueueExecutionUseCase.NormalizePath(candidate.ProjectDir),
                QueueExecutionUseCase.NormalizePath(path), StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId) && item.AccountProfileId != accountId)
            throw new InvalidOperationException("项目属于另一个账号。");
        var marker = XingeAutoPublishMarkerStore.TryRead(path);
        if (marker is not null && (marker.AccountProfileId != accountId ||
            marker.AssignmentId > project.AutoPublishAssignmentId || marker.IsActive))
            throw new InvalidOperationException("项目已经绑定其他派单，或派单尚未结束。");

        WorkspaceQueueService.RemoveFromQueuePreservingFiles(workspaceRoot, path);
    }

    public static bool RefreshAssignment(string projectDir, long assignmentId, string accountId, Action<string>? report,
        TikTokUploadSeriesSpec? migration = null, string chain = "")
    {
        try
        {
            var marker = XingeAutoPublishMarkerStore.TryRead(projectDir);
            if (migration is not null && (assignmentId <= 0 || migration.EpisodeCnt <= 0 ||
                string.IsNullOrWhiteSpace(migration.Title) || string.IsNullOrWhiteSpace(chain) ||
                marker is null || !string.Equals(marker.SeriesId, migration.SeriesId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(marker.AccountProfileId, accountId, StringComparison.Ordinal)))
            {
                report?.Invoke("旧派单的项目标记、账号或剧集身份不匹配，已跳过迁移。");
                return false;
            }
            if (marker is { IsActive: true } && marker.AssignmentId == assignmentId &&
                (migration is null || marker.RequiresClientDownloadLookup))
                return true;

            var createdAt = marker is not null && marker.AssignmentId == assignmentId && marker.CreatedAt != default
                ? marker.CreatedAt : (DateTimeOffset?)null;
            XingeAutoPublishMarkerStore.Write(projectDir, new XingeAutoPublishAssignment(
                assignmentId, $"{XingeAutoPublishProgressReporter.DefaultProgressPathPrefix}{assignmentId}/progress",
                marker?.SeriesId ?? "", string.IsNullOrWhiteSpace(marker?.AccountProfileId) ? accountId : marker.AccountProfileId,
                migration?.Title ?? marker?.Title ?? "", marker?.ConnectionScope ?? "",
                migration?.EpisodeCnt ?? marker?.ExpectedEpisodeTotal ?? 0,
                migration is not null ? chain : marker?.ClientDownloadChain ?? "",
                migration is not null || marker?.RequiresClientDownloadLookup == true), createdAt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke($"更新自动上新标记失败：{projectDir}：{ex.Message}");
            return false;
        }
    }
}
