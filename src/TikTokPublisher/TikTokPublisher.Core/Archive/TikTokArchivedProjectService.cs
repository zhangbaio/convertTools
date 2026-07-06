using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Archive;

public sealed record ArchivedProjectItem(
    string ProjectKey,
    string DisplayName,
    string OriginalTitle,
    string NewTitle,
    string ArchivedAt,
    string QueuedAt,
    string MetadataPath,
    string ArchiveProjectDir,
    string ArchiveSource,
    string ArchivedSourceDir,
    string ArchivedWorkflowDir,
    string AccountProfileId = "",
    string AccountProfileName = "");

public static class TikTokArchivedProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
        ".aria2", ".part", ".partial", ".download", ".crdownload", ".tmp",
    };

    private static readonly HashSet<string> PreservableVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly string[] WorkflowVideoDirs = { "videos", "tiktok_upload_videos" };
    private static readonly string[] OriginalTitleKeys = { "原剧名", "原剧名称", "原始剧名", "原剧名名称", "剧名", "标题" };
    private static readonly string[] NewTitleKeys = { "新剧名", "新剧名称", "剧名", "标题" };

    public static string ResolveArchiveRoot(string workspaceRoot, string? archiveRootDir = null)
    {
        var custom = (archiveRootDir ?? ClientSettingsStore.Load().ArchiveRootDir ?? "").Trim();
        if (custom.Length > 0)
            return Path.GetFullPath(custom);
        return Path.Combine(Path.GetFullPath(workspaceRoot), "archive");
    }

    public static string ResolveArchiveRootForDisplay(string workspaceRoot, string? archiveRootDir = null) =>
        ResolveArchiveRoot(workspaceRoot, archiveRootDir);

    public static IReadOnlyList<ArchivedProjectItem> List(string workspaceRoot, string? archiveRootDir = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<ArchivedProjectItem>();

        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        var items = new List<ArchivedProjectItem>();
        if (Directory.Exists(archiveRoot))
        {
            items.AddRange(ListMetaLayout(archiveRoot));
            items.AddRange(ListLegacyProjectLayout(archiveRoot, items));
        }

        if (items.Count > 0)
        {
            var sorted = SortItems(BackfillQueuedAtFromQueueState(workspaceRoot, items));
            SaveArchiveProjectsToDatabase(workspaceRoot, sorted);
            return sorted;
        }

        return SortItems(BackfillQueuedAtFromQueueState(workspaceRoot, LoadArchiveProjectsFromDatabase(workspaceRoot)));
    }

    public static async Task ArchiveQueueProjectAsync(
        string workspaceRoot,
        string projectDir,
        string? archiveRootDir = null,
        IEnumerable<int>? preserveWorkflowEpisodes = null,
        bool deleteSourceVideos = true,
        bool deleteWorkflowVideos = true,
        bool deleteMaterialVideos = true,
        string source = "tiktok",
        TikTokAccountProfile? account = null,
        string? queuedAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"工作目录不存在：{workspaceRoot}");
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            throw new DirectoryNotFoundException($"项目目录不存在：{projectDir}");

        var context = ProjectWorkspaceService.LoadContext(projectDir);
        var sourceProjectDir = Path.GetFullPath(context.SourceProjectDir);
        var workflowProjectDir = Path.GetFullPath(context.WorkflowProjectDir);
        var queuedAtValue = FirstNonEmpty(
            queuedAt,
            ResolveQueuedAtFromQueueState(workspaceRoot, sourceProjectDir, workflowProjectDir, projectDir));
        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        var sourceRoot = Path.Combine(archiveRoot, "source");
        var workflowRoot = Path.Combine(archiveRoot, "workflow");
        var metaRoot = Path.Combine(archiveRoot, "meta");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(workflowRoot);
        Directory.CreateDirectory(metaRoot);

        var projectKey = Path.GetFileName(sourceProjectDir);
        string archivedSourceDir = "";
        string archivedWorkflowDir = "";
        var preserveEpisodes = (preserveWorkflowEpisodes ?? Array.Empty<int>())
            .Where(value => value > 0)
            .ToHashSet();
        var deletedSourceVideoCount = 0;
        var deletedWorkflowVideoCount = 0;
        var deletedMaterialVideoCount = 0;
        var deletedMaterialClipVideoCount = 0;
        var preservedVideoCount = 0;
        var sourceVideosPrunedBeforeMove = false;
        var sourceMaterialPrunedBeforeMove = false;
        var workflowVideosPrunedBeforeMove = false;
        var workflowMaterialPrunedBeforeMove = false;

        if (Directory.Exists(sourceProjectDir))
        {
            archivedSourceDir = BuildArchiveTargetDir(sourceRoot, Path.GetFileName(sourceProjectDir));
            var sourceCrossVolume = IsCrossVolumeMove(sourceProjectDir, archivedSourceDir);
            if (sourceCrossVolume && deleteSourceVideos)
            {
                deletedSourceVideoCount = DeleteVideoFilesRecursive(sourceProjectDir);
                sourceVideosPrunedBeforeMove = true;
            }

            if (sourceCrossVolume && deleteMaterialVideos)
            {
                deletedMaterialClipVideoCount += DeleteMaterialClipVideoFiles(sourceProjectDir);
                sourceMaterialPrunedBeforeMove = true;
            }

            MoveDirectory(sourceProjectDir, archivedSourceDir);
        }

        if (Directory.Exists(workflowProjectDir) &&
            !string.Equals(workflowProjectDir, sourceProjectDir, StringComparison.OrdinalIgnoreCase))
        {
            archivedWorkflowDir = BuildArchiveTargetDir(workflowRoot, Path.GetFileName(workflowProjectDir));
            var workflowCrossVolume = IsCrossVolumeMove(workflowProjectDir, archivedWorkflowDir);
            if (workflowCrossVolume && deleteWorkflowVideos)
            {
                (deletedWorkflowVideoCount, preservedVideoCount) =
                    DeleteWorkflowVideoFiles(workflowProjectDir, preserveEpisodes);
                workflowVideosPrunedBeforeMove = true;
            }

            if (workflowCrossVolume && deleteMaterialVideos)
            {
                deletedMaterialVideoCount = DeleteMaterialVideoFiles(workflowProjectDir);
                deletedMaterialClipVideoCount += DeleteMaterialClipVideoFiles(workflowProjectDir);
                workflowMaterialPrunedBeforeMove = true;
            }

            MoveDirectory(workflowProjectDir, archivedWorkflowDir);
        }

        if (deleteSourceVideos &&
            !sourceVideosPrunedBeforeMove &&
            !string.IsNullOrWhiteSpace(archivedSourceDir) &&
            Directory.Exists(archivedSourceDir))
        {
            deletedSourceVideoCount = DeleteVideoFilesRecursive(archivedSourceDir);
        }

        if (!string.IsNullOrWhiteSpace(archivedWorkflowDir) && Directory.Exists(archivedWorkflowDir))
        {
            if (deleteWorkflowVideos && !workflowVideosPrunedBeforeMove)
            {
                (deletedWorkflowVideoCount, preservedVideoCount) =
                    DeleteWorkflowVideoFiles(archivedWorkflowDir, preserveEpisodes);
            }

            if (deleteMaterialVideos && !workflowMaterialPrunedBeforeMove)
            {
                deletedMaterialVideoCount = DeleteMaterialVideoFiles(archivedWorkflowDir);
                deletedMaterialClipVideoCount += DeleteMaterialClipVideoFiles(archivedWorkflowDir);
            }
        }

        if (deleteMaterialVideos &&
            !sourceMaterialPrunedBeforeMove &&
            !string.IsNullOrWhiteSpace(archivedSourceDir) &&
            Directory.Exists(archivedSourceDir))
        {
            deletedMaterialClipVideoCount += DeleteMaterialClipVideoFiles(archivedSourceDir);
        }

        ct.ThrowIfCancellationRequested();
        var sourceInfo = ReadInfo(archivedSourceDir);
        var workflowInfo = ReadInfo(archivedWorkflowDir);
        var originalTitle = FirstNonEmpty(
            Pick(sourceInfo, OriginalTitleKeys),
            Pick(workflowInfo, OriginalTitleKeys),
            projectKey);
        var newTitle = FirstNonEmpty(
            Pick(workflowInfo, NewTitleKeys),
            Pick(sourceInfo, NewTitleKeys),
            Path.GetFileName(workflowProjectDir).TrimStart('_'),
            projectKey);
        var metadataPath = BuildArchiveMetadataPath(metaRoot, projectKey);
        var accountProfileId = (account?.Id ?? "").Trim();
        var accountProfileName = FirstNonEmpty(account?.DisplayName, account?.Name);
        var metadata = new Dictionary<string, object?>
        {
            ["projectKey"] = projectKey,
            ["displayName"] = projectKey,
            ["originalTitle"] = originalTitle,
            ["newTitle"] = newTitle,
            ["accountProfileId"] = accountProfileId,
            ["account_profile_id"] = accountProfileId,
            ["accountProfileName"] = accountProfileName,
            ["account_profile_name"] = accountProfileName,
            ["archiveSource"] = source.Trim(),
            ["archivedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["queuedAt"] = queuedAtValue,
            ["queued_at"] = queuedAtValue,
            ["sourceProjectDir"] = sourceProjectDir,
            ["workflowProjectDir"] = workflowProjectDir,
            ["archivedSourceDir"] = archivedSourceDir,
            ["archivedWorkflowDir"] = archivedWorkflowDir,
            ["deleteSourceVideos"] = deleteSourceVideos,
            ["deleteWorkflowVideos"] = deleteWorkflowVideos,
            ["deleteMaterialVideos"] = deleteMaterialVideos,
            ["deletedVideoFileCount"] =
                deletedSourceVideoCount + deletedWorkflowVideoCount + deletedMaterialVideoCount + deletedMaterialClipVideoCount,
            ["preservedVideoFileCount"] = preservedVideoCount,
            ["deletedSourceVideoFileCount"] = deletedSourceVideoCount,
            ["deletedWorkflowVideoFileCount"] = deletedWorkflowVideoCount,
            ["deletedMaterialVideoFileCount"] = deletedMaterialVideoCount,
            ["deletedMaterialClipVideoFileCount"] = deletedMaterialClipVideoCount,
        };
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8,
            ct).ConfigureAwait(false);

        SaveArchiveProjectsToDatabase(workspaceRoot, List(workspaceRoot, archiveRootDir));
    }

    public static void Restore(string workspaceRoot, string archiveProjectDir, string? archiveRootDir = null)
    {
        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        var archiveRef = EnsureArchiveMetadataReference(workspaceRoot, archiveProjectDir);
        var (payload, metadataPath, archiveDir) = LoadArchiveReference(archiveRef, archiveRoot);
        var sourceArchived = FirstNonEmpty(ReadString(payload, "archivedSourceDir"), Path.Combine(archiveDir, "source"));
        var workflowArchived = FirstNonEmpty(ReadString(payload, "archivedWorkflowDir"), Path.Combine(archiveDir, "workflow"));

        string? restoredSource = null;
        string? restoredWorkflow = null;
        if (!string.IsNullOrWhiteSpace(sourceArchived) && Directory.Exists(sourceArchived))
        {
            restoredSource = FirstNonEmpty(
                ReadString(payload, "sourceProjectDir"),
                Path.Combine(Path.GetFullPath(workspaceRoot), Path.GetFileName(sourceArchived)));
            if (Directory.Exists(restoredSource))
                throw new InvalidOperationException($"恢复目标 source 目录已存在：{restoredSource}");
            MoveDirectory(sourceArchived, restoredSource);
        }

        if (!string.IsNullOrWhiteSpace(workflowArchived) && Directory.Exists(workflowArchived))
        {
            restoredWorkflow = FirstNonEmpty(
                ReadString(payload, "workflowProjectDir"),
                Path.Combine(Path.GetFullPath(workspaceRoot), "workflow", Path.GetFileName(workflowArchived)));
            if (Directory.Exists(restoredWorkflow))
                throw new InvalidOperationException($"恢复目标 workflow 目录已存在：{restoredWorkflow}");
            MoveDirectory(workflowArchived, restoredWorkflow);
        }

        UpdateRestoredMetadata(restoredSource, restoredWorkflow);
        UpdateQueueStateForRestoredProject(workspaceRoot, restoredSource);
        CleanupArchiveReference(metadataPath, archiveDir);
        RemoveArchiveFromDatabase(workspaceRoot, metadataPath);
    }

    public static Task DeleteAsync(
        string workspaceRoot,
        string archiveProjectDir,
        string? archiveRootDir = null,
        CancellationToken ct = default)
    {
        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        var archiveRef = EnsureArchiveMetadataReference(workspaceRoot, archiveProjectDir);
        var (payload, metadataPath, archiveDir) = LoadArchiveReference(archiveRef, archiveRoot);
        ct.ThrowIfCancellationRequested();

        foreach (var key in new[] { "archivedSourceDir", "archivedWorkflowDir" })
        {
            var path = ReadString(payload, key);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;
            if (!IsWithin(path, archiveRoot))
                throw new InvalidOperationException($"拒绝删除归档根目录外路径：{path}");
            Directory.Delete(path, recursive: true);
        }

        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
            PruneEmptyParent(metadataPath, archiveRoot);
        }
        else if (Directory.Exists(archiveDir) && IsWithin(archiveDir, archiveRoot))
        {
            Directory.Delete(archiveDir, recursive: true);
        }

        RemoveArchiveFromDatabase(workspaceRoot, metadataPath);
        return Task.CompletedTask;
    }

    public static QueueProjectItem ToQueueItemForSync(ArchivedProjectItem item)
    {
        var episodeCount = ResolveArchivedEpisodeCount(item);
        return new QueueProjectItem
        {
            ProjectDir = FirstNonEmpty(item.ArchivedWorkflowDir, item.ArchivedSourceDir, item.MetadataPath),
            DisplayName = item.DisplayName,
            OriginalTitle = item.OriginalTitle,
            NewTitle = item.NewTitle,
            AccountProfileId = item.AccountProfileId,
            AccountProfileName = item.AccountProfileName,
            EpisodeCount = Math.Max(1, episodeCount),
            QueuedAt = NormalizeTime(FirstNonEmpty(item.QueuedAt, item.ArchivedAt)),
            StatusText = QueueStepStatus.Completed,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
    }

    private static IReadOnlyList<ArchivedProjectItem> ListMetaLayout(string archiveRoot)
    {
        var metadataRoot = Path.Combine(archiveRoot, "meta");
        if (!Directory.Exists(metadataRoot))
            return Array.Empty<ArchivedProjectItem>();

        var items = new List<ArchivedProjectItem>();
        foreach (var metadataPath in Directory.EnumerateFiles(metadataRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var payload = ReadJsonObject(metadataPath);
            var archiveSource = ReadString(payload, "archiveSource");
            if (!string.IsNullOrWhiteSpace(archiveSource) &&
                !string.Equals(archiveSource, "tiktok", StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(BuildItemFromPayload(payload, metadataPath, metadataPath));
        }

        return items;
    }

    private static IReadOnlyList<ArchivedProjectItem> ListLegacyProjectLayout(
        string archiveRoot,
        IReadOnlyCollection<ArchivedProjectItem> existing)
    {
        var seen = existing
            .Select(item => PathKey(item.MetadataPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<ArchivedProjectItem>();
        foreach (var dir in Directory.EnumerateDirectories(archiveRoot).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "source", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "workflow", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "meta", StringComparison.OrdinalIgnoreCase))
                continue;

            var metadataPath = Path.Combine(dir, "archive-meta.json");
            if (!File.Exists(metadataPath) || seen.Contains(PathKey(metadataPath)))
                continue;
            var payload = ReadJsonObject(metadataPath);
            items.Add(BuildItemFromPayload(payload, metadataPath, dir));
        }

        return items;
    }

    private static ArchivedProjectItem BuildItemFromPayload(
        Dictionary<string, object?> payload,
        string metadataPath,
        string archiveProjectDir)
    {
        var projectKey = FirstNonEmpty(
            ReadString(payload, "projectKey"),
            ReadString(payload, "project_key"),
            ReadString(payload, "ProjectKey"),
            Path.GetFileNameWithoutExtension(metadataPath));
        var sourceDir = FirstNonEmpty(
            ReadString(payload, "archivedSourceDir"),
            ReadString(payload, "archived_source_dir"),
            Path.Combine(archiveProjectDir, "source"));
        var workflowDir = FirstNonEmpty(
            ReadString(payload, "archivedWorkflowDir"),
            ReadString(payload, "archived_workflow_dir"),
            Path.Combine(archiveProjectDir, "workflow"));
        if (File.Exists(archiveProjectDir))
            archiveProjectDir = metadataPath;
        var sourceInfo = ReadInfo(sourceDir);
        var workflowInfo = ReadInfo(workflowDir);
        var originalTitle = FirstNonEmpty(
            ReadString(payload, "originalTitle"),
            ReadString(payload, "original_title"),
            ReadString(payload, "SourceName"),
            Pick(sourceInfo, OriginalTitleKeys),
            Pick(workflowInfo, OriginalTitleKeys),
            projectKey);
        var newTitle = FirstNonEmpty(
            ReadString(payload, "newTitle"),
            ReadString(payload, "new_title"),
            ReadString(payload, "DisplayName"),
            Pick(workflowInfo, NewTitleKeys),
            Pick(sourceInfo, NewTitleKeys),
            projectKey);

        return new ArchivedProjectItem(
            ProjectKey: projectKey,
            DisplayName: FirstNonEmpty(
                ReadString(payload, "displayName"),
                ReadString(payload, "display_name"),
                ReadString(payload, "DisplayName"),
                projectKey),
            OriginalTitle: originalTitle,
            NewTitle: newTitle,
            ArchivedAt: FirstNonEmpty(
                ReadString(payload, "archivedAt"),
                ReadString(payload, "archived_at"),
                ReadString(payload, "ArchivedAt")),
            QueuedAt: FirstNonEmpty(
                ReadString(payload, "queuedAt"),
                ReadString(payload, "queued_at"),
                ReadString(payload, "QueuedAt")),
            MetadataPath: metadataPath,
            ArchiveProjectDir: archiveProjectDir,
            ArchiveSource: FirstNonEmpty(
                ReadString(payload, "archiveSource"),
                ReadString(payload, "archive_source"),
                "tiktok"),
            ArchivedSourceDir: Directory.Exists(sourceDir) ? Path.GetFullPath(sourceDir) : sourceDir,
            ArchivedWorkflowDir: Directory.Exists(workflowDir) ? Path.GetFullPath(workflowDir) : workflowDir,
            AccountProfileId: FirstNonEmpty(
                ReadString(payload, "accountProfileId"),
                ReadString(payload, "account_profile_id")),
            AccountProfileName: FirstNonEmpty(
                ReadString(payload, "accountProfileName"),
                ReadString(payload, "account_profile_name")));
    }

    private static (Dictionary<string, object?> Payload, string MetadataPath, string ArchiveDir) LoadArchiveReference(
        string archiveRef,
        string archiveRoot)
    {
        var target = Path.GetFullPath(archiveRef);
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new FileNotFoundException($"归档项目不存在：{target}", target);
        if (!IsWithin(target, archiveRoot))
            throw new InvalidOperationException($"只允许操作 archive 目录下的归档项目：{target}");

        var metadataPath = File.Exists(target)
            ? target
            : Path.Combine(target, "archive-meta.json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("未找到归档元数据", metadataPath);
        var archiveDir = File.Exists(target) ? Path.GetDirectoryName(metadataPath)! : target;
        return (ReadJsonObject(metadataPath), metadataPath, archiveDir);
    }

    private static string EnsureArchiveMetadataReference(string workspaceRoot, string archiveRef)
    {
        var reference = Path.GetFullPath(archiveRef);
        if (File.Exists(reference) || Directory.Exists(reference))
            return reference;

        var payload = FindArchivePayloadForReference(workspaceRoot, reference);
        if (payload.Count == 0)
            return reference;

        var metadataPath = FirstNonEmpty(
            ReadString(payload, "metadata_path", "metadataPath"),
            reference);
        metadataPath = Path.GetFullPath(metadataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(ArchivePayloadToMetadata(payload, metadataPath), JsonOptions),
            Encoding.UTF8);
        return metadataPath;
    }

    private static Dictionary<string, object?> FindArchivePayloadForReference(string workspaceRoot, string archiveRef)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        if (!File.Exists(dbPath)) return new Dictionary<string, object?>(StringComparer.Ordinal);
        EnsureArchiveDatabase(dbPath);
        var referenceKey = PathKey(archiveRef);
        var referenceStem = Path.GetFileNameWithoutExtension(archiveRef);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT archive_id, metadata_path, payload_json FROM archive_projects";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var archiveId = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var metadataPath = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var payload = ReadPayloadJson(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
            var projectKey = ReadString(payload, "project_key", "projectKey");
            if (!string.IsNullOrWhiteSpace(metadataPath) && PathKey(metadataPath) == referenceKey)
                return payload;
            if (!string.IsNullOrWhiteSpace(referenceStem) &&
                (string.Equals(referenceStem, archiveId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(referenceStem, projectKey, StringComparison.OrdinalIgnoreCase)))
                return payload;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> ArchivePayloadToMetadata(
        IReadOnlyDictionary<string, object?> payload,
        string metadataPath)
    {
        var projectKey = FirstNonEmpty(
            ReadString(payload, "project_key", "projectKey"),
            Path.GetFileNameWithoutExtension(metadataPath));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectKey"] = projectKey,
            ["displayName"] = FirstNonEmpty(ReadString(payload, "display_name", "displayName"), projectKey),
            ["originalTitle"] = ReadString(payload, "original_title", "originalTitle"),
            ["newTitle"] = ReadString(payload, "new_title", "newTitle"),
            ["archiveSource"] = FirstNonEmpty(ReadString(payload, "archive_source", "archiveSource"), "tiktok"),
            ["archivedAt"] = ReadString(payload, "archived_at", "archivedAt"),
            ["queuedAt"] = ReadString(payload, "queued_at", "queuedAt"),
            ["queued_at"] = ReadString(payload, "queued_at", "queuedAt"),
            ["sourceProjectDir"] = ReadString(payload, "source_project_dir", "sourceProjectDir"),
            ["workflowProjectDir"] = ReadString(payload, "workflow_project_dir", "workflowProjectDir"),
            ["archivedSourceDir"] = ReadString(payload, "archived_source_dir", "archivedSourceDir"),
            ["archivedWorkflowDir"] = ReadString(payload, "archived_workflow_dir", "archivedWorkflowDir"),
        };
    }

    private static void CleanupArchiveReference(string metadataPath, string archiveDir)
    {
        if (File.Exists(metadataPath))
            File.Delete(metadataPath);

        if (Directory.Exists(archiveDir) &&
            string.Equals(Path.GetFileName(archiveDir), "meta", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(archiveDir) && !Directory.EnumerateFileSystemEntries(archiveDir).Any())
            Directory.Delete(archiveDir);
    }

    private static void UpdateQueueStateForRestoredProject(string workspaceRoot, string? restoredSourceDir)
    {
        if (string.IsNullOrWhiteSpace(restoredSourceDir) || !Directory.Exists(restoredSourceDir))
            return;

        var state = WorkspaceQueueDatabase.Load(workspaceRoot);
        var items = state.Items.ToList();
        var options = QueueRunOptions.FromDictionary(state.Options);
        var normalized = Path.GetFullPath(restoredSourceDir);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        QueueProjectItem? restoredItem = null;
        var remaining = new List<QueueProjectItem>();

        foreach (var item in items)
        {
            var projectDir = item.ProjectDir;
            var itemPath = string.IsNullOrWhiteSpace(projectDir) ? "" : Path.GetFullPath(projectDir);
            if (restoredItem is null &&
                string.Equals(itemPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                item.Archived = false;
                item.QueuedAt = timestamp;
                restoredItem = item;
                continue;
            }

            remaining.Add(item);
        }

        if (restoredItem is null)
        {
            var scanned = WorkspaceProjectScanner.BuildProject(normalized);
            restoredItem = new QueueProjectItem
            {
                ProjectDir = scanned.ProjectDir,
                DisplayName = scanned.DisplayName,
                OriginalTitle = scanned.OriginalTitle,
                NewTitle = scanned.NewTitle,
                Description = scanned.Description,
                GenreCategory = scanned.GenreCategory,
                EpisodeCount = scanned.EpisodeCount,
                PrimaryVideoPath = scanned.PrimaryVideoPath,
                CoverPath = scanned.CoverPath,
                Archived = false,
                Enabled = false,
                QueuedAt = timestamp,
            };
        }
        else
        {
            restoredItem.Archived = false;
            restoredItem.QueuedAt = timestamp;
        }

        remaining.Add(restoredItem);

        WorkspaceQueueService.SaveRunOptions(workspaceRoot, remaining, options);
    }

    private static void UpdateRestoredMetadata(string? restoredSourceDir, string? restoredWorkflowDir)
    {
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceProjectDir"] = restoredSourceDir ?? "",
            ["workflowProjectDir"] = restoredWorkflowDir ?? "",
            ["workflowDirName"] = string.IsNullOrWhiteSpace(restoredWorkflowDir) ? "" : Path.GetFileName(restoredWorkflowDir),
        };

        foreach (var metadataPath in new[]
                 {
                     string.IsNullOrWhiteSpace(restoredSourceDir) ? "" : Path.Combine(restoredSourceDir, "shortdrama-project.json"),
                     string.IsNullOrWhiteSpace(restoredWorkflowDir) ? "" : Path.Combine(restoredWorkflowDir, "shortdrama-project.json"),
                 })
        {
            if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath)) continue;
            Dictionary<string, object?> payload;
            try
            {
                payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(metadataPath), CompactJsonOptions)
                          ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            catch
            {
                payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            foreach (var (key, value) in updates)
            {
                if (!string.IsNullOrWhiteSpace(value?.ToString()))
                    payload[key] = value;
            }

            File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    private static IReadOnlyList<ArchivedProjectItem> BackfillQueuedAtFromQueueState(
        string workspaceRoot,
        IReadOnlyList<ArchivedProjectItem> items)
    {
        if (items.Count == 0 || items.All(item => !string.IsNullOrWhiteSpace(item.QueuedAt)))
            return items;

        var queuedAtByDir = LoadQueuedAtByProjectDir(workspaceRoot);
        if (queuedAtByDir.Count == 0)
            return items;

        var changed = false;
        var result = new List<ArchivedProjectItem>(items.Count);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.QueuedAt))
            {
                result.Add(item);
                continue;
            }

            var queuedAt = ResolveArchivedQueuedAt(item, workspaceRoot, queuedAtByDir);
            if (string.IsNullOrWhiteSpace(queuedAt))
            {
                result.Add(item);
                continue;
            }

            result.Add(item with { QueuedAt = queuedAt });
            changed = true;
        }

        return changed ? result : items;
    }

    private static IReadOnlyDictionary<string, string> LoadQueuedAtByProjectDir(string workspaceRoot)
    {
        try
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in WorkspaceQueueDatabase.Load(workspaceRoot).Items)
            {
                if (string.IsNullOrWhiteSpace(item.ProjectDir) || string.IsNullOrWhiteSpace(item.QueuedAt))
                    continue;
                result.TryAdd(PathKey(item.ProjectDir), item.QueuedAt.Trim());
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ResolveArchivedQueuedAt(
        ArchivedProjectItem item,
        string workspaceRoot,
        IReadOnlyDictionary<string, string> queuedAtByDir)
    {
        foreach (var candidate in EnumerateOriginalProjectDirCandidates(item, workspaceRoot))
        {
            if (queuedAtByDir.TryGetValue(PathKey(candidate), out var queuedAt))
                return queuedAt;
        }

        return "";
    }

    private static IEnumerable<string> EnumerateOriginalProjectDirCandidates(
        ArchivedProjectItem item,
        string workspaceRoot)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(item.MetadataPath) && File.Exists(item.MetadataPath))
            payload = ReadJsonObject(item.MetadataPath);

        foreach (var value in new[]
                 {
                     ReadString(payload, "sourceProjectDir", "source_project_dir"),
                     ReadString(payload, "workflowProjectDir", "workflow_project_dir"),
                     item.ArchivedSourceDir,
                     item.ArchivedWorkflowDir,
                     string.IsNullOrWhiteSpace(item.ProjectKey) ? "" : Path.Combine(workspaceRoot, item.ProjectKey),
                     string.IsNullOrWhiteSpace(item.ProjectKey) ? "" : Path.Combine(workspaceRoot, "workflow", item.ProjectKey),
                     string.IsNullOrWhiteSpace(item.ProjectKey) ? "" : Path.Combine(workspaceRoot, "workflow", "_" + item.ProjectKey),
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static IReadOnlyList<ArchivedProjectItem> SortItems(IEnumerable<ArchivedProjectItem> items) =>
        items
            .OrderByDescending(item => ParseSortTime(item.ArchivedAt, item.MetadataPath))
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DateTimeOffset ParseSortTime(string value, string metadataPath)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
            return parsed;
        try
        {
            if (File.Exists(metadataPath))
                return File.GetLastWriteTime(metadataPath);
        }
        catch { }

        return DateTimeOffset.MinValue;
    }

    private static Dictionary<string, string> ReadInfo(string? dir)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(dir)) return result;
        var path = Path.Combine(dir, "短剧信息.txt");
        if (!File.Exists(path)) return result;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var sep = line.IndexOf('：');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                result[key] = value;
        }

        return result;
    }

    private static string Pick(IReadOnlyDictionary<string, string> values, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string BuildArchiveMetadataPath(string metaRoot, string projectKey)
    {
        var candidate = Path.Combine(metaRoot, $"{SanitizeName(projectKey)}.json");
        if (!File.Exists(candidate)) return candidate;
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(metaRoot, $"{SanitizeName(projectKey)}-{suffix}.json");
    }

    private static string BuildArchiveTargetDir(string parentRoot, string projectName)
    {
        var candidate = Path.Combine(parentRoot, SanitizeName(projectName));
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(parentRoot, $"{SanitizeName(projectName)}-{suffix}");
    }

    private static string SanitizeName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string((name ?? "").Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "archived-project" : result;
    }

    private static void MoveDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        if (Directory.Exists(destinationDir))
            throw new InvalidOperationException($"目标目录已存在：{destinationDir}");
        try
        {
            Directory.Move(sourceDir, destinationDir);
        }
        catch (IOException)
        {
            CopyDirectory(sourceDir, destinationDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
    }

    private static int DeleteVideoFilesRecursive(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            File.Delete(file);
            deleted++;
        }

        return deleted;
    }

    private static (int Deleted, int Preserved) DeleteWorkflowVideoFiles(string dir, IReadOnlySet<int> preserveWorkflowEpisodes)
    {
        var deleted = 0;
        var preserved = 0;
        foreach (var relative in WorkflowVideoDirs)
        {
            var videoDir = Path.Combine(dir, relative);
            if (!Directory.Exists(videoDir)) continue;
            if (preserveWorkflowEpisodes.Count == 0 &&
                TryDeleteWholeDirectoryIfOnlyMatchingFiles(videoDir, IsVideoFile, out var wholeDirDeleted))
            {
                deleted += wholeDirDeleted;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(videoDir, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!VideoExtensions.Contains(extension)) continue;
                if (preserveWorkflowEpisodes.Count > 0 &&
                    PreservableVideoExtensions.Contains(extension) &&
                    preserveWorkflowEpisodes.Contains(ExtractEpisodeNumber(Path.GetFileNameWithoutExtension(file))))
                {
                    preserved++;
                    continue;
                }

                File.Delete(file);
                deleted++;
            }
            PruneEmptyDirectories(videoDir);
        }

        return (deleted, preserved);
    }

    private static int DeleteMaterialVideoFiles(string dir)
    {
        var materialDir = Path.Combine(dir, "material-videos");
        if (!Directory.Exists(materialDir))
            return 0;

        return TryDeleteWholeDirectoryIfOnlyMatchingFiles(materialDir, IsVideoFile, out var deleted)
            ? deleted
            : DeleteVideoFilesRecursive(materialDir);
    }

    private static int DeleteMaterialClipVideoFiles(string dir)
    {
        var clipDir = Path.Combine(dir, "material-clip-output");
        if (!Directory.Exists(clipDir)) return 0;
        if (!ContainsSubtitlesDirectory(clipDir) &&
            TryDeleteWholeDirectoryIfOnlyMatchingFiles(clipDir, IsVideoFile, out var wholeDirDeleted))
        {
            return wholeDirDeleted;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(clipDir, "*", SearchOption.AllDirectories))
        {
            if (IsUnderSubtitlesDirectory(clipDir, file)) continue;
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            File.Delete(file);
            deleted++;
        }

        return deleted;
    }

    private static bool TryDeleteWholeDirectoryIfOnlyMatchingFiles(
        string dir,
        Func<string, bool> shouldDelete,
        out int deleted)
    {
        deleted = 0;
        if (!Directory.Exists(dir))
            return true;

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }

        if (files.Any(file => !shouldDelete(file)))
            return false;

        deleted = files.Count;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    private static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    private static bool ContainsSubtitlesDirectory(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            return false;

        return Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories)
            .Any(dir => string.Equals(Path.GetFileName(dir), "subtitles", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderSubtitlesDirectory(string rootDir, string filePath)
    {
        var relative = Path.GetRelativePath(rootDir, filePath);
        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 &&
               parts.Take(parts.Length - 1)
                   .Any(part => string.Equals(part, "subtitles", StringComparison.OrdinalIgnoreCase));
    }

    private static int ExtractEpisodeNumber(string text)
    {
        var match = Regex.Match(text, @"\u7b2c\s*0*(\d+)\s*\u96c6");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var episode))
            return episode;

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out episode) ? episode : 0;
    }

    private static bool IsCrossVolumeMove(string sourceDir, string destinationDir)
    {
        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceDir));
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationDir));
            return !string.IsNullOrWhiteSpace(sourceRoot) &&
                   !string.IsNullOrWhiteSpace(destinationRoot) &&
                   !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneEmptyDirectories(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var child in Directory.EnumerateDirectories(dir))
            PruneEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    private static void PruneEmptyParent(string path, string stopRoot)
    {
        var parent = Directory.GetParent(path);
        var root = Path.GetFullPath(stopRoot);
        while (parent is not null && IsWithin(parent.FullName, root))
        {
            if (Directory.EnumerateFileSystemEntries(parent.FullName).Any())
                break;
            var next = parent.Parent;
            Directory.Delete(parent.FullName);
            parent = next;
        }
    }

    private static bool IsWithin(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> ReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, object?>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? JsonElementToDictionary(doc.RootElement)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = JsonElementToObject(prop.Value);
        return result;
    }

    private static object? JsonElementToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => JsonElementToDictionary(value),
        JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };

    private static string ReadString(IReadOnlyDictionary<string, object?> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                return value!.ToString()!.Trim();
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return "";
    }

    private static string NormalizeTime(string value)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
            return parsed.ToString("o");
        return value;
    }

    private static string ResolveQueuedAtFromQueueState(string workspaceRoot, params string[] projectDirs)
    {
        try
        {
            var candidates = projectDirs
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0)
                return "";

            foreach (var item in WorkspaceQueueDatabase.Load(workspaceRoot).Items)
            {
                if (string.IsNullOrWhiteSpace(item.ProjectDir) || string.IsNullOrWhiteSpace(item.QueuedAt))
                    continue;
                if (candidates.Contains(Path.GetFullPath(item.ProjectDir)))
                    return item.QueuedAt.Trim();
            }
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static int ResolveArchivedEpisodeCount(ArchivedProjectItem item)
    {
        foreach (var root in new[] { item.ArchivedWorkflowDir, item.ArchivedSourceDir })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var state = TikTokUploadStateStore.LoadState(root);
            foreach (var key in new[] { "episode_count", "total_episodes" })
            {
                if (state.TryGetValue(key, out var value) && value.TryGetInt32(out var count) && count > 0)
                    return count;
            }

            if (state.TryGetValue("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
                return episodes.GetArrayLength();
        }

        return 1;
    }

    private static void SaveArchiveProjectsToDatabase(string workspaceRoot, IReadOnlyList<ArchivedProjectItem> items)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        EnsureArchiveDatabase(dbPath);
        var now = DateTimeOffset.Now.ToString("o");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        var seen = new List<string>();
        foreach (var item in items)
        {
            var archiveId = StableArchiveId(item);
            seen.Add(archiveId);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO archive_projects (
                    archive_id, account_profile_id, original_title, new_title, archive_source,
                    archived_at, archived_source_dir, archived_workflow_dir, metadata_path,
                    payload_json, created_at, updated_at
                ) VALUES (
                    $archive_id, $account_profile_id, $original_title, $new_title, $archive_source,
                    $archived_at, $archived_source_dir, $archived_workflow_dir, $metadata_path,
                    $payload_json, $created_at, $updated_at
                )
                ON CONFLICT(archive_id) DO UPDATE SET
                    account_profile_id = excluded.account_profile_id,
                    original_title = excluded.original_title,
                    new_title = excluded.new_title,
                    archive_source = excluded.archive_source,
                    archived_at = excluded.archived_at,
                    archived_source_dir = excluded.archived_source_dir,
                    archived_workflow_dir = excluded.archived_workflow_dir,
                    metadata_path = excluded.metadata_path,
                    payload_json = excluded.payload_json,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$archive_id", archiveId);
            cmd.Parameters.AddWithValue("$account_profile_id", item.AccountProfileId);
            cmd.Parameters.AddWithValue("$original_title", item.OriginalTitle);
            cmd.Parameters.AddWithValue("$new_title", item.NewTitle);
            cmd.Parameters.AddWithValue("$archive_source", item.ArchiveSource);
            cmd.Parameters.AddWithValue("$archived_at", item.ArchivedAt);
            cmd.Parameters.AddWithValue("$archived_source_dir", item.ArchivedSourceDir);
            cmd.Parameters.AddWithValue("$archived_workflow_dir", item.ArchivedWorkflowDir);
            cmd.Parameters.AddWithValue("$metadata_path", item.MetadataPath);
            cmd.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(ToPayload(item), CompactJsonOptions));
            cmd.Parameters.AddWithValue("$created_at", string.IsNullOrWhiteSpace(item.ArchivedAt) ? now : item.ArchivedAt);
            cmd.Parameters.AddWithValue("$updated_at", now);
            cmd.ExecuteNonQuery();
        }

        using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        if (seen.Count == 0)
        {
            deleteCmd.CommandText = "DELETE FROM archive_projects";
        }
        else
        {
            var placeholders = string.Join(", ", seen.Select((_, i) => $"$id{i}"));
            deleteCmd.CommandText = $"DELETE FROM archive_projects WHERE archive_id NOT IN ({placeholders})";
            for (var i = 0; i < seen.Count; i++)
                deleteCmd.Parameters.AddWithValue($"$id{i}", seen[i]);
        }
        deleteCmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static IReadOnlyList<ArchivedProjectItem> LoadArchiveProjectsFromDatabase(string workspaceRoot)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        if (!File.Exists(dbPath)) return Array.Empty<ArchivedProjectItem>();
        EnsureArchiveDatabase(dbPath);
        var items = new List<ArchivedProjectItem>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload_json
            FROM archive_projects
            ORDER BY archived_at DESC, created_at DESC, archive_id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var payload = ReadPayloadJson(reader.IsDBNull(0) ? "{}" : reader.GetString(0));
            var item = BuildItemFromPayload(
                payload,
                ReadString(payload, "metadata_path", "metadataPath"),
                ReadString(payload, "metadata_path", "metadataPath"));
            if (!string.IsNullOrWhiteSpace(item.MetadataPath))
                items.Add(item);
        }

        return SortItems(items);
    }

    private static void RemoveArchiveFromDatabase(string workspaceRoot, string metadataPath)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        if (!File.Exists(dbPath)) return;
        EnsureArchiveDatabase(dbPath);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM archive_projects WHERE metadata_path = $path OR archive_id = $id";
        cmd.Parameters.AddWithValue("$path", Path.GetFullPath(metadataPath));
        cmd.Parameters.AddWithValue("$id", StableArchiveId(Path.GetFullPath(metadataPath)));
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, object?> ReadPayloadJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? JsonElementToDictionary(doc.RootElement)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, object?> ToPayload(ArchivedProjectItem item) => new(StringComparer.Ordinal)
    {
        ["project_key"] = item.ProjectKey,
        ["display_name"] = item.DisplayName,
        ["original_title"] = item.OriginalTitle,
        ["new_title"] = item.NewTitle,
        ["account_profile_id"] = item.AccountProfileId,
        ["account_profile_name"] = item.AccountProfileName,
        ["archived_at"] = item.ArchivedAt,
        ["queued_at"] = item.QueuedAt,
        ["metadata_path"] = item.MetadataPath,
        ["archive_source"] = item.ArchiveSource,
        ["archived_source_dir"] = item.ArchivedSourceDir,
        ["archived_workflow_dir"] = item.ArchivedWorkflowDir,
    };

    private static void EnsureArchiveDatabase(string dbPath)
    {
        WorkspaceQueueDatabase.EnsureDatabase(dbPath);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS archive_projects (
                archive_id TEXT PRIMARY KEY,
                account_profile_id TEXT NOT NULL DEFAULT '',
                original_title TEXT NOT NULL DEFAULT '',
                new_title TEXT NOT NULL DEFAULT '',
                archive_source TEXT NOT NULL DEFAULT '',
                archived_at TEXT NOT NULL DEFAULT '',
                archived_source_dir TEXT NOT NULL DEFAULT '',
                archived_workflow_dir TEXT NOT NULL DEFAULT '',
                metadata_path TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_archive_projects_archived_at
                ON archive_projects(archived_at DESC, created_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private static string StableArchiveId(ArchivedProjectItem item) =>
        StableArchiveId(FirstNonEmpty(item.MetadataPath, item.ProjectKey, item.ArchivedSourceDir, item.ArchivedWorkflowDir));

    private static string StableArchiveId(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Replace('\\', '/').ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string PathKey(string path)
    {
        try { return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant(); }
        catch { return (path ?? "").Replace('\\', '/').ToLowerInvariant(); }
    }
}
