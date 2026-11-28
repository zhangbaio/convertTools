using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>工作目录队列扫描 + 持久化合并（对齐 Python <c>scan_workspace_projects</c>）。</summary>
public static class WorkspaceQueueService
{
    private static readonly object DeletedDirSync = new();
    private static readonly Dictionary<string, HashSet<string>> DeletedDirCache = new(StringComparer.OrdinalIgnoreCase);

    private const string RemovedByXingeMarker = ".xinge-removed-from-queue";

    internal static bool IsRemovedByXinge(string projectDir) =>
        File.Exists(Path.Combine(projectDir, RemovedByXingeMarker));

    internal static void RemoveFromQueuePreservingFiles(string workspaceRoot, string projectDir)
    {
        File.WriteAllText(Path.Combine(projectDir, RemovedByXingeMarker), "");
        RemoveProjectsFromQueue(workspaceRoot, [projectDir]);
    }

    public static IReadOnlyList<QueueProjectItem> ScanProjects(string workspaceRoot) =>
        ScanProjects(workspaceRoot, reconcile: true);

    public static IReadOnlyList<QueueProjectItem> InspectProjects(string workspaceRoot) =>
        ScanProjects(workspaceRoot, reconcile: false);

    private static IReadOnlyList<QueueProjectItem> ScanProjects(string workspaceRoot, bool reconcile)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root)) return Array.Empty<QueueProjectItem>();

        if (reconcile)
        {
            QueueProjectDeletionRecovery.Recover(root);
            TryDeleteLegacyProjectDirectories(root);
        }
        var deleted = SnapshotDeletedProjectDirs(root);

        var state = WorkspaceQueueDatabase.Load(root);
        var clientSettings = new Lazy<ClientSettings>(
            () => ClientSettingsStore.Load(),
            LazyThreadSafetyMode.ExecutionAndPublication);
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
            if (deleted.Contains(normalized) || IsRemovedByXinge(normalized))
                continue;
            persistedByDir.TryGetValue(normalized, out var persisted);
            discovered[normalized] = MergeScanned(scanned, persisted, clientSettings);
        }

        var results = new List<QueueProjectItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outsideWorkspace = new List<string>();

        foreach (var (normalized, persisted) in persistedEntries)
        {
            // 队列只保留自己工作目录里的项目。别的账号目录即使文件夹还在，也不再显示，也不去扫那块盘。
            if (!IsProjectInsideWorkspace(root, normalized))
            {
                outsideWorkspace.Add(normalized);
                continue;
            }

            if (deleted.Contains(normalized) || IsRemovedByXinge(normalized))
                continue;

            if (!discovered.TryGetValue(normalized, out var item))
            {
                if (string.IsNullOrWhiteSpace(persisted.QueueEntryDramaType) &&
                    !WorkspaceProjectScanner.HasExplicitProjectSignal(normalized))
                {
                    continue;
                }
                if (!WorkspaceProjectScanner.IsValidProjectDirectory(normalized)) continue;
                item = MergeScanned(WorkspaceProjectScanner.BuildProject(normalized), persisted, clientSettings);
            }
            results.Add(item);
            seen.Add(normalized);
        }

        if (reconcile)
            RemoveStoredProjectsOutsideWorkspace(root, outsideWorkspace);

        foreach (var (normalized, item) in discovered)
        {
            if (seen.Contains(normalized)) continue;
            results.Add(item);
        }

        return OrderByQueuedAt(results);
    }

    /// <summary>
    /// 只读队列库里已写完上传时间的条目，不检查项目目录是否还在。
    /// </summary>
    public static IReadOnlyList<QueueProjectItem> LoadCompletedUploadItems(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<QueueProjectItem>();

        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            return Array.Empty<QueueProjectItem>();

        return WorkspaceQueueDatabase.Load(root).Items
            .Where(item => !string.IsNullOrWhiteSpace(item.UploadCompletedAt) &&
                           !string.IsNullOrWhiteSpace(item.ProjectDir) &&
                           IsProjectInsideWorkspace(root, item.ProjectDir))
            .ToArray();
    }

    /// <summary>
    /// Loads the database snapshot for immediate display, recovering interrupted deletions
    /// when a staging journal is present.
    /// </summary>
    public static (IReadOnlyList<QueueProjectItem> Items, QueueRunOptions Options) LoadPersistedSnapshot(
        string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            return (Array.Empty<QueueProjectItem>(), new QueueRunOptions());

        QueueProjectDeletionRecovery.Recover(root);
        var state = WorkspaceQueueDatabase.Load(root);
        var items = new List<QueueProjectItem>();
        var outsideWorkspace = new List<string>();
        foreach (var item in state.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            if (!IsProjectInsideWorkspace(root, item.ProjectDir))
            {
                outsideWorkspace.Add(item.ProjectDir);
                continue;
            }
            if (!Directory.Exists(item.ProjectDir))
                continue;
            if (IsDeletedProjectDir(root, item.ProjectDir))
                continue;

            item.NormalizeStepStates();
            RecoverInterruptedRunningSteps(item);
            ApplyManualUploadStatus(item);
            items.Add(item);
        }

        RemoveStoredProjectsOutsideWorkspace(root, outsideWorkspace);

        var options = QueueRunOptions.FromDictionary(state.Options);
        options.ClearTransientRunState();
        return (OrderByQueuedAt(items), options);
    }

    public static IReadOnlyList<QueueProjectItem> ResolveExecutionSnapshot(
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem>? displayedSnapshot,
        bool preferPersistedSnapshot)
    {
        var items = !preferPersistedSnapshot && displayedSnapshot is { Count: > 0 }
            ? displayedSnapshot
            : ScanProjects(workspaceRoot);
        return items
            .Where(item => IsProjectInsideWorkspace(workspaceRoot, item.ProjectDir))
            .ToArray();
    }

    public static IEnumerable<QueueProjectItem> FilterPendingUpload(IEnumerable<QueueProjectItem> items) =>
        items.Where(item => item.IsPendingUpload && !string.IsNullOrWhiteSpace(item.PrimaryVideoPath));

    public static void SaveProjects(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, Dictionary<string, object?>? options = null) =>
        WorkspaceQueueDatabase.Save(workspaceRoot, items, options);

    public static QueueRunOptions LoadRunOptions(string workspaceRoot)
    {
        var state = WorkspaceQueueDatabase.Load(workspaceRoot);
        var options = QueueRunOptions.FromDictionary(state.Options);
        options.ClearTransientRunState();
        return options;
    }

    public static void SaveRunOptions(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, QueueRunOptions options) =>
        WorkspaceQueueDatabase.Save(workspaceRoot, items, options.ToPersistentDictionary());

    public static void MarkUploadSeriesCompleted(
        string workspaceRoot,
        string projectDir,
        string? accountProfileId = null,
        string? accountProfileName = null)
    {
        var normalized = Path.GetFullPath(projectDir);
        if (!IsProjectInsideWorkspace(workspaceRoot, normalized))
            return;

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
        item.ManualUploadStatus = "";
        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        if (!string.IsNullOrWhiteSpace(accountProfileId))
            item.AccountProfileId = accountProfileId.Trim();
        if (!string.IsNullOrWhiteSpace(accountProfileName))
            item.AccountProfileName = accountProfileName.Trim();
        item.NormalizeStepStates();
        SaveProjects(workspaceRoot, items);
    }

    public static IReadOnlyList<QueueProjectItem> AddProjectsToQueue(
        string workspaceRoot,
        IEnumerable<string> projectDirs,
        string? accountProfileId = null,
        string? accountProfileName = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        ForgetDeletedProjects(root, projectDirs);
        var explicitAccountId = (accountProfileId ?? "").Trim();
        var explicitAccountName = (accountProfileName ?? "").Trim();
        var items = ScanProjects(root).ToList();
        var options = LoadRunOptions(root);
        var existing = items.ToDictionary(i => Path.GetFullPath(i.ProjectDir), StringComparer.OrdinalIgnoreCase);
        var appendedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var lastQueuedAt = DateTimeOffset.MinValue;

        foreach (var projectDir in projectDirs)
        {
            var normalized = Path.GetFullPath(projectDir);
            if (!IsProjectInsideWorkspace(root, normalized) ||
                !WorkspaceProjectScanner.IsValidProjectDirectory(normalized))
                continue;
            if (!appendedKeys.Add(normalized))
                continue;
            var removedMarker = Path.Combine(normalized, RemovedByXingeMarker);
            if (File.Exists(removedMarker))
                File.Delete(removedMarker);

            if (existing.TryGetValue(normalized, out var existingItem))
            {
                existingItem.Enabled = true;
                existingItem.QueuedAt = NextQueuedAt(ref lastQueuedAt);
                ApplyExplicitAccountBinding(existingItem);
                changed = true;
                continue;
            }

            var item = MergeScanned(WorkspaceProjectScanner.BuildProject(normalized), null);
            item.Enabled = true;
            item.QueuedAt = NextQueuedAt(ref lastQueuedAt);
            ApplyExplicitAccountBinding(item);
            items.Add(item);
            existing[normalized] = item;
            changed = true;
        }

        if (!changed)
            return Array.Empty<QueueProjectItem>();

        items = OrderByQueuedAt(items);
        SaveRunOptions(root, items, options);
        return items.Where(i => appendedKeys.Contains(Path.GetFullPath(i.ProjectDir))).ToArray();

        void ApplyExplicitAccountBinding(QueueProjectItem item)
        {
            if (string.IsNullOrWhiteSpace(explicitAccountId)) return;
            item.AccountProfileId = explicitAccountId;
            item.AccountProfileName = explicitAccountName;
        }
    }

    public static void RemoveProjectsFromQueue(string workspaceRoot, IEnumerable<string> projectDirs)
    {
        var removeKeys = projectDirs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = ScanProjects(workspaceRoot).Where(i => !removeKeys.Contains(Path.GetFullPath(i.ProjectDir))).ToList();
        SaveRunOptions(workspaceRoot, items, LoadRunOptions(workspaceRoot));
    }

    public static bool IsDeletedProjectDir(string workspaceRoot, string? projectDir)
    {
        var full = NormalizeDeletedProjectDir(projectDir);
        return full.Length > 0 && DeletedProjectDirs(workspaceRoot).Contains(full);
    }

    public static void RememberDeletedProjects(string workspaceRoot, IEnumerable<string> projectDirs)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        var root = QueueProjectDeletionPaths.Normalize(workspaceRoot);
        lock (DeletedDirSync)
        {
            var deleted = WorkspaceQueueDatabase.LoadLegacyDeletedProjectDirs(root);
            var changed = false;
            foreach (var path in projectDirs)
            {
                var full = NormalizeDeletedProjectDir(path);
                if (full.Length > 0 && deleted.Add(full))
                    changed = true;
            }

            if (changed)
            {
                WorkspaceQueueDatabase.SaveDeletedProjectDirs(root, deleted);
                DeletedDirCache.Remove(root);
            }
        }
    }

    public static void ForgetDeletedProjects(string workspaceRoot, IEnumerable<string> projectDirs)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        var root = QueueProjectDeletionPaths.Normalize(workspaceRoot);
        lock (DeletedDirSync)
        {
            var paths = projectDirs.Select(NormalizeDeletedProjectDir).Where(path => path.Length > 0).ToArray();
            WorkspaceQueueDatabase.ForgetDeletionPaths(root, paths, legacyOnly: false);
            DeletedDirCache.Remove(root);
        }
    }

    internal static void CommitProjectDeletion(string workspaceRoot, string projectDir)
    {
        var root = QueueProjectDeletionPaths.Normalize(workspaceRoot);
        lock (DeletedDirSync)
        {
            WorkspaceQueueDatabase.CommitProjectDeletion(root, projectDir);
            DeletedDirCache.Remove(root);
        }
    }

    public static List<QueueProjectItem> WithoutDeletedProjects(
        string workspaceRoot,
        IEnumerable<QueueProjectItem> items)
    {
        var visible = new List<QueueProjectItem>();
        foreach (var item in items)
        {
            if (item is null || IsDeletedProjectDir(workspaceRoot, item.ProjectDir))
                continue;
            visible.Add(item);
        }

        return visible;
    }

    private static HashSet<string> SnapshotDeletedProjectDirs(string workspaceRoot)
    {
        lock (DeletedDirSync)
            return new HashSet<string>(DeletedProjectDirs(workspaceRoot), StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> DeletedProjectDirs(string workspaceRoot)
    {
        var root = QueueProjectDeletionPaths.Normalize(workspaceRoot);
        lock (DeletedDirSync)
        {
            if (!DeletedDirCache.TryGetValue(root, out var deleted))
            {
                deleted = WorkspaceQueueDatabase.LoadDeletedProjectDirs(root);
                DeletedDirCache[root] = deleted;
            }

            return deleted;
        }
    }

    private static void TryDeleteLegacyProjectDirectories(string workspaceRoot)
    {
        lock (DeletedDirSync)
        {
            var removed = new List<string>();
            foreach (var projectDir in WorkspaceQueueDatabase.LoadLegacyDeletedProjectDirs(workspaceRoot))
            {
                try
                {
                    QueueProjectDeletionPaths.ValidateProjectDirectory(workspaceRoot, projectDir);
                    if (Directory.Exists(projectDir))
                        Directory.Delete(projectDir, recursive: true);
                    removed.Add(projectDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // 保留旧记录，等待路径校验通过且文件释放后再处理。
                }
            }

            if (removed.Count > 0)
            {
                WorkspaceQueueDatabase.ForgetDeletionPaths(workspaceRoot, removed, legacyOnly: true);
                DeletedDirCache.Remove(QueueProjectDeletionPaths.Normalize(workspaceRoot));
            }
        }
    }

    private static string NormalizeDeletedProjectDir(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static QueueProjectItem MergeScanned(
        WorkspaceProjectScanner.WorkspaceProject scanned,
        QueueProjectItem? persisted,
        Lazy<ClientSettings>? clientSettings = null)
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
                Remark = persisted.Remark,
                ManualUploadStatus = persisted.ManualUploadStatus,
                StepStates = new Dictionary<string, string>(persisted.StepStates),
                Archived = persisted.Archived,
                AccountProfileId = persisted.AccountProfileId,
                AccountProfileName = persisted.AccountProfileName,
                QueueEntryDramaType = persisted.QueueEntryDramaType,
                SourceEpisodeCount = persisted.SourceEpisodeCount,
                DisplayName = persisted.DisplayName,
                VideoVertical = persisted.VideoVertical,
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
        if (item.SourceEpisodeCount <= scanned.EpisodeCount &&
            OverLimitEpisodePolicy.TryReadOriginalEpisodeCount(scanned.ProjectDir, out var originalEpisodeCount) &&
            originalEpisodeCount > scanned.EpisodeCount)
            item.SourceEpisodeCount = originalEpisodeCount;
        if (scanned.VideoVertical is 0 or 1)
            item.VideoVertical = scanned.VideoVertical;
        item.PrimaryVideoPath = scanned.PrimaryVideoPath;
        item.CoverPath = scanned.CoverPath;
        if (string.IsNullOrWhiteSpace(item.QueuedAt))
            item.QueuedAt = ResolveInitialQueuedAt(scanned);

        item.NormalizeStepStates();
        RecoverLocalStepExecutionState(item, clientSettings);
        RecoverQueueItemExecutionState(item);
        ApplyManualUploadStatus(item);
        item.NormalizeStepStates();
        return item;
    }

    public static void ApplyManualUploadStatus(QueueProjectItem item)
    {
        var status = (item.ManualUploadStatus ?? "").Trim();
        if (string.IsNullOrWhiteSpace(status))
            return;

        item.CurrentStep = "";
        item.StepStates[QueueStepKeys.UploadSeries] = status;

        if (string.Equals(status, QueueStepStatus.Completed, StringComparison.Ordinal))
        {
            item.StatusText = QueueStepStatus.Completed;
            item.LastError = "";
            if (string.IsNullOrWhiteSpace(item.UploadCompletedAt))
                item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        }
        else if (string.Equals(status, QueueStepStatus.Failed, StringComparison.Ordinal))
        {
            item.StatusText = QueueStepStatus.Failed;
            item.UploadCompletedAt = "";
            if (string.IsNullOrWhiteSpace(item.LastError))
                item.LastError = "手动标记上传失败";
        }
    }

    private static void RecoverLocalStepExecutionState(
        QueueProjectItem item,
        Lazy<ClientSettings>? clientSettings = null)
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

        var downloadStatus = item.StepStates.GetValueOrDefault(QueueStepKeys.Download, QueueStepStatus.Pending);
        var preservesCompletedDownload = item.IsUploadCompleted ||
                                         item.ManualUploadStatus == QueueStepStatus.Completed ||
                                         item.StepStates.GetValueOrDefault(QueueStepKeys.DeleteSourceVideos) == QueueStepStatus.Completed;
        if (downloadStatus == QueueStepStatus.Pending ||
            (downloadStatus == QueueStepStatus.Completed && !preservesCompletedDownload))
        {
            item.StepStates[QueueStepKeys.Download] = QueueMaterialStepService.HasCompleteDownloadArtifacts(item)
                ? QueueStepStatus.Completed
                : QueueStepStatus.Pending;
        }

        if (IsPending(item, QueueStepKeys.RewriteInfo) &&
            WorkflowRewriteLooksCompleted(sourceProjectDir, workflowInfoPath, sourceInfo, workflowInfo))
        {
            item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed;
        }

        var posterStatus = item.StepStates.GetValueOrDefault(QueueStepKeys.GeneratePoster, QueueStepStatus.Pending);
        if (LocalManualDramaImportService.IsLocalManualImportProject(sourceProjectDir))
        {
            // Local imports copy the raw source poster into workflow under 海报图片.*. Only a
            // title-aware generation state proves that the poster step actually ran.
            var hasCurrentPoster = TikTokPosterGenerationStateService.HasCurrentTitleState(item);
            if (posterStatus == QueueStepStatus.Completed && !hasCurrentPoster)
                item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Pending;
            else if (posterStatus == QueueStepStatus.Pending && hasCurrentPoster)
                item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
        }
        else if (posterStatus == QueueStepStatus.Pending &&
                 HasGeneratedPoster(sourceProjectDir, workflowProjectDir))
        {
            item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
        }

        if (IsPending(item, QueueStepKeys.GenerateProjectImages) &&
            TikTokProjectImageService.HasCurrentProjectImages(sourceProjectDir))
        {
            item.StepStates[QueueStepKeys.GenerateProjectImages] = QueueStepStatus.Completed;
        }

        if (IsPending(item, QueueStepKeys.MaterialValidate) &&
            TikTokMaterialValidationService.HasCurrentValidationState(sourceProjectDir))
        {
            item.StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;
        }

        ReconcileCompletedArtifactStates(item, context, clientSettings);
    }

    private static void ReconcileCompletedArtifactStates(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        Lazy<ClientSettings>? clientSettings)
    {
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.RewriteInfo,
            () => !QueueMaterialStepService.NeedsAiRewrite(item));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GeneratePoster,
            () => !TikTokPosterGenerationStateService.NeedsGeneratePoster(
                item,
                (clientSettings ??= new Lazy<ClientSettings>(() => ClientSettingsStore.Load())).Value));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateEpisodeScript,
            () => TikTokEpisodeScriptService.HasCurrentOutput(item, account: null));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateAiScriptOutline,
            () => TikTokAiScriptOutlineService.HasCurrentOutput(item));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateAiDramaMaterials,
            () => TikTokAiDramaProductionMaterialService.HasCurrentOutput(context.WorkflowProjectDir));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateRoleVector,
            () => TikTokRoleVectorService.HasCurrentOutput(context.WorkflowProjectDir));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateProjectImages,
            () => TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.GenerateTimestampCertificate,
            () => TikTokTimestampCertificateService.HasCurrentOutput(item));
        ResetCompletedWhenMissing(
            item,
            QueueStepKeys.MaterialValidate,
            () => TikTokMaterialValidationService.HasCurrentValidationState(context.SourceProjectDir));
    }

    private static void ResetCompletedWhenMissing(
        QueueProjectItem item,
        string stepKey,
        Func<bool> hasCurrentArtifact)
    {
        if (item.StepStates.GetValueOrDefault(stepKey) == QueueStepStatus.Completed && !hasCurrentArtifact())
            item.StepStates[stepKey] = QueueStepStatus.Pending;
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
            item.StepStates[QueueStepKeys.GenerateProjectImages] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
            return;
        }

        if (StateBool(uploadState, "upload_step_attempted"))
        {
            item.UploadCompletedAt = "";
            CompleteIfPending(item, QueueStepKeys.GenerateProjectImages);
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

    /// <summary>
    /// 项目目录必须是这个工作目录的子目录。工作目录本身、同名前缀的兄弟目录、别的账号目录都不算。
    /// </summary>
    public static bool IsProjectInsideWorkspace(string workspaceRoot, string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(projectDir))
            return false;

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(projectDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return false;

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveStoredProjectsOutsideWorkspace(
        string workspaceRoot,
        IReadOnlyCollection<string> projectDirs)
    {
        if (projectDirs.Count == 0)
            return;

        try
        {
            WorkspaceQueueDatabase.DeleteProjects(workspaceRoot, projectDirs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // 队列库正被写入时删不掉越界记录。这次列表已经排除它，下次加载再删。
        }
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

    private static List<QueueProjectItem> OrderByQueuedAt(IEnumerable<QueueProjectItem> items) =>
        items
            .OrderBy(item => string.IsNullOrWhiteSpace(item.QueuedAt) ? "9999" : item.QueuedAt, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolveInitialQueuedAt(WorkspaceProjectScanner.WorkspaceProject scanned)
    {
        var candidates = new List<DateTimeOffset>();
        AddFileSystemCreatedAt(scanned.ProjectDir, candidates);
        AddFileSystemCreatedAt(scanned.PrimaryVideoPath, candidates);
        AddFileSystemCreatedAt(scanned.CoverPath, candidates);

        return candidates.Count == 0
            ? DateTimeOffset.Now.ToString("o")
            : candidates.Min().ToString("o");
    }

    private static string NextQueuedAt(ref DateTimeOffset lastQueuedAt)
    {
        var now = DateTimeOffset.Now;
        if (lastQueuedAt != DateTimeOffset.MinValue && now <= lastQueuedAt)
            now = lastQueuedAt.AddMilliseconds(1);
        lastQueuedAt = now;
        return now.ToString("o");
    }

    private static void AddFileSystemCreatedAt(string? path, ICollection<DateTimeOffset> candidates)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                candidates.Add(new DateTimeOffset(Directory.GetCreationTime(path)));
                return;
            }

            if (File.Exists(path))
                candidates.Add(new DateTimeOffset(File.GetCreationTime(path)));
        }
        catch
        {
            // Filesystem timestamps are a display fallback only; scan should not fail on inaccessible metadata.
        }
    }
}
