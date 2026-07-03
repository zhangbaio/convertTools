using System.Text.Json;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Workflow;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Archive;

public sealed record ArchivedProjectItem(
    string ProjectKey,
    string DisplayName,
    string OriginalTitle,
    string NewTitle,
    string ArchivedAt,
    string MetadataPath,
    string ArchiveProjectDir,
    string ArchivedSourceDir,
    string ArchivedWorkflowDir);

public static class TikTokArchivedProjectService
{
    private static readonly ProjectArchiveService ArchiveService = new();
    private static readonly ArchivedProjectDeleteService DeleteService = new();

    public static string ResolveArchiveRoot(string workspaceRoot, string? archiveRootDir = null)
    {
        var custom = (archiveRootDir ?? ClientSettingsStore.Load().ArchiveRootDir ?? "").Trim();
        if (custom.Length > 0)
            return Path.GetFullPath(custom);
        return Path.Combine(Path.GetFullPath(workspaceRoot), "archive");
    }

    public static IReadOnlyList<ArchivedProjectItem> List(string workspaceRoot, string? archiveRootDir = null)
    {
        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        if (!Directory.Exists(archiveRoot))
            return Array.Empty<ArchivedProjectItem>();

        var items = new List<ArchivedProjectItem>();
        foreach (var dir in Directory.GetDirectories(archiveRoot).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var metadataPath = Path.Combine(dir, "archive-meta.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var root = doc.RootElement;
                var projectKey = ReadString(root, "ProjectKey", "projectKey") ?? Path.GetFileName(dir);
                items.Add(new ArchivedProjectItem(
                    ProjectKey: projectKey,
                    DisplayName: ReadString(root, "DisplayName", "displayName") ?? projectKey,
                    OriginalTitle: ReadTitle(root, "SourceName", "sourceName", "originalTitle"),
                    NewTitle: ReadTitle(root, "DisplayName", "displayName", "newTitle"),
                    ArchivedAt: ReadString(root, "ArchivedAt", "archivedAt") ?? "",
                    MetadataPath: metadataPath,
                    ArchiveProjectDir: dir,
                    ArchivedSourceDir: ReadString(root, "archivedSourceDir") ?? Path.Combine(dir, "source"),
                    ArchivedWorkflowDir: ReadString(root, "archivedWorkflowDir") ?? Path.Combine(dir, "workflow")));
            }
            catch
            {
                // skip invalid metadata
            }
        }

        return items;
    }

    public static async Task ArchiveQueueProjectAsync(
        string workspaceRoot,
        string projectDir,
        string? archiveRootDir = null,
        CancellationToken ct = default)
    {
        var normalizedProject = Path.GetFullPath(projectDir);
        var displayName = Path.GetFileName(normalizedProject);
        var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(normalizedProject);
        var project = new ScannedProject(
            ProjectKey: displayName,
            SourceName: displayName,
            DisplayName: displayName,
            SourceProjectDir: normalizedProject,
            WorkflowProjectDir: Directory.Exists(workflowDir) ? workflowDir : null,
            BackupProjectDir: null,
            CreatedAt: null,
            Status: "archived",
            VideoCount: 0,
            CompletedSteps: 0,
            TotalSteps: 0,
            ResumeFrom: null,
            FailedStep: null,
            HasFailure: false);

        await ArchiveService.ArchiveAsync(workspaceRoot, project, null, ct);
    }

    public static void Restore(string workspaceRoot, string archiveProjectDir, string? archiveRootDir = null)
    {
        var archiveRoot = ResolveArchiveRoot(workspaceRoot, archiveRootDir);
        var target = Path.GetFullPath(archiveProjectDir);
        if (!target.StartsWith(Path.GetFullPath(archiveRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许恢复 archive 目录下的归档项目。");

        var metadataPath = Path.Combine(target, "archive-meta.json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("未找到 archive-meta.json", metadataPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = doc.RootElement;
        var sourceArchived = Path.Combine(target, "source");
        var workflowArchived = Path.Combine(target, "workflow");

        var preferredSource = ReadString(root, "SourceProjectDir", "sourceProjectDir");
        var preferredWorkflow = ReadString(root, "WorkflowProjectDir", "workflowProjectDir");
        var restoredSource = !string.IsNullOrWhiteSpace(preferredSource)
            ? Path.GetFullPath(preferredSource)
            : Path.Combine(Path.GetFullPath(workspaceRoot), Path.GetFileName(sourceArchived));
        var restoredWorkflow = !string.IsNullOrWhiteSpace(preferredWorkflow)
            ? Path.GetFullPath(preferredWorkflow)
            : Path.Combine(Path.GetFullPath(workspaceRoot), "workflow", Path.GetFileName(workflowArchived));

        if (Directory.Exists(sourceArchived))
        {
            if (Directory.Exists(restoredSource))
                throw new InvalidOperationException($"恢复目标 source 目录已存在：{restoredSource}");
            Directory.CreateDirectory(Path.GetDirectoryName(restoredSource)!);
            Directory.Move(sourceArchived, restoredSource);
        }

        if (Directory.Exists(workflowArchived))
        {
            if (Directory.Exists(restoredWorkflow))
                throw new InvalidOperationException($"恢复目标 workflow 目录已存在：{restoredWorkflow}");
            Directory.CreateDirectory(Path.GetDirectoryName(restoredWorkflow)!);
            Directory.Move(workflowArchived, restoredWorkflow);
        }

        Directory.Delete(target, recursive: true);
    }

    public static Task DeleteAsync(string workspaceRoot, string archiveProjectDir, CancellationToken ct = default) =>
        DeleteService.DeleteAsync(workspaceRoot, archiveProjectDir, ct);

    private static string ReadTitle(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return "";
    }

    private static string? ReadString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString()?.Trim();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }
}
