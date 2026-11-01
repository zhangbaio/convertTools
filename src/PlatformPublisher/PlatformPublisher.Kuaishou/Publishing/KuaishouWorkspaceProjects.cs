using System.Collections.Concurrent;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 与短剧助手快手个人版、企业版相同：列出当前工作目录下的项目。
/// 有项目文件、短剧信息，或目录里已经有剧集视频，都会出现在列表中。
/// </summary>
public static class KuaishouWorkspaceProjects
{
    public const string ProjectFileName = "shortdrama-project.json";
    private static readonly ConcurrentDictionary<string, bool> ListedCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> List(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return [];

        InvalidateWorkspace(workspaceRoot);
        return Directory.EnumerateDirectories(workspaceRoot)
            .Where(directory => IsListed(workspaceRoot, directory))
            .Select(directory => Path.GetFullPath(directory))
            .ToList();
    }

    public static bool TryGetListed(string? workspaceRoot, string? projectDirectory, out bool listed)
    {
        listed = false;
        return TryCacheKey(workspaceRoot, projectDirectory, out var key) && ListedCache.TryGetValue(key, out listed);
    }

    public static void Invalidate(string? projectDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            ListedCache.Clear();
            return;
        }

        if (!TryNormalize(projectDirectory, out var project))
            return;
        var suffix = "|" + project;
        foreach (var key in ListedCache.Keys.ToArray())
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                ListedCache.TryRemove(key, out _);
        }
    }

    public static void InvalidateWorkspace(string? workspaceRoot)
    {
        if (!TryNormalize(workspaceRoot, out var root))
            return;
        var prefix = root + "|";
        foreach (var key in ListedCache.Keys.ToArray())
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                ListedCache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 只比较路径，不访问磁盘。切换账号时用它先筛出当前工作目录下的项目。
    /// </summary>
    public static bool IsStructuralChild(string? workspaceRoot, string? projectDirectory)
    {
        if (!TryNormalizePair(workspaceRoot, projectDirectory, out var root, out var project))
            return false;
        if (!IsDirectChild(root, project))
            return false;
        return !IsReservedName(Path.GetFileName(project));
    }

    public static bool IsListed(string? workspaceRoot, string? projectDirectory)
    {
        if (!TryCacheKey(workspaceRoot, projectDirectory, out var key))
            return false;
        if (ListedCache.TryGetValue(key, out var cached))
            return cached;

        var listed = IsListedCore(workspaceRoot, projectDirectory);
        ListedCache[key] = listed;
        return listed;
    }

    private static bool IsListedCore(string? workspaceRoot, string? projectDirectory)
    {
        if (!TryNormalizePair(workspaceRoot, projectDirectory, out var root, out var project))
            return false;
        if (!IsDirectChild(root, project) || IsReservedName(Path.GetFileName(project)))
            return false;
        if (IsArchiveRoot(project) || !Directory.Exists(project))
            return false;
        if (File.Exists(Path.Combine(project, ProjectFileName)) ||
            File.Exists(Path.Combine(project, "短剧信息.txt")))
            return true;

        return HasEpisodeVideo(project);
    }

    private static bool TryCacheKey(string? workspaceRoot, string? projectDirectory, out string key)
    {
        key = "";
        if (!TryNormalizePair(workspaceRoot, projectDirectory, out var root, out var project))
            return false;
        key = root + "|" + project;
        return true;
    }

    private static bool TryNormalizePair(string? workspaceRoot, string? projectDirectory, out string root, out string project)
    {
        root = "";
        project = "";
        return TryNormalize(workspaceRoot, out root) && TryNormalize(projectDirectory, out project);
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDirectChild(string root, string project)
    {
        var parent = Path.GetDirectoryName(project);
        return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedName(string name) =>
        name.StartsWith('.') ||
        name.Equals("workflow", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("archive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("config", StringComparison.OrdinalIgnoreCase);

    private static bool HasEpisodeVideo(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(current);
                children = Directory.EnumerateDirectories(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".avi", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var child in children)
            {
                var childName = Path.GetFileName(child);
                if (childName.StartsWith('.') ||
                    childName.Equals("material-videos", StringComparison.OrdinalIgnoreCase) ||
                    childName.Equals("material-clip-output", StringComparison.OrdinalIgnoreCase) ||
                    childName.Equals("素材剪辑输出", StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(child);
            }
        }

        return false;
    }

    private static bool IsArchiveRoot(string directory)
    {
        var hasMeta = Directory.Exists(Path.Combine(directory, "meta"));
        var hasSource = Directory.Exists(Path.Combine(directory, "source"));
        var hasWorkflow = Directory.Exists(Path.Combine(directory, "workflow"));
        return hasMeta && (hasSource || hasWorkflow);
    }
}
