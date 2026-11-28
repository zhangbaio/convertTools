using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

internal static class QueueProjectDeletionPaths
{
    internal const string StagingFolderName = ".queue-deletions";

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static void ValidateProjectDirectory(string workspace, string directory)
    {
        if (!WorkspaceQueueService.IsProjectInsideWorkspace(workspace, directory))
            throw new InvalidDataException($"拒绝删除工作区外路径或根目录：{directory}");

        var parts = Path.GetRelativePath(workspace, directory).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0];
        var isSource = parts.Length == 1 && IsSourceName(first);
        var isWorkflow = parts.Length == 2 && first.Equals("workflow", StringComparison.OrdinalIgnoreCase) &&
                         !parts[1].StartsWith('.');
        var isRecovery = parts.Length == 2 && IsSourceName(first) && parts[1] == "_版权恢复";
        if (!isSource && !isWorkflow && !isRecovery)
            throw new InvalidDataException($"拒绝删除工作区保留目录或非项目路径：{directory}");

        ValidateDirectoryChain(directory);
    }

    private static bool IsSourceName(string name) => !name.StartsWith('.') &&
        !name.Equals("config", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("workflow", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("archive", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("material-clip-output", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals(TikTokUploadStagingService.StagingDirName, StringComparison.OrdinalIgnoreCase) &&
        !name.Equals(ProjectVideoResolver.SourceOriginalsDirectoryName, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateDirectoryChain(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            _ = ExistsAsSafeDirectory(current.FullName);
    }

    internal static bool ExistsAsSafeDirectory(string path)
    {
        if (ReadSafeAttributes(path) is not { } attributes)
            return false;
        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new InvalidDataException($"项目路径不是目录：{path}");
        return true;
    }

    internal static void ValidateQueueFiles(string workspace)
    {
        foreach (var path in new[] { WorkspaceQueuePaths.QueueDatabasePath(workspace), WorkspaceQueuePaths.LegacyQueueJsonPath(workspace) })
        {
            if (ReadSafeAttributes(path)?.HasFlag(FileAttributes.Directory) == true)
                throw new InvalidDataException($"队列状态路径不是文件：{path}");
        }
    }

    internal static FileAttributes? ReadSafeAttributes(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"拒绝通过链接或重解析点操作路径：{path}");
        return attributes;
    }

    internal static bool EqualOrNested(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        WorkspaceQueueService.IsProjectInsideWorkspace(root, path);
}
