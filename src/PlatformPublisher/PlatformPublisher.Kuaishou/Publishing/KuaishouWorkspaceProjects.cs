namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 与短剧助手快手个人版、企业版相同：只列出当前工作目录下带 shortdrama-project.json 的项目。
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

        if (IsArchiveRoot(project))
            return false;

        return File.Exists(Path.Combine(project, ProjectFileName));
    }

    private static bool IsArchiveRoot(string directory)
    {
        var hasMeta = Directory.Exists(Path.Combine(directory, "meta"));
        var hasSource = Directory.Exists(Path.Combine(directory, "source"));
        var hasWorkflow = Directory.Exists(Path.Combine(directory, "workflow"));
        return hasMeta && (hasSource || hasWorkflow);
    }
}
