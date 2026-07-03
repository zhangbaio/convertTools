using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>工作目录队列扫描 + 持久化合并（对齐 Python <c>scan_workspace_projects</c>）。</summary>
public static class WorkspaceQueueService
{
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
        return item;
    }

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
