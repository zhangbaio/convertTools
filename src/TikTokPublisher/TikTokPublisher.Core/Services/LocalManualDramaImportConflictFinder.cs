using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class LocalManualDramaImportConflictFinder
{
    public const string LocalQueueReason = "本地队列";
    public const string LocalImportedReason = "本地已导入";
    public const string ArchiveReason = "已归档";
    public const string ManagementReason = "管理系统";
    private const string MetadataFile = "shortdrama-project.json";

    public sealed record MatchedQueueProject(string ProjectDir, bool MetadataExists, bool ActiveUpload);

    public sealed record Conflict(
        string ProjectDir,
        string DisplayName,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<MatchedQueueProject> MatchedQueueProjects);

    public sealed record UnimportedLocalDeletionTarget(string Directory, IReadOnlyList<string> RelatedImportDirs);

    public sealed record UnimportedLocalDeletionPlan(
        IReadOnlyList<UnimportedLocalDeletionTarget> Targets,
        IReadOnlyList<string> ProtectedImportDirs,
        IReadOnlyList<string> BlockedImportDirs);

    public static IReadOnlyList<Conflict> Find(
        IReadOnlyList<LocalManualDramaImportPreview> selected,
        IEnumerable<QueueProjectItem> queueItems,
        IEnumerable<ArchivedProjectItem> archivedItems,
        IReadOnlySet<string>? managementDuplicateNames = null)
    {
        var queueList = queueItems.Where(item => item is not null).ToArray();
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
            var matchedQueue = queueList
                .Where(item => QueueItemMatches(path, names, item))
                .Select(ToMatchedQueueProject)
                .GroupBy(item => item.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var reasons = new List<string>();
            if (matchedQueue.Length > 0)
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
                reasons,
                matchedQueue));
        }

        return conflicts;
    }

    public static UnimportedLocalDeletionPlan ResolveUnimportedLocalDeletion(
        IReadOnlyList<Conflict> conflicts,
        IReadOnlyList<LocalManualDramaImportPreview> localCandidates,
        IEnumerable<string> importDirs)
    {
        var protectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in importDirs)
        {
            var path = NormalizePath(dir);
            if (!string.IsNullOrWhiteSpace(path))
                protectedDirs.Add(path);
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in conflicts)
        {
            var conflictDir = NormalizePath(conflict.ProjectDir);
            foreach (var queueProject in conflict.MatchedQueueProjects)
            {
                var queueDir = NormalizePath(queueProject.ProjectDir);
                if (string.IsNullOrWhiteSpace(queueDir) || OverlapsProtected(queueDir, protectedDirs))
                    continue;
                if (queueProject.MetadataExists)
                    continue;
                if (queueProject.ActiveUpload)
                {
                    if (!string.IsNullOrWhiteSpace(conflictDir))
                        blocked.Add(conflictDir);
                    continue;
                }

                AddTarget(targets, queueDir, conflictDir);
            }
        }

        foreach (var candidate in localCandidates)
        {
            if (candidate.MetadataExists)
                continue;
            var candidateDir = NormalizePath(candidate.ProjectDir);
            if (string.IsNullOrWhiteSpace(candidateDir) || OverlapsProtected(candidateDir, protectedDirs))
                continue;

            var names = CandidateNames(candidate);
            foreach (var conflict in conflicts)
            {
                if (!names.Any(ConflictNames(conflict).Contains))
                    continue;
                AddTarget(targets, candidateDir, NormalizePath(conflict.ProjectDir));
            }
        }

        return new UnimportedLocalDeletionPlan(
            targets
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new UnimportedLocalDeletionTarget(
                    pair.Key,
                    pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()))
                .ToArray(),
            protectedDirs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static bool IsActiveUpload(string? statusText) =>
        string.Equals(statusText, QueueStepStatus.Running, StringComparison.Ordinal) ||
        string.Equals(statusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal);

    public static bool IsProtectedImportPath(string? path, IEnumerable<string> protectedImportDirs)
    {
        var full = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(full))
            return false;
        var protectedDirs = protectedImportDirs
            .Select(NormalizePath)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return OverlapsProtected(full, protectedDirs);
    }

    public static bool IsUnderDirectory(string? path, string? parent)
    {
        var full = NormalizePath(path);
        var root = NormalizePath(parent);
        if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(root))
            return false;
        return IsNestedPath(full, root);
    }

    private static void AddTarget(
        Dictionary<string, HashSet<string>> targets,
        string directory,
        string importDir)
    {
        if (!targets.TryGetValue(directory, out var relatedDirs))
        {
            relatedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            targets[directory] = relatedDirs;
        }

        if (!string.IsNullOrWhiteSpace(importDir))
            relatedDirs.Add(importDir);
    }

    private static MatchedQueueProject ToMatchedQueueProject(QueueProjectItem item)
    {
        var dir = NormalizePath(item.ProjectDir);
        return new MatchedQueueProject(dir, QueueMetadataExists(dir), IsActiveUpload(item.StatusText));
    }

    private static bool QueueMetadataExists(string projectDir) =>
        !string.IsNullOrWhiteSpace(projectDir) &&
        File.Exists(Path.Combine(projectDir, MetadataFile));

    private static bool QueueItemMatches(string path, IReadOnlyList<string> names, QueueProjectItem item)
    {
        var namesForItem = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathsForItem = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProjectKeys(namesForItem, pathsForItem, item.ProjectDir, item.DisplayName, item.OriginalTitle, item.NewTitle);
        return Matches(path, names, pathsForItem, namesForItem);
    }

    private static bool OverlapsProtected(string path, HashSet<string> protectedDirs)
    {
        foreach (var importDir in protectedDirs)
        {
            if (string.Equals(path, importDir, StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsNestedPath(path, importDir) || IsNestedPath(importDir, path))
                return true;
        }

        return false;
    }

    private static bool IsNestedPath(string path, string parent)
    {
        var root = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ConflictNames(Conflict conflict)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddName(names, conflict.DisplayName);
        AddName(names, Path.GetFileName(NormalizePath(conflict.ProjectDir)));
        return names;
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

    private static void AddName(ICollection<string> names, string? value)
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
