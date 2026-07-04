using System.Text.Json;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>工作目录队列扫描 + 持久化合并（对齐 Python <c>scan_workspace_projects</c>）。</summary>
public static class WorkspaceQueueService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    public static IReadOnlyList<QueueProjectItem> ScanProjects(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root)) return Array.Empty<QueueProjectItem>();

        var state = WorkspaceQueueDatabase.Load(root);
        var persistedEntries = state.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .Select(item => (Normalized: Path.GetFullPath(item.ProjectDir), Item: item))
            .ToList();
        var persistedByDir = persistedEntries
            .GroupBy(entry => entry.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.OrdinalIgnoreCase);

        var discovered = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var scanned in WorkspaceProjectScanner.Scan(root))
        {
            var normalized = Path.GetFullPath(scanned.ProjectDir);
            persistedByDir.TryGetValue(normalized, out var persisted);
            discovered[normalized] = MergeScanned(scanned, persisted);
        }

        var results = new List<QueueProjectItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (normalized, persisted) in persistedEntries)
        {
            if (!discovered.TryGetValue(normalized, out var item))
            {
                if (!IsWithinWorkspace(normalized, root)) continue;
                if (!WorkspaceProjectScanner.IsValidProjectDirectory(normalized)) continue;
                item = MergeScanned(WorkspaceProjectScanner.BuildProject(normalized), persisted);
            }
            results.Add(item);
            seen.Add(normalized);
        }

        foreach (var (normalized, item) in discovered)
        {
            if (seen.Contains(normalized)) continue;
            results.Add(item);
        }

        return OrderByQueuedAt(results);
    }

    public static IEnumerable<QueueProjectItem> FilterPendingUpload(IEnumerable<QueueProjectItem> items) =>
        items.Where(item => item.IsPendingUpload && !string.IsNullOrWhiteSpace(item.PrimaryVideoPath));

    public static void SaveProjects(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, Dictionary<string, object?>? options = null) =>
        WorkspaceQueueDatabase.Save(workspaceRoot, items, options);

    public static QueueRunOptions LoadRunOptions(string workspaceRoot)
    {
        var state = WorkspaceQueueDatabase.Load(workspaceRoot);
        return QueueRunOptions.FromDictionary(state.Options);
    }

    public static void SaveRunOptions(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, QueueRunOptions options) =>
        WorkspaceQueueDatabase.Save(workspaceRoot, items, options.ToDictionary());

    public static void MarkUploadSeriesCompleted(
        string workspaceRoot,
        string projectDir,
        string? accountProfileId = null,
        string? accountProfileName = null)
    {
        var normalized = Path.GetFullPath(projectDir);
        var items = ScanProjects(workspaceRoot).ToList();
        var item = items.FirstOrDefault(i =>
            string.Equals(Path.GetFullPath(i.ProjectDir), normalized, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            if (!WorkspaceProjectScanner.IsValidProjectDirectory(normalized))
                return;
            item = MergeScanned(WorkspaceProjectScanner.BuildProject(normalized), null);
            items.Add(item);
        }

        item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
        item.StatusText = QueueStepStatus.Completed;
        item.CurrentStep = "";
        item.LastError = "";
        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        if (!string.IsNullOrWhiteSpace(accountProfileId))
            item.AccountProfileId = accountProfileId.Trim();
        if (!string.IsNullOrWhiteSpace(accountProfileName))
            item.AccountProfileName = accountProfileName.Trim();
        item.NormalizeStepStates();
        SaveProjects(workspaceRoot, items);
    }

    public static IReadOnlyList<QueueProjectItem> AddProjectsToQueue(string workspaceRoot, IEnumerable<string> projectDirs)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var items = ScanProjects(root).ToList();
        var options = LoadRunOptions(root);
        var existing = items.ToDictionary(i => Path.GetFullPath(i.ProjectDir), StringComparer.OrdinalIgnoreCase);
        var appendedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var projectDir in projectDirs)
        {
            var normalized = Path.GetFullPath(projectDir);
            if (!WorkspaceProjectScanner.IsValidProjectDirectory(normalized))
                continue;
            if (!appendedKeys.Add(normalized))
                continue;

            if (existing.TryGetValue(normalized, out var existingItem))
            {
                existingItem.Enabled = true;
                existingItem.QueuedAt = DateTimeOffset.Now.ToString("o");
                changed = true;
                continue;
            }

            var item = MergeScanned(WorkspaceProjectScanner.BuildProject(normalized), null);
            item.Enabled = true;
            item.QueuedAt = DateTimeOffset.Now.ToString("o");
            items.Add(item);
            existing[normalized] = item;
            changed = true;
        }

        if (!changed)
            return Array.Empty<QueueProjectItem>();

        items = OrderByQueuedAt(items);
        SaveRunOptions(root, items, options);
        return items.Where(i => appendedKeys.Contains(Path.GetFullPath(i.ProjectDir))).ToArray();
    }

    public static void RemoveProjectsFromQueue(string workspaceRoot, IEnumerable<string> projectDirs)
    {
        var removeKeys = projectDirs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = ScanProjects(workspaceRoot).Where(i => !removeKeys.Contains(Path.GetFullPath(i.ProjectDir))).ToList();
        SaveRunOptions(workspaceRoot, items, LoadRunOptions(workspaceRoot));
    }

    private static QueueProjectItem MergeScanned(
        WorkspaceProjectScanner.WorkspaceProject scanned,
        QueueProjectItem? persisted)
    {
        var item = persisted is null
            ? new QueueProjectItem()
            : new QueueProjectItem
            {
                QueuedAt = persisted.QueuedAt,
                UploadCompletedAt = persisted.UploadCompletedAt,
                Enabled = persisted.Enabled,
                CurrentStep = persisted.CurrentStep,
                StatusText = persisted.StatusText,
                LastError = persisted.LastError,
                StepStates = new Dictionary<string, string>(persisted.StepStates),
                Archived = persisted.Archived,
                AccountProfileId = persisted.AccountProfileId,
                AccountProfileName = persisted.AccountProfileName,
                QueueEntryDramaType = persisted.QueueEntryDramaType,
                DisplayName = persisted.DisplayName,
            };

        item.ProjectDir = scanned.ProjectDir;
        if (item.Archived && Directory.Exists(scanned.ProjectDir))
            item.Archived = false;
        if (string.IsNullOrWhiteSpace(item.DisplayName))
            item.DisplayName = scanned.DisplayName;
        item.OriginalTitle = scanned.OriginalTitle;
        item.NewTitle = scanned.NewTitle;
        item.Description = scanned.Description;
        item.GenreCategory = scanned.GenreCategory;
        item.EpisodeCount = scanned.EpisodeCount;
        item.PrimaryVideoPath = scanned.PrimaryVideoPath;
        item.CoverPath = scanned.CoverPath;

        if (string.IsNullOrWhiteSpace(item.QueuedAt))
            item.QueuedAt = DateTimeOffset.Now.ToString("o");

        item.NormalizeStepStates();
        RecoverLocalStepExecutionState(item);
        RecoverQueueItemExecutionState(item);
        item.NormalizeStepStates();
        return item;
    }

    private static void RecoverLocalStepExecutionState(QueueProjectItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir)) return;

        ProjectWorkspaceContext context;
        try
        {
            context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        }
        catch
        {
            return;
        }

        var sourceProjectDir = context.SourceProjectDir;
        var workflowProjectDir = context.WorkflowProjectDir;
        var sourceInfoPath = Path.Combine(sourceProjectDir, "\u77ed\u5267\u4fe1\u606f.txt");
        var workflowInfoPath = Path.Combine(workflowProjectDir, "\u77ed\u5267\u4fe1\u606f.txt");
        var sourceInfo = ProjectInfoTextHelper.ParseInfoFile(sourceInfoPath);
        var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(workflowInfoPath);

        var hasSourceVideos = HasVideoFiles(sourceProjectDir, Path.Combine(sourceProjectDir, "videos"));
        var hasStagedUploadVideos = HasVideoFiles(Path.Combine(workflowProjectDir, TikTokUploadStagingService.StagingDirName));
        var hasDownloadArtifacts = HasVideoFiles(
            sourceProjectDir,
            Path.Combine(sourceProjectDir, "videos"),
            Path.Combine(workflowProjectDir, "videos"),
            Path.Combine(workflowProjectDir, TikTokUploadStagingService.StagingDirName));

        if (IsPending(item, QueueStepKeys.Download) && hasDownloadArtifacts)
            item.StepStates[QueueStepKeys.Download] = QueueStepStatus.Completed;

        if (IsPending(item, QueueStepKeys.RewriteInfo) &&
            WorkflowRewriteLooksCompleted(sourceProjectDir, workflowInfoPath, sourceInfo, workflowInfo))
        {
            item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed;
        }

        if (IsPending(item, QueueStepKeys.GeneratePoster) && HasGeneratedPoster(sourceProjectDir, workflowProjectDir))
            item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;

        var manifestExists = HasTikTokUploadManifest(context);
        if (IsPending(item, QueueStepKeys.SmallVideoRepair) && (manifestExists || hasStagedUploadVideos))
            item.StepStates[QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed;

        if (IsPending(item, QueueStepKeys.SilenceDetect) && HasSilenceAsrReport(context))
            item.StepStates[QueueStepKeys.SilenceDetect] = QueueStepStatus.Completed;

        if (IsPending(item, QueueStepKeys.MaterialValidate) && manifestExists)
            item.StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;

        if (IsPending(item, QueueStepKeys.DeleteSourceVideos) &&
            !hasSourceVideos &&
            hasDownloadArtifacts &&
            (hasStagedUploadVideos || manifestExists ||
             item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Completed))
        {
            item.StepStates[QueueStepKeys.DeleteSourceVideos] = QueueStepStatus.Completed;
        }
    }

    private static void RecoverQueueItemExecutionState(QueueProjectItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir)) return;

        string workflowProjectDir;
        try
        {
            workflowProjectDir = ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir;
        }
        catch
        {
            return;
        }

        var uploadState = TikTokUploadStateStore.LoadState(workflowProjectDir);
        var uploadFailedAt = StateText(uploadState, "last_upload_step_failed_at");
        var uploadError = StateText(uploadState, "last_upload_step_error");
        var uploadCompletedAt = StateText(uploadState, "last_upload_completed_at");
        if (string.IsNullOrWhiteSpace(uploadCompletedAt))
            uploadCompletedAt = item.UploadCompletedAt.Trim();

        if (!string.IsNullOrWhiteSpace(uploadCompletedAt) &&
            string.IsNullOrWhiteSpace(uploadFailedAt) &&
            string.IsNullOrWhiteSpace(uploadError))
        {
            item.UploadCompletedAt = uploadCompletedAt;
            item.CurrentStep = "";
            item.StatusText = QueueStepStatus.Completed;
            item.LastError = "";
            item.StepStates[QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
            return;
        }

        if (StateBool(uploadState, "upload_step_attempted"))
        {
            item.UploadCompletedAt = "";
            CompleteIfPending(item, QueueStepKeys.SmallVideoRepair);
            CompleteIfPending(item, QueueStepKeys.MaterialValidate);

            var uploadStatus = item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries, QueueStepStatus.Pending).Trim();
            if (!string.IsNullOrWhiteSpace(uploadFailedAt) || !string.IsNullOrWhiteSpace(uploadError))
            {
                item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Failed;
                item.StatusText = QueueStepStatus.Failed;
            }
            else if (uploadStatus is QueueStepStatus.Pending or QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot)
            {
                item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Stopped;
                if (IsTransientStatus(item.StatusText))
                    item.StatusText = QueueStepStatus.Stopped;
            }

            if (item.CurrentStep == QueueStepKeys.UploadSeries)
                item.CurrentStep = "";

            if (item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Failed)
            {
                if (string.IsNullOrWhiteSpace(item.LastError))
                    item.LastError = string.IsNullOrWhiteSpace(uploadError)
                        ? "检测到上次上传失败，可直接重试上传。"
                        : uploadError;
            }
            else if (item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Stopped &&
                     string.IsNullOrWhiteSpace(item.LastError))
            {
                item.LastError = "检测到上次已进入上传步骤但未完成，可直接重试上传。";
            }
        }

        RecoverInterruptedRunningSteps(item);
    }

    private static void RecoverInterruptedRunningSteps(QueueProjectItem item)
    {
        var runningStepKeys = QueueStepRegistry.All
            .Select(step => step.Key)
            .Where(stepKey => IsTransientRunningStatus(item.StepStates.GetValueOrDefault(stepKey, "")))
            .ToHashSet(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(item.CurrentStep) &&
            item.StepStates.ContainsKey(item.CurrentStep) &&
            IsTransientRunningStatus(item.StepStates.GetValueOrDefault(item.CurrentStep, "")))
        {
            runningStepKeys.Add(item.CurrentStep);
        }

        if (runningStepKeys.Count == 0 && !IsTransientRunningStatus(item.StatusText))
            return;

        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Stopped;
        foreach (var stepKey in runningStepKeys)
        {
            if (item.StepStates.ContainsKey(stepKey) &&
                IsTransientRunningStatus(item.StepStates.GetValueOrDefault(stepKey, "")))
            {
                item.StepStates[stepKey] = QueueStepStatus.Stopped;
            }
        }

        if (string.IsNullOrWhiteSpace(item.LastError))
            item.LastError = "上次任务在软件关闭前未完成，已恢复为可重试状态。";
    }

    private static bool WorkflowRewriteLooksCompleted(
        string sourceProjectDir,
        string workflowInfoPath,
        IReadOnlyDictionary<string, string> sourceInfo,
        IReadOnlyDictionary<string, string> workflowInfo)
    {
        if (!File.Exists(workflowInfoPath) || workflowInfo.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(InfoValue(workflowInfo, "\u63a8\u8350\u8bed")))
            return true;

        var sourceTitles = new[]
            {
                Path.GetFileName(sourceProjectDir),
                InfoValue(sourceInfo, "\u539f\u5267\u540d"),
                InfoValue(sourceInfo, "\u5267\u540d"),
                InfoValue(sourceInfo, "\u6807\u9898"),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeTitleForCompare)
            .ToHashSet(StringComparer.Ordinal);
        var workflowTitle = NormalizeTitleForCompare(InfoValue(workflowInfo, "\u65b0\u5267\u540d", "\u5267\u540d"));
        if (!string.IsNullOrWhiteSpace(workflowTitle) && !sourceTitles.Contains(workflowTitle))
            return true;

        var tags = InfoValue(workflowInfo, "\u6807\u7b7e").Trim();
        if (!string.IsNullOrWhiteSpace(tags) && tags.Replace(" ", "", StringComparison.Ordinal) != "#\u77ed\u5267#\u5267\u60c5")
            return true;

        foreach (var key in new[]
                 {
                     "TikTok\u76ee\u6807\u89c2\u4f17",
                     "TikTok \u76ee\u6807\u89c2\u4f17",
                     "\u76ee\u6807\u89c2\u4f17",
                     "\u76ee\u6807\u53d7\u4f17",
                     "TikTok\u9898\u6750\u7c7b\u578b",
                     "TikTok \u9898\u6750\u7c7b\u578b",
                     "\u9898\u6750\u7c7b\u578b",
                     "\u9898\u6750",
                 })
        {
            if (!string.IsNullOrWhiteSpace(InfoValue(workflowInfo, key)))
                return true;
        }

        return false;
    }

    private static bool HasGeneratedPoster(string sourceProjectDir, string workflowProjectDir)
    {
        foreach (var root in new[] { workflowProjectDir, sourceProjectDir })
        {
            foreach (var name in new[]
                     {
                         "\u6d77\u62a5\u56fe\u7247.png",
                         "\u6d77\u62a5\u56fe\u7247.jpg",
                     })
            {
                if (File.Exists(Path.Combine(root, name)))
                    return true;
            }
        }

        return false;
    }

    private static bool HasTikTokUploadManifest(ProjectWorkspaceContext context)
    {
        if (ProjectStateDocumentStore.LoadDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                TikTokUploadManifestService.DocumentType).Count > 0)
        {
            return true;
        }

        return File.Exists(Path.Combine(context.WorkflowProjectDir, "tiktok-upload-manifest.json"));
    }

    private static bool HasSilenceAsrReport(ProjectWorkspaceContext context)
    {
        if (ProjectStateDocumentStore.LoadDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                "silence_asr_report").Count > 0)
        {
            return true;
        }

        return File.Exists(Path.Combine(context.WorkflowProjectDir, "silence-asr-report.json"));
    }

    private static bool HasVideoFiles(params string[] roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            try
            {
                if (Directory.EnumerateFiles(root)
                    .Any(path => VideoExtensions.Contains(Path.GetExtension(path))))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore directories that disappear while scanning.
            }
        }

        return false;
    }

    private static bool IsPending(QueueProjectItem item, string stepKey) =>
        item.StepStates.GetValueOrDefault(stepKey, QueueStepStatus.Pending) == QueueStepStatus.Pending;

    private static void CompleteIfPending(QueueProjectItem item, string stepKey)
    {
        if (IsPending(item, stepKey))
            item.StepStates[stepKey] = QueueStepStatus.Completed;
    }

    private static bool IsTransientStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ||
        status is QueueStepStatus.Pending or QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot;

    private static bool IsTransientRunningStatus(string? status) =>
        status is QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot;

    private static string StateText(IReadOnlyDictionary<string, JsonElement> state, string key)
    {
        if (!state.TryGetValue(key, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText().Trim(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static bool StateBool(IReadOnlyDictionary<string, JsonElement> state, string key)
    {
        if (!state.TryGetValue(key, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static string InfoValue(IReadOnlyDictionary<string, string> info, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (info.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string NormalizeTitleForCompare(string? value) =>
        string.Concat((value ?? "").Trim().TrimStart('_').Where(ch => !char.IsWhiteSpace(ch)));

    private static bool IsWithinWorkspace(string projectDir, string workspaceRoot)
    {
        var project = Path.GetFullPath(projectDir);
        var workspace = Path.GetFullPath(workspaceRoot);
        return project.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(project, workspace, StringComparison.OrdinalIgnoreCase);
    }

    private static List<QueueProjectItem> OrderByQueuedAt(IEnumerable<QueueProjectItem> items) =>
        items
            .OrderBy(item => string.IsNullOrWhiteSpace(item.QueuedAt) ? "9999" : item.QueuedAt, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
