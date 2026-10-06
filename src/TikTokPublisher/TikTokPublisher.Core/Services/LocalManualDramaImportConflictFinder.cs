using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class LocalManualDramaImportConflictFinder
{
    public const string LocalQueueReason = "本地队列";
    public const string LocalImportedReason = "本地已导入";
    public const string ArchiveReason = "已归档";
    public const string ManagementReason = "管理系统";

    public sealed record Conflict(string ProjectDir, string DisplayName, IReadOnlyList<string> Reasons);

    public static IReadOnlyList<Conflict> Find(
        IReadOnlyList<LocalManualDramaImportPreview> selected,
        IEnumerable<QueueProjectItem> queueItems,
        IEnumerable<ArchivedProjectItem> archivedItems,
        IReadOnlySet<string>? managementDuplicateNames = null)
    {
        var queueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queuePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in queueItems)
            AddProjectKeys(queueNames, queuePaths, item.ProjectDir, item.DisplayName, item.OriginalTitle, item.NewTitle);

        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in archivedItems)
        {
            AddProjectKeys(
                archiveNames,
                archivePaths,
                item.SourceProjectDir,
                item.ArchivedSourceDir,
                item.DisplayName,
                item.OriginalTitle,
                item.NewTitle);
        }

        var managementNames = managementDuplicateNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(managementDuplicateNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);

        var conflicts = new List<Conflict>();
        foreach (var preview in selected)
        {
            var names = CandidateNames(preview);
            var path = NormalizePath(preview.ProjectDir);
            var reasons = new List<string>();
            if (Matches(path, names, queuePaths, queueNames))
                reasons.Add(LocalQueueReason);
            else if (preview.MetadataExists)
                reasons.Add(LocalImportedReason);
            if (Matches(path, names, archivePaths, archiveNames))
                reasons.Add(ArchiveReason);
            if (names.Any(managementNames.Contains))
                reasons.Add(ManagementReason);
            if (reasons.Count == 0)
                continue;

            conflicts.Add(new Conflict(
                preview.ProjectDir,
                string.IsNullOrWhiteSpace(preview.DisplayName) ? Path.GetFileName(preview.ProjectDir) : preview.DisplayName,
                reasons));
        }

        return conflicts;
    }

    private static bool Matches(
        string path,
        IReadOnlyList<string> names,
        HashSet<string> paths,
        HashSet<string> knownNames)
    {
        if (!string.IsNullOrWhiteSpace(path) && paths.Contains(path))
            return true;
        return names.Any(knownNames.Contains);
    }

    private static void AddProjectKeys(HashSet<string> names, HashSet<string> paths, params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            names.Add(text);
            if (LooksLikePath(text))
            {
                var path = NormalizePath(text);
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
                var folder = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(folder))
                    names.Add(folder);
            }
        }
    }

    private static List<string> CandidateNames(LocalManualDramaImportPreview preview)
    {
        var names = new List<string>();
        AddName(names, preview.DisplayName);
        AddName(names, Path.GetFileName(preview.ProjectDir));
        return names;
    }

    private static void AddName(List<string> names, string? value)
    {
        var text = (value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(text))
            names.Add(text);
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        Path.IsPathRooted(value);

    private static string NormalizePath(string? path)
    {
        var text = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";
        try
        {
            return Path.GetFullPath(text);
        }
        catch
        {
            return text;
        }
    }
}
