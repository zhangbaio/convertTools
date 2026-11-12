using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Services;

/// <summary>
/// 删除勾选时要删掉的项目文件夹。只允许当前工作目录下的剧集目录，以及它对应的 workflow 子目录。
/// </summary>
public static class KuaishouCheckedProjectDeletion
{
    public sealed record Plan(IReadOnlyList<string> Directories, string? Refusal)
    {
        public bool AlreadyGone => Directories.Count == 0 && Refusal is null;
    }

    public static Plan Resolve(string? workspaceRoot, string? projectDirectory)
    {
        if (!TryNormalize(workspaceRoot, out var workspace))
            return Refuse("没有工作目录，未删除文件夹。");
        if (!TryNormalize(projectDirectory, out var project))
            return Refuse("没有项目目录，未删除文件夹。");
        if (!Directory.Exists(project))
            return new Plan([], null);

        string source;
        string workflow;
        try
        {
            (source, workflow) = KuaishouArtifactColumnStatusReader.ResolveDirectories(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            source = project;
            workflow = project;
        }

        if (!TryAcceptProject(workspace, source, out var refusal))
            return Refuse(refusal);

        var targets = new List<string> { source };
        if (!string.Equals(source, workflow, StringComparison.OrdinalIgnoreCase) &&
            TryAcceptWorkflowChild(workspace, workflow))
        {
            targets.Add(workflow);
        }

        return new Plan(targets.OrderByDescending(path => path.Length).ToArray(), null);
    }

    private static Plan Refuse(string reason) => new([], reason);

    private static bool TryAcceptProject(string workspace, string path, out string refusal)
    {
        refusal = "";
        if (!TryNormalize(path, out path))
        {
            refusal = "项目目录无效，未删除文件夹。";
            return false;
        }

        if (string.Equals(path, workspace, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "不能删除工作目录本身。";
            return false;
        }

        if (!IsInside(workspace, path))
        {
            refusal = "文件夹不在当前工作目录，未删除。";
            return false;
        }

        var parts = RelativeParts(workspace, path);
        if (parts.Length != 1 || IsReserved(parts[0]))
        {
            refusal = "只能删除工作目录下的剧集文件夹。";
            return false;
        }

        return true;
    }

    private static bool TryAcceptWorkflowChild(string workspace, string path)
    {
        if (!TryNormalize(path, out path) || !IsInside(workspace, path))
            return false;
        var parts = RelativeParts(workspace, path);
        return parts.Length >= 2 &&
               parts[0].Equals("workflow", StringComparison.OrdinalIgnoreCase) &&
               !IsReserved(parts[1]);
    }

    private static string[] RelativeParts(string workspace, string path)
    {
        var relative = path[workspace.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsInside(string workspace, string path) =>
        path.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsReserved(string name) =>
        name.StartsWith('.') ||
        name.Equals("workflow", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("archive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("config", StringComparison.OrdinalIgnoreCase);

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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
