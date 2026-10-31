namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 与短剧助手快手个人版、企业版相同：列出当前工作目录下的项目。
/// 有项目文件、短剧信息，或目录里已经有剧集视频，都会出现在列表中。
/// </summary>
public static class KuaishouWorkspaceProjects
{
    public const string ProjectFileName = "shortdrama-project.json";

    public static bool IsListed(string? workspaceRoot, string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(projectDirectory))
            return false;

        string root;
        string project;
        try
        {
            root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            project = Path.GetFullPath(projectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        var parent = Path.GetDirectoryName(project);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileName(project);
        if (name.StartsWith('.') ||
            name.Equals("workflow", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("archive", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsArchiveRoot(project) || !Directory.Exists(project))
            return false;

        if (File.Exists(Path.Combine(project, ProjectFileName)) ||
            File.Exists(Path.Combine(project, "短剧信息.txt")))
            return true;

        return HasEpisodeVideo(project);
    }

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
