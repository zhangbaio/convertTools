using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record WorkspaceMergeSourceAnalysis(
    string WorkspaceRoot,
    int ActiveProjectCount,
    int ArchivedProjectCount,
    int MissingArchiveComponentCount,
    IReadOnlyList<string> Warnings);

public sealed record WorkspaceMergeAnalysis(
    string TargetWorkspaceRoot,
    IReadOnlyList<WorkspaceMergeSourceAnalysis> Sources)
{
    public int ActiveProjectCount => Sources.Sum(source => source.ActiveProjectCount);
    public int ArchivedProjectCount => Sources.Sum(source => source.ArchivedProjectCount);
    public int MissingArchiveComponentCount => Sources.Sum(source => source.MissingArchiveComponentCount);
    public IReadOnlyList<string> Warnings => Sources.SelectMany(source => source.Warnings).ToArray();
}

public sealed record WorkspaceMergeProgress(
    int Completed,
    int Total,
    string Stage,
    string Message);

public sealed record WorkspaceMergeResult(
    int ImportedProjectCount,
    int ImportedArchiveCount,
    int ReusedProjectCount,
    int ReusedArchiveCount,
    IReadOnlyList<string> Warnings,
    string BackupDatabasePath);

/// <summary>
/// Copies one or more historical TikTok workspaces into the active workspace while preserving
/// queue state, project documents and restorable archive metadata.
/// </summary>
public static class WorkspaceMergeService
{
    private const string MergeStateDirectoryName = ".workspace-merge";
    private const string MergeIndexFileName = "import-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static WorkspaceMergeAnalysis Analyze(
        string targetWorkspaceRoot,
        IEnumerable<string> sourceWorkspaceRoots)
    {
        var targetRoot = ValidateTarget(targetWorkspaceRoot);
        var sourceRoots = NormalizeSources(targetRoot, sourceWorkspaceRoots);
        var analyses = new List<WorkspaceMergeSourceAnalysis>();

        foreach (var sourceRoot in sourceRoots)
        {
            var warnings = new List<string>();
            IReadOnlyList<QueueProjectItem> projects;
            IReadOnlyList<ArchivedProjectItem> archives;
            try
            {
                projects = LoadSourceProjects(sourceRoot);
            }
            catch (Exception ex)
            {
                projects = Array.Empty<QueueProjectItem>();
                warnings.Add($"读取队列失败：{ex.Message}");
            }

            try
            {
                archives = TikTokArchivedProjectService.List(
                    sourceRoot,
                    Path.Combine(sourceRoot, "archive"));
            }
            catch (Exception ex)
            {
                archives = Array.Empty<ArchivedProjectItem>();
                warnings.Add($"读取归档失败：{ex.Message}");
            }

            var missingArchiveComponents = archives.Count(item =>
                (!string.IsNullOrWhiteSpace(item.ArchivedSourceDir) &&
                 !Directory.Exists(item.ArchivedSourceDir)) ||
                (!string.IsNullOrWhiteSpace(item.ArchivedWorkflowDir) &&
                 !Directory.Exists(item.ArchivedWorkflowDir)));
            if (missingArchiveComponents > 0)
                warnings.Add($"{missingArchiveComponents} 个归档项目存在缺失目录；记录仍会迁移，但缺失文件无法恢复。");

            analyses.Add(new WorkspaceMergeSourceAnalysis(
                sourceRoot,
                projects.Count,
                archives.Count,
                missingArchiveComponents,
                warnings));
        }

        return new WorkspaceMergeAnalysis(targetRoot, analyses);
    }

    public static WorkspaceMergeResult Merge(
        WorkspaceMergeAnalysis analysis,
        TikTokAccountProfile targetAccount,
        string? targetArchiveRootDir = null,
        IProgress<WorkspaceMergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(targetAccount);

        var targetRoot = ValidateTarget(analysis.TargetWorkspaceRoot);
        var sourceRoots = NormalizeSources(targetRoot, analysis.Sources.Select(source => source.WorkspaceRoot));
        var stateRoot = Path.Combine(targetRoot, MergeStateDirectoryName);
        Directory.CreateDirectory(stateRoot);

        using var targetLock = AcquireWorkspaceLock(stateRoot);
        var indexPath = Path.Combine(stateRoot, MergeIndexFileName);
        var index = LoadMergeIndex(indexPath);
        var targetQueueState = WorkspaceQueueDatabase.Load(targetRoot);
        var targetItems = WorkspaceQueueService.ScanProjects(targetRoot).ToList();
        var knownTargetProjects = targetItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedRestoreSourcePaths = new HashSet<string>(knownTargetProjects, StringComparer.OrdinalIgnoreCase);
        var reservedRestoreWorkflowPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetArchiveRoot = TikTokArchivedProjectService.ResolveArchiveRoot(
            targetRoot,
            targetArchiveRootDir);
        foreach (var existingArchive in TikTokArchivedProjectService.List(targetRoot, targetArchiveRoot))
        {
            if (!string.IsNullOrWhiteSpace(existingArchive.SourceProjectDir))
                reservedRestoreSourcePaths.Add(SafeFullPath(existingArchive.SourceProjectDir));
            if (!string.IsNullOrWhiteSpace(existingArchive.WorkflowProjectDir))
                reservedRestoreWorkflowPaths.Add(SafeFullPath(existingArchive.WorkflowProjectDir));
        }
        var warnings = analysis.Warnings.ToList();
        var createdDirectories = new List<string>();
        var createdFiles = new List<string>();
        var importedProjects = 0;
        var importedArchives = 0;
        var reusedProjects = 0;
        var reusedArchives = 0;
        var backupPath = BackupTargetDatabase(targetRoot, stateRoot);
        var allSourceProjects = sourceRoots.ToDictionary(
            source => source,
            LoadSourceProjects,
            StringComparer.OrdinalIgnoreCase);
        var allSourceArchives = sourceRoots.ToDictionary(
            source => source,
            source => TikTokArchivedProjectService.List(source, Path.Combine(source, "archive")),
            StringComparer.OrdinalIgnoreCase);
        var total = allSourceProjects.Values.Sum(items => items.Count) +
                    allSourceArchives.Values.Sum(items => items.Count);
        var completed = 0;

        try
        {
            foreach (var sourceRoot in sourceRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceTag = SanitizePathPart(Path.GetFileName(TrimSeparators(sourceRoot)));
                if (string.IsNullOrWhiteSpace(sourceTag))
                    sourceTag = "来源目录";

                foreach (var sourceItem in allSourceProjects[sourceRoot])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new WorkspaceMergeProgress(
                        completed,
                        total,
                        "普通项目",
                        $"正在合并：{sourceItem.Title}"));

                    var entry = ImportActiveProject(
                        sourceRoot,
                        targetRoot,
                        sourceTag,
                        sourceItem,
                        targetAccount,
                        index,
                        indexPath,
                        knownTargetProjects,
                        createdDirectories,
                        cancellationToken,
                        out var reused);
                    if (reused) reusedProjects++;
                    else importedProjects++;

                    var existingIndex = targetItems.FindIndex(item =>
                        string.Equals(
                            SafeFullPath(item.ProjectDir),
                            SafeFullPath(entry.Item.ProjectDir),
                            StringComparison.OrdinalIgnoreCase));
                    if (existingIndex >= 0)
                        targetItems[existingIndex] = entry.Item;
                    else
                        targetItems.Add(entry.Item);

                    completed++;
                    progress?.Report(new WorkspaceMergeProgress(
                        completed,
                        total,
                        "普通项目",
                        $"已合并：{entry.Item.Title}"));
                }

                foreach (var archive in allSourceArchives[sourceRoot])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new WorkspaceMergeProgress(
                        completed,
                        total,
                        "归档项目",
                        $"正在合并归档：{archive.DisplayName}"));

                    var reused = ImportArchiveProject(
                        sourceRoot,
                        targetRoot,
                        targetArchiveRoot,
                        sourceTag,
                        archive,
                        targetAccount,
                        index,
                        indexPath,
                        reservedRestoreSourcePaths,
                        reservedRestoreWorkflowPaths,
                        createdDirectories,
                        createdFiles,
                        warnings,
                        cancellationToken);
                    if (reused) reusedArchives++;
                    else importedArchives++;

                    completed++;
                    progress?.Report(new WorkspaceMergeProgress(
                        completed,
                        total,
                        "归档项目",
                        $"已合并归档：{archive.DisplayName}"));
                }
            }

            WorkspaceQueueDatabase.Save(targetRoot, targetItems, targetQueueState.Options);
            TikTokArchivedProjectService.List(targetRoot, targetArchiveRoot);
            SaveMergeIndex(indexPath, index);

            progress?.Report(new WorkspaceMergeProgress(
                total,
                total,
                "完成",
                $"合并完成：普通项目 {importedProjects + reusedProjects} 个，归档项目 {importedArchives + reusedArchives} 个"));

            return new WorkspaceMergeResult(
                importedProjects,
                importedArchives,
                reusedProjects,
                reusedArchives,
                warnings,
                backupPath);
        }
        catch
        {
            RollBackCreatedFiles(targetRoot, targetArchiveRoot, createdFiles, createdDirectories);
            RestoreTargetDatabase(targetRoot, backupPath);
            throw;
        }
    }

    private static ImportedActiveProject ImportActiveProject(
        string sourceRoot,
        string targetRoot,
        string sourceTag,
        QueueProjectItem sourceItem,
        TikTokAccountProfile targetAccount,
        WorkspaceMergeIndex index,
        string indexPath,
        ISet<string> knownTargetProjects,
        ICollection<string> createdDirectories,
        CancellationToken cancellationToken,
        out bool reused)
    {
        if (string.IsNullOrWhiteSpace(sourceItem.ProjectDir))
            throw new InvalidOperationException("来源队列包含空项目目录。");

        var context = ProjectWorkspaceService.LoadContext(sourceItem.ProjectDir);
        var sourceProjectDir = Path.GetFullPath(context.SourceProjectDir);
        var sourceWorkflowDir = Path.GetFullPath(context.WorkflowProjectDir);
        if (!Directory.Exists(sourceProjectDir))
            throw new DirectoryNotFoundException($"来源项目目录不存在：{sourceProjectDir}");

        var originKey = StableOriginKey("project", sourceRoot, sourceProjectDir);
        WorkspaceMergeIndexEntry mapping;
        if (index.Entries.TryGetValue(originKey, out var existingMapping) &&
            Directory.Exists(existingMapping.TargetProjectDir))
        {
            mapping = existingMapping;
            reused = true;
        }
        else
        {
            var sourceName = Path.GetFileName(TrimSeparators(sourceProjectDir));
            var targetProjectDir = ReserveUniqueDirectory(
                targetRoot,
                sourceName,
                sourceTag,
                knownTargetProjects);
            var workflowInsideSource = IsSameOrChildPath(sourceProjectDir, sourceWorkflowDir);
            string targetWorkflowDir;
            if (workflowInsideSource)
            {
                targetWorkflowDir = Path.Combine(
                    targetProjectDir,
                    Path.GetRelativePath(sourceProjectDir, sourceWorkflowDir));
            }
            else
            {
                var workflowRoot = Path.Combine(targetRoot, "workflow");
                Directory.CreateDirectory(workflowRoot);
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targetWorkflowDir = ReserveUniqueDirectory(
                    workflowRoot,
                    Path.GetFileName(TrimSeparators(sourceWorkflowDir)),
                    sourceTag,
                    reserved);
            }

            createdDirectories.Add(targetProjectDir);
            CopyDirectoryVerified(sourceProjectDir, targetProjectDir, cancellationToken);
            if (!workflowInsideSource && Directory.Exists(sourceWorkflowDir))
            {
                createdDirectories.Add(targetWorkflowDir);
                CopyDirectoryVerified(sourceWorkflowDir, targetWorkflowDir, cancellationToken);
            }

            ProjectWorkspaceService.UpdateMovedWorkspaceMetadata(targetProjectDir, targetWorkflowDir);
            RewriteJsonFiles(
                new[] { targetProjectDir, targetWorkflowDir },
                BuildPathMappings(sourceRoot, targetRoot, sourceProjectDir, targetProjectDir, sourceWorkflowDir, targetWorkflowDir),
                targetAccount,
                targetRoot);
            ApplyQueueMetadataToProjectFiles(targetProjectDir, targetWorkflowDir, sourceItem);

            mapping = new WorkspaceMergeIndexEntry
            {
                Kind = "project",
                SourceWorkspaceRoot = sourceRoot,
                SourcePath = sourceProjectDir,
                TargetProjectDir = targetProjectDir,
                TargetWorkflowDir = targetWorkflowDir,
            };
            index.Entries[originKey] = mapping;
            SaveMergeIndex(indexPath, index);
            reused = false;
        }

        var clonedItem = QueueProjectItem.FromPayload(sourceItem.ToPayload());
        clonedItem.ProjectDir = mapping.TargetProjectDir;
        clonedItem.AccountProfileId = targetAccount.Id;
        clonedItem.AccountProfileName = targetAccount.DisplayName;
        if (string.Equals(clonedItem.StatusText, QueueStepStatus.Running, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(clonedItem.CurrentStep) &&
                string.Equals(
                    clonedItem.StepStates.GetValueOrDefault(clonedItem.CurrentStep),
                    QueueStepStatus.Running,
                    StringComparison.Ordinal))
            {
                clonedItem.StepStates[clonedItem.CurrentStep] = QueueStepStatus.Stopped;
            }
            clonedItem.StatusText = QueueStepStatus.Stopped;
            clonedItem.CurrentStep = "";
        }
        ProjectWorkspaceService.RefreshQueueItemMetadata(clonedItem);
        clonedItem.DisplayName = FirstNonEmpty(sourceItem.DisplayName, clonedItem.DisplayName);
        clonedItem.OriginalTitle = FirstNonEmpty(sourceItem.OriginalTitle, clonedItem.OriginalTitle);
        clonedItem.NewTitle = FirstNonEmpty(sourceItem.NewTitle, clonedItem.NewTitle);
        clonedItem.Description = FirstNonEmpty(sourceItem.Description, clonedItem.Description);
        clonedItem.GenreCategory = FirstNonEmpty(sourceItem.GenreCategory, clonedItem.GenreCategory);
        if (sourceItem.EpisodeCount > 0)
            clonedItem.EpisodeCount = sourceItem.EpisodeCount;
        if (sourceItem.VideoVertical is 0 or 1)
            clonedItem.VideoVertical = sourceItem.VideoVertical;
        RewriteAndSaveProjectDocuments(
            sourceRoot,
            targetRoot,
            sourceProjectDir,
            mapping.TargetProjectDir,
            sourceWorkflowDir,
            mapping.TargetWorkflowDir,
            targetAccount);
        knownTargetProjects.Add(mapping.TargetProjectDir);
        return new ImportedActiveProject(clonedItem);
    }

    private static bool ImportArchiveProject(
        string sourceRoot,
        string targetRoot,
        string targetArchiveRoot,
        string sourceTag,
        ArchivedProjectItem sourceItem,
        TikTokAccountProfile targetAccount,
        WorkspaceMergeIndex index,
        string indexPath,
        ISet<string> reservedRestoreSourcePaths,
        ISet<string> reservedRestoreWorkflowPaths,
        ICollection<string> createdDirectories,
        ICollection<string> createdFiles,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var originPath = FirstNonEmpty(
            sourceItem.MetadataPath,
            sourceItem.ArchivedSourceDir,
            sourceItem.ArchivedWorkflowDir,
            sourceItem.ProjectKey);
        var originKey = StableOriginKey("archive", sourceRoot, originPath);
        if (index.Entries.TryGetValue(originKey, out var existingMapping) &&
            File.Exists(existingMapping.TargetMetadataPath))
        {
            return true;
        }

        var targetArchiveSourceRoot = Path.Combine(targetArchiveRoot, "source");
        var targetArchiveWorkflowRoot = Path.Combine(targetArchiveRoot, "workflow");
        var targetArchiveMetaRoot = Path.Combine(targetArchiveRoot, "meta");
        Directory.CreateDirectory(targetArchiveSourceRoot);
        Directory.CreateDirectory(targetArchiveWorkflowRoot);
        Directory.CreateDirectory(targetArchiveMetaRoot);

        var sourceLeaf = FirstNonEmpty(
            PathLeaf(sourceItem.ArchivedSourceDir),
            PathLeaf(sourceItem.SourceProjectDir),
            sourceItem.ProjectKey,
            "归档项目");
        var workflowLeaf = FirstNonEmpty(
            PathLeaf(sourceItem.ArchivedWorkflowDir),
            PathLeaf(sourceItem.WorkflowProjectDir),
            sourceLeaf);
        var targetArchivedSourceDir = "";
        var targetArchivedWorkflowDir = "";

        if (!string.IsNullOrWhiteSpace(sourceItem.ArchivedSourceDir) &&
            Directory.Exists(sourceItem.ArchivedSourceDir))
        {
            targetArchivedSourceDir = ReserveUniqueDirectory(
                targetArchiveSourceRoot,
                sourceLeaf,
                sourceTag,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            createdDirectories.Add(targetArchivedSourceDir);
            CopyDirectoryVerified(sourceItem.ArchivedSourceDir, targetArchivedSourceDir, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(sourceItem.ArchivedSourceDir))
        {
            warnings.Add($"归档 Source 缺失：{sourceItem.DisplayName}（{sourceItem.ArchivedSourceDir}）");
        }

        if (!string.IsNullOrWhiteSpace(sourceItem.ArchivedWorkflowDir) &&
            Directory.Exists(sourceItem.ArchivedWorkflowDir))
        {
            targetArchivedWorkflowDir = ReserveUniqueDirectory(
                targetArchiveWorkflowRoot,
                workflowLeaf,
                sourceTag,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            createdDirectories.Add(targetArchivedWorkflowDir);
            CopyDirectoryVerified(sourceItem.ArchivedWorkflowDir, targetArchivedWorkflowDir, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(sourceItem.ArchivedWorkflowDir))
        {
            warnings.Add($"归档 Workflow 缺失：{sourceItem.DisplayName}（{sourceItem.ArchivedWorkflowDir}）");
        }

        var restoreSourceDir = ReserveRestorePath(
            targetRoot,
            PathLeaf(sourceItem.SourceProjectDir, sourceLeaf),
            sourceTag,
            reservedRestoreSourcePaths);
        var restoreWorkflowDir = ReserveRestorePath(
            Path.Combine(targetRoot, "workflow"),
            PathLeaf(sourceItem.WorkflowProjectDir, workflowLeaf),
            sourceTag,
            reservedRestoreWorkflowPaths);
        var metadataStem = SanitizePathPart(FirstNonEmpty(sourceItem.ProjectKey, sourceLeaf, "archive"));
        var targetMetadataPath = ReserveUniqueFile(targetArchiveMetaRoot, metadataStem, ".json", sourceTag);
        var payload = ReadMetadata(sourceItem.MetadataPath);
        var mappings = BuildPathMappings(
            sourceRoot,
            targetRoot,
            sourceItem.SourceProjectDir,
            restoreSourceDir,
            sourceItem.WorkflowProjectDir,
            restoreWorkflowDir,
            sourceItem.ArchivedSourceDir,
            targetArchivedSourceDir,
            sourceItem.ArchivedWorkflowDir,
            targetArchivedWorkflowDir);
        payload = RewriteDictionary(payload, mappings);
        ApplyArchiveMetadata(
            payload,
            sourceItem,
            targetAccount,
            restoreSourceDir,
            restoreWorkflowDir,
            targetArchivedSourceDir,
            targetArchivedWorkflowDir);
        File.WriteAllText(targetMetadataPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        createdFiles.Add(targetMetadataPath);

        index.Entries[originKey] = new WorkspaceMergeIndexEntry
        {
            Kind = "archive",
            SourceWorkspaceRoot = sourceRoot,
            SourcePath = originPath,
            TargetMetadataPath = targetMetadataPath,
            TargetProjectDir = restoreSourceDir,
            TargetWorkflowDir = restoreWorkflowDir,
            TargetArchivedSourceDir = targetArchivedSourceDir,
            TargetArchivedWorkflowDir = targetArchivedWorkflowDir,
        };
        SaveMergeIndex(indexPath, index);
        return false;
    }

    private static void RewriteAndSaveProjectDocuments(
        string sourceRoot,
        string targetRoot,
        string sourceProjectDir,
        string targetProjectDir,
        string sourceWorkflowDir,
        string targetWorkflowDir,
        TikTokAccountProfile targetAccount)
    {
        var mappings = BuildPathMappings(
            sourceRoot,
            targetRoot,
            sourceProjectDir,
            targetProjectDir,
            sourceWorkflowDir,
            targetWorkflowDir);
        var documents = ProjectStateDocumentStore.LoadProjectDocuments(sourceRoot, sourceProjectDir);
        foreach (var (documentType, payload) in documents)
        {
            var rewritten = RewriteDictionary(payload, mappings);
            ApplyAccountFields(rewritten, targetAccount, targetRoot);
            ProjectStateDocumentStore.SaveDocument(
                targetRoot,
                targetProjectDir,
                documentType,
                rewritten,
                targetWorkflowDir);
        }
    }

    private static void ApplyArchiveMetadata(
        Dictionary<string, object?> payload,
        ArchivedProjectItem sourceItem,
        TikTokAccountProfile targetAccount,
        string sourceProjectDir,
        string workflowProjectDir,
        string archivedSourceDir,
        string archivedWorkflowDir)
    {
        payload["projectKey"] = FirstNonEmpty(sourceItem.ProjectKey, PathLeaf(sourceProjectDir));
        payload["displayName"] = FirstNonEmpty(sourceItem.DisplayName, sourceItem.NewTitle, sourceItem.OriginalTitle);
        payload["originalTitle"] = sourceItem.OriginalTitle;
        payload["newTitle"] = sourceItem.NewTitle;
        payload["archiveSource"] = FirstNonEmpty(sourceItem.ArchiveSource, "tiktok");
        payload["archivedAt"] = sourceItem.ArchivedAt;
        payload["queuedAt"] = sourceItem.QueuedAt;
        payload["queued_at"] = sourceItem.QueuedAt;
        payload["uploadCompletedAt"] = sourceItem.UploadCompletedAt;
        payload["upload_completed_at"] = sourceItem.UploadCompletedAt;
        payload["sourceProjectDir"] = sourceProjectDir;
        payload["workflowProjectDir"] = workflowProjectDir;
        payload["archivedSourceDir"] = archivedSourceDir;
        payload["archivedWorkflowDir"] = archivedWorkflowDir;
        payload["accountProfileId"] = targetAccount.Id;
        payload["account_profile_id"] = targetAccount.Id;
        payload["accountProfileName"] = targetAccount.DisplayName;
        payload["account_profile_name"] = targetAccount.DisplayName;

        if (payload.TryGetValue("queueProjectState", out var state) &&
            state is Dictionary<string, object?> stateDictionary)
        {
            stateDictionary["project_dir"] = sourceProjectDir;
            stateDictionary["account_profile_id"] = targetAccount.Id;
            stateDictionary["account_profile_name"] = targetAccount.DisplayName;
        }
    }

    private static void RewriteJsonFiles(
        IEnumerable<string> roots,
        IReadOnlyList<(string From, string To)> mappings,
        TikTokAccountProfile targetAccount,
        string targetRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                if (!seen.Add(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 16 * 1024 * 1024) continue;
                    var payload = ReadMetadata(path);
                    if (payload.Count == 0) continue;
                    payload = RewriteDictionary(payload, mappings);
                    ApplyAccountFields(payload, targetAccount, targetRoot);
                    File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
                }
                catch
                {
                    // A non-standard JSON sidecar must not block an otherwise valid workspace merge.
                }
            }
        }
    }

    private static void ApplyQueueMetadataToProjectFiles(
        string targetProjectDir,
        string targetWorkflowDir,
        QueueProjectItem sourceItem)
    {
        var sourceInfoPath = Path.Combine(targetProjectDir, "短剧信息.txt");
        var workflowInfoPath = Path.Combine(targetWorkflowDir, "短剧信息.txt");
        if (File.Exists(sourceInfoPath))
        {
            if (!string.IsNullOrWhiteSpace(sourceItem.OriginalTitle))
                ProjectWorkspaceService.UpdateProjectInfoField(
                    sourceInfoPath,
                    "原剧名",
                    sourceItem.OriginalTitle);
            if (!string.IsNullOrWhiteSpace(sourceItem.NewTitle))
                ProjectWorkspaceService.UpdateProjectInfoField(
                    sourceInfoPath,
                    "新剧名",
                    sourceItem.NewTitle);
        }
        if (File.Exists(workflowInfoPath) && !string.IsNullOrWhiteSpace(sourceItem.NewTitle))
        {
            ProjectWorkspaceService.UpdateProjectInfoField(
                workflowInfoPath,
                "新剧名",
                sourceItem.NewTitle);
        }

        foreach (var directory in new[] { targetProjectDir, targetWorkflowDir }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var metadataPath = Path.Combine(directory, "shortdrama-project.json");
            if (!File.Exists(metadataPath))
                continue;
            var payload = ReadMetadata(metadataPath);
            if (payload.Count == 0)
                continue;
            if (!string.IsNullOrWhiteSpace(sourceItem.DisplayName))
                payload["displayName"] = sourceItem.DisplayName;
            if (!string.IsNullOrWhiteSpace(sourceItem.OriginalTitle))
                payload["originalTitle"] = sourceItem.OriginalTitle;
            if (!string.IsNullOrWhiteSpace(sourceItem.NewTitle))
            {
                payload["newTitle"] = sourceItem.NewTitle;
                payload["new_title"] = sourceItem.NewTitle;
            }
            if (!string.IsNullOrWhiteSpace(sourceItem.Description))
                payload["description"] = sourceItem.Description;
            if (!string.IsNullOrWhiteSpace(sourceItem.GenreCategory))
                payload["category"] = sourceItem.GenreCategory;
            if (sourceItem.EpisodeCount > 0)
                payload["episodeCount"] = sourceItem.EpisodeCount;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        }
    }

    private static void ApplyAccountFields(
        Dictionary<string, object?> payload,
        TikTokAccountProfile targetAccount,
        string targetRoot)
    {
        RewriteKnownAccountFields(payload, targetAccount);
        if (payload.TryGetValue("publish_config", out var publishConfig) &&
            publishConfig is Dictionary<string, object?> config)
        {
            config["upload_profile_path"] = targetRoot;
            if (!string.IsNullOrWhiteSpace(targetAccount.TiktokSeriesUrl))
                config["series_url"] = targetAccount.TiktokSeriesUrl;
        }
        if (payload.ContainsKey("series_url") &&
            !string.IsNullOrWhiteSpace(targetAccount.TiktokSeriesUrl))
        {
            payload["series_url"] = targetAccount.TiktokSeriesUrl;
        }
    }

    private static void RewriteKnownAccountFields(
        Dictionary<string, object?> payload,
        TikTokAccountProfile targetAccount)
    {
        foreach (var key in payload.Keys.ToArray())
        {
            if (key is "account_profile_id" or "accountProfileId")
                payload[key] = targetAccount.Id;
            else if (key is "account_profile_name" or "accountProfileName")
                payload[key] = targetAccount.DisplayName;
            else if (payload[key] is Dictionary<string, object?> child)
                RewriteKnownAccountFields(child, targetAccount);
            else if (payload[key] is IEnumerable<object?> values)
            {
                foreach (var value in values.OfType<Dictionary<string, object?>>())
                    RewriteKnownAccountFields(value, targetAccount);
            }
        }
    }

    private static IReadOnlyList<(string From, string To)> BuildPathMappings(
        params string[] values)
    {
        var mappings = new List<(string From, string To)>();
        for (var index = 0; index + 1 < values.Length; index += 2)
        {
            var from = values[index];
            var to = values[index + 1];
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                continue;
            try
            {
                var normalizedFrom = Path.GetFullPath(from);
                var normalizedTo = Path.GetFullPath(to);
                if (!string.Equals(normalizedFrom, normalizedTo, StringComparison.OrdinalIgnoreCase))
                    mappings.Add((normalizedFrom, normalizedTo));
            }
            catch
            {
                // Invalid historical paths are ignored; explicit metadata fields are still replaced.
            }
        }

        return mappings
            .OrderByDescending(mapping => mapping.From.Length)
            .ToArray();
    }

    private static Dictionary<string, object?> RewriteDictionary(
        Dictionary<string, object?> payload,
        IReadOnlyList<(string From, string To)> mappings)
    {
        return payload.ToDictionary(
            pair => pair.Key,
            pair => RewriteValue(pair.Value, mappings),
            StringComparer.Ordinal);
    }

    private static object? RewriteValue(
        object? value,
        IReadOnlyList<(string From, string To)> mappings)
    {
        return value switch
        {
            string text => RewritePath(text, mappings),
            Dictionary<string, object?> dictionary => RewriteDictionary(dictionary, mappings),
            IEnumerable<object?> values => values.Select(item => RewriteValue(item, mappings)).ToList(),
            JsonElement element => RewriteValue(JsonElementToObject(element), mappings),
            _ => value,
        };
    }

    private static string RewritePath(
        string text,
        IReadOnlyList<(string From, string To)> mappings)
    {
        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text))
            return text;
        try
        {
            var fullPath = Path.GetFullPath(text);
            foreach (var mapping in mappings)
            {
                if (!IsSameOrChildPath(mapping.From, fullPath))
                    continue;
                if (IsSamePath(mapping.From, fullPath))
                    return mapping.To;
                return Path.GetFullPath(Path.Combine(mapping.To, Path.GetRelativePath(mapping.From, fullPath)));
            }
        }
        catch
        {
            return text;
        }

        return text;
    }

    private static void CopyDirectoryVerified(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"来源目录不存在：{source}");
        if (Directory.Exists(target))
            throw new IOException($"目标目录已存在：{target}");

        Directory.CreateDirectory(target);
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((source, target));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var childTarget = Path.Combine(current.Target, info.Name);
                Directory.CreateDirectory(childTarget);
                pending.Push((directory, childTarget));
            }

            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFile = Path.Combine(current.Target, Path.GetFileName(file));
                File.Copy(file, targetFile, overwrite: false);
            }
        }

        var sourceFingerprint = DirectoryFingerprint(source);
        var targetFingerprint = DirectoryFingerprint(target);
        if (sourceFingerprint != targetFingerprint)
            throw new IOException($"目录复制校验失败：{source} -> {target}");
    }

    private static (long Files, long Bytes) DirectoryFingerprint(string root)
    {
        long files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
        }

        return (files, bytes);
    }

    private static string ReserveUniqueDirectory(
        string parent,
        string desiredName,
        string suffix,
        ISet<string> reserved)
    {
        Directory.CreateDirectory(parent);
        var safeName = SanitizePathPart(FirstNonEmpty(desiredName, "项目"));
        var candidate = Path.Combine(parent, safeName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate) && reserved.Add(candidate))
            return candidate;

        var safeSuffix = SanitizePathPart(FirstNonEmpty(suffix, "来源目录"));
        candidate = Path.Combine(parent, $"{safeName}-{safeSuffix}");
        var number = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate) || !reserved.Add(candidate))
            candidate = Path.Combine(parent, $"{safeName}-{safeSuffix}-{number++}");
        return candidate;
    }

    private static string ReserveRestorePath(
        string parent,
        string desiredName,
        string suffix,
        ISet<string> reserved)
    {
        Directory.CreateDirectory(parent);
        var safeName = SanitizePathPart(FirstNonEmpty(desiredName, "项目"));
        var candidate = Path.Combine(parent, safeName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate) && reserved.Add(candidate))
            return candidate;
        var safeSuffix = SanitizePathPart(FirstNonEmpty(suffix, "来源目录"));
        candidate = Path.Combine(parent, $"{safeName}-{safeSuffix}");
        var number = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate) || !reserved.Add(candidate))
            candidate = Path.Combine(parent, $"{safeName}-{safeSuffix}-{number++}");
        return candidate;
    }

    private static string ReserveUniqueFile(
        string parent,
        string desiredStem,
        string extension,
        string suffix)
    {
        var stem = SanitizePathPart(FirstNonEmpty(desiredStem, "archive"));
        var candidate = Path.Combine(parent, stem + extension);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;
        var safeSuffix = SanitizePathPart(FirstNonEmpty(suffix, "来源目录"));
        candidate = Path.Combine(parent, $"{stem}-{safeSuffix}{extension}");
        var number = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(parent, $"{stem}-{safeSuffix}-{number++}{extension}");
        return candidate;
    }

    private static Dictionary<string, object?> ReadMetadata(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => JsonElementToObject(property.Value),
                    StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonElementToObject(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };

    private static string BackupTargetDatabase(string targetRoot, string stateRoot)
    {
        var databasePath = WorkspaceQueuePaths.QueueDatabasePath(targetRoot);
        if (!File.Exists(databasePath))
            return "";
        CheckpointDatabase(databasePath);
        var backupRoot = Path.Combine(stateRoot, "backups");
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(
            backupRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(databasePath)}");
        File.Copy(databasePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void RestoreTargetDatabase(string targetRoot, string backupPath)
    {
        try
        {
            var databasePath = WorkspaceQueuePaths.QueueDatabasePath(targetRoot);
            DeleteIfExists(databasePath + "-wal");
            DeleteIfExists(databasePath + "-shm");
            if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                File.Copy(backupPath, databasePath, overwrite: true);
            else
                DeleteIfExists(databasePath);
        }
        catch
        {
            // Keep the original merge error. The backup path is retained for manual recovery.
        }
    }

    private static void CheckpointDatabase(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(FULL);";
            command.ExecuteNonQuery();
        }
        catch
        {
            // A best-effort backup is still preferable to skipping the merge entirely.
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static FileStream AcquireWorkspaceLock(string stateRoot)
    {
        var path = Path.Combine(stateRoot, "merge.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("当前工作目录已有合并任务正在执行。", ex);
        }
    }

    private static WorkspaceMergeIndex LoadMergeIndex(string path)
    {
        try
        {
            if (!File.Exists(path)) return new WorkspaceMergeIndex();
            return JsonSerializer.Deserialize<WorkspaceMergeIndex>(File.ReadAllText(path), JsonOptions)
                   ?? new WorkspaceMergeIndex();
        }
        catch
        {
            return new WorkspaceMergeIndex();
        }
    }

    private static void SaveMergeIndex(string path, WorkspaceMergeIndex index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string StableOriginKey(string kind, string sourceRoot, string path)
    {
        var value = $"{kind}|{SafeFullPath(sourceRoot)}|{SafeFullPath(path)}"
            .Replace('\\', '/')
            .ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeSources(
        string targetRoot,
        IEnumerable<string> sourceWorkspaceRoots)
    {
        var sources = (sourceWorkspaceRoots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("请至少选择一个来源工作目录。");

        foreach (var source in sources)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"来源工作目录不存在：{source}");
            if (IsSamePath(source, targetRoot))
                throw new InvalidOperationException("来源工作目录不能与当前工作目录相同。");
            if (IsSameOrChildPath(source, targetRoot) || IsSameOrChildPath(targetRoot, source))
                throw new InvalidOperationException($"来源与当前工作目录不能互相包含：{source}");
        }

        for (var left = 0; left < sources.Length; left++)
        {
            for (var right = left + 1; right < sources.Length; right++)
            {
                if (IsSameOrChildPath(sources[left], sources[right]) ||
                    IsSameOrChildPath(sources[right], sources[left]))
                {
                    throw new InvalidOperationException(
                        $"来源工作目录不能互相包含：{sources[left]} 与 {sources[right]}");
                }
            }
        }

        return sources;
    }

    private static string ValidateTarget(string targetWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(targetWorkspaceRoot))
            throw new InvalidOperationException("请先选择当前工作目录。");
        var targetRoot = Path.GetFullPath(targetWorkspaceRoot.Trim());
        if (!Directory.Exists(targetRoot))
            throw new DirectoryNotFoundException($"当前工作目录不存在：{targetRoot}");
        return targetRoot;
    }

    private static IReadOnlyList<QueueProjectItem> LoadSourceProjects(string workspaceRoot)
    {
        var persisted = WorkspaceQueueDatabase.Load(workspaceRoot).Items
            .Where(item =>
                !item.Archived &&
                !string.IsNullOrWhiteSpace(item.ProjectDir) &&
                WorkspaceProjectScanner.IsValidProjectDirectory(item.ProjectDir))
            .ToList();
        var known = persisted
            .Select(item => SafeFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scanned in WorkspaceQueueService.ScanProjects(workspaceRoot))
        {
            if (string.IsNullOrWhiteSpace(scanned.ProjectDir) ||
                !known.Add(SafeFullPath(scanned.ProjectDir)))
            {
                continue;
            }
            persisted.Add(scanned);
        }

        return persisted;
    }

    private static void RollBackCreatedFiles(
        string targetRoot,
        string targetArchiveRoot,
        IEnumerable<string> createdFiles,
        IEnumerable<string> createdDirectories)
    {
        foreach (var path in createdFiles.Reverse())
        {
            try
            {
                if (IsAllowedMergeTarget(targetRoot, targetArchiveRoot, path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort rollback.
            }
        }

        foreach (var path in createdDirectories.Reverse())
        {
            try
            {
                if (IsAllowedMergeTarget(targetRoot, targetArchiveRoot, path) && Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best effort rollback.
            }
        }
    }

    private static bool IsAllowedMergeTarget(string targetRoot, string targetArchiveRoot, string path) =>
        IsSameOrChildPath(targetRoot, path) || IsSameOrChildPath(targetArchiveRoot, path);

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((value ?? "").Trim().Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        return cleaned.Trim().TrimEnd('.');
    }

    private static string PathLeaf(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return FirstNonEmpty(Path.GetFileName(TrimSeparators(value)), fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(SafeFullPath(left), SafeFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChildPath(string parent, string child)
    {
        var parentFull = SafeFullPath(parent);
        var childFull = SafeFullPath(child);
        if (string.Equals(parentFull, childFull, StringComparison.OrdinalIgnoreCase))
            return true;
        var relative = Path.GetRelativePath(parentFull, childFull);
        return !string.IsNullOrWhiteSpace(relative) &&
               !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath((path ?? "").Trim()); }
        catch { return (path ?? "").Trim(); }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private sealed record ImportedActiveProject(QueueProjectItem Item);

    private sealed class WorkspaceMergeIndex
    {
        public Dictionary<string, WorkspaceMergeIndexEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class WorkspaceMergeIndexEntry
    {
        public string Kind { get; set; } = "";
        public string SourceWorkspaceRoot { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string TargetProjectDir { get; set; } = "";
        public string TargetWorkflowDir { get; set; } = "";
        public string TargetMetadataPath { get; set; } = "";
        public string TargetArchivedSourceDir { get; set; } = "";
        public string TargetArchivedWorkflowDir { get; set; } = "";
    }
}
