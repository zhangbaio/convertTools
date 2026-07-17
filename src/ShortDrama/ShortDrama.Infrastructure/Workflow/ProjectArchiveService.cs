using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class ProjectArchiveService : IProjectArchiveService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] IncompleteVideoExtensions = [".aria2", ".part", ".partial", ".download", ".crdownload", ".tmp"];
    private static readonly Regex EpisodeRegex = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);

    public async Task<ProjectArchiveResult> ArchiveAsync(
        string rootDir,
        ScannedProject project,
        ProjectArchiveOptions? options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            throw new DirectoryNotFoundException($"根目录不存在: {rootDir}");
        }

        if (string.IsNullOrWhiteSpace(project.SourceProjectDir) || !Directory.Exists(project.SourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {project.SourceProjectDir}");
        }

        var archiveRoot = Path.Combine(rootDir, "archive");
        Directory.CreateDirectory(archiveRoot);

        var archiveProjectDir = BuildArchiveProjectDir(archiveRoot, project.ProjectKey);
        Directory.CreateDirectory(archiveProjectDir);

        string? archivedSourceDir = null;
        string? archivedWorkflowDir = null;
        string? archivedBackupDir = null;

        if (Directory.Exists(project.SourceProjectDir))
        {
            archivedSourceDir = Path.Combine(archiveProjectDir, "source");
            MoveDirectory(project.SourceProjectDir, archivedSourceDir);
        }

        if (!string.IsNullOrWhiteSpace(project.WorkflowProjectDir) && Directory.Exists(project.WorkflowProjectDir))
        {
            archivedWorkflowDir = Path.Combine(archiveProjectDir, "workflow");
            MoveDirectory(project.WorkflowProjectDir, archivedWorkflowDir);
        }

        if (!string.IsNullOrWhiteSpace(project.BackupProjectDir) && Directory.Exists(project.BackupProjectDir))
        {
            archivedBackupDir = Path.Combine(archiveProjectDir, "backup");
            MoveDirectory(project.BackupProjectDir, archivedBackupDir);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var deletedVideoFileCount = 0;
        var preservedVideoFileCount = 0;
        foreach (var dir in new[] { archivedSourceDir, archivedWorkflowDir, archivedBackupDir }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var preserveEpisodes = string.Equals(dir, archivedWorkflowDir, StringComparison.Ordinal)
                ? options?.PreserveWorkflowVideoEpisodes
                : null;
            var (deleted, preserved) = DeleteVideoFiles(dir!, preserveEpisodes);
            deletedVideoFileCount += deleted;
            preservedVideoFileCount += preserved;
        }

        var metadataPath = Path.Combine(archiveProjectDir, "archive-meta.json");
        var metadata = new
        {
            project.ProjectKey,
            project.DisplayName,
            project.SourceName,
            ArchivedAt = DateTimeOffset.Now,
            project.SourceProjectDir,
            project.WorkflowProjectDir,
            project.BackupProjectDir,
            archivedSourceDir,
            archivedWorkflowDir,
            archivedBackupDir,
            deletedVideoFileCount,
            preservedVideoFileCount
        };
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return new ProjectArchiveResult(
            Ok: true,
            ProjectKey: project.ProjectKey,
            ArchiveProjectDir: archiveProjectDir,
            ArchivedSourceDir: archivedSourceDir,
            ArchivedWorkflowDir: archivedWorkflowDir,
            ArchivedBackupDir: archivedBackupDir,
            DeletedVideoFileCount: deletedVideoFileCount,
            PreservedVideoFileCount: preservedVideoFileCount,
            Message: preservedVideoFileCount > 0
                ? $"已归档到 {archiveProjectDir}，删除视频文件 {deletedVideoFileCount} 个，保留视频文件 {preservedVideoFileCount} 个。"
                : $"已归档到 {archiveProjectDir}，删除视频文件 {deletedVideoFileCount} 个。");
    }

    private static string BuildArchiveProjectDir(string archiveRoot, string projectKey)
    {
        var baseDir = Path.Combine(archiveRoot, projectKey);
        if (!Directory.Exists(baseDir) && !File.Exists(baseDir))
        {
            return baseDir;
        }

        var suffix = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(archiveRoot, $"{projectKey}-{suffix}");
    }

    private static void MoveDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        if (Directory.Exists(destinationDir))
        {
            throw new InvalidOperationException($"归档目标已存在: {destinationDir}");
        }

        Directory.Move(sourceDir, destinationDir);
    }

    private static (int Deleted, int Preserved) DeleteVideoFiles(string dir, IReadOnlyCollection<int>? preserveEpisodes)
    {
        if (!Directory.Exists(dir))
        {
            return (0, 0);
        }

        var deleted = 0;
        var preserved = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
                !IncompleteVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
                ShouldPreserveFile(file, dir, preserveEpisodes))
            {
                preserved++;
                continue;
            }

            File.Delete(file);
            deleted++;
        }

        return (deleted, preserved);
    }

    private static bool ShouldPreserveFile(string file, string archivedDir, IReadOnlyCollection<int>? preserveEpisodes)
    {
        if (preserveEpisodes is null || preserveEpisodes.Count == 0)
        {
            return false;
        }

        var normalizedArchivedDir = archivedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workflowVideosDir = Path.Combine(normalizedArchivedDir, "videos");
        var fileDir = Path.GetDirectoryName(file)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(fileDir, workflowVideosDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = EpisodeRegex.Match(Path.GetFileNameWithoutExtension(file));
        return match.Success &&
               int.TryParse(match.Groups[1].Value, out var episode) &&
               preserveEpisodes.Contains(episode);
    }
}
