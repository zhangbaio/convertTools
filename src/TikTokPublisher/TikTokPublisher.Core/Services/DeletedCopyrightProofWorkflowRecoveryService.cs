using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// Reconciles proof-material-only workflow remnants left by an older deleted-project
/// recovery. A real project directory is never replaced or merged automatically.
/// </summary>
public static class DeletedCopyrightProofWorkflowRecoveryService
{
    private static readonly HashSet<string> AllowedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        TikTokProofMaterialService.ProofPdfFileName,
    };

    private static readonly HashSet<string> AllowedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        TikTokSourceFileInfoScreenshotService.OutputDirectoryName,
        TikTokSourceFileInfoUploadPackageService.OutputDirectoryName,
        "原始文件或素材文件信息",
        TikTokAiGenerationScreenshotService.OutputDirectoryName,
        "AI生成过程截图",
        TikTokProjectImageService.OutputDirectoryName,
    };

    public static T RunWithReconciledTarget<T>(
        string projectDir,
        string newTitle,
        Func<T> switchWorkflow,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(switchWorkflow);

        var sourceDir = Path.GetFullPath(projectDir);
        var currentWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.LoadContext(sourceDir).WorkflowProjectDir);
        var desiredWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.ResolveDesiredWorkflowProjectDir(sourceDir, newTitle));

        if (PathsEqual(currentWorkflowDir, desiredWorkflowDir) ||
            !Directory.Exists(desiredWorkflowDir))
        {
            return switchWorkflow();
        }

        EnsureProofMaterialOnly(desiredWorkflowDir);
        log?.Invoke(
            $"发现仅含旧证明材料的目标 workflow，将直接原位复用：{desiredWorkflowDir}");

        // Do not rename the existing target directory. On Windows a PDF preview,
        // Explorer window, antivirus scanner, or another process can hold a directory
        // handle that denies Directory.Move even though its ACL grants full access.
        // Moving the rebuilt project's files into that directory keeps the old proof
        // artifact in place and avoids requiring delete-sharing on the open handle.
        MergeDirectory(currentWorkflowDir, desiredWorkflowDir);
        ProjectWorkspaceService.UpdateMovedWorkspaceMetadata(
            sourceDir,
            desiredWorkflowDir);

        var result = switchWorkflow();
        log?.Invoke($"旧证明材料已原位合并到恢复后的项目目录：{desiredWorkflowDir}");
        return result;
    }

    public static void ValidateTarget(string projectDir, string newTitle)
    {
        var sourceDir = Path.GetFullPath(projectDir);
        var currentWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.LoadContext(sourceDir).WorkflowProjectDir);
        var desiredWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.ResolveDesiredWorkflowProjectDir(sourceDir, newTitle));

        if (PathsEqual(currentWorkflowDir, desiredWorkflowDir) ||
            !Directory.Exists(desiredWorkflowDir))
        {
            return;
        }

        EnsureProofMaterialOnly(desiredWorkflowDir);
    }

    public static bool RepairExistingProject(
        string projectDir,
        string newTitle,
        Action<string>? log = null)
    {
        var sourceDir = Path.GetFullPath(projectDir);
        var currentWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.LoadContext(sourceDir).WorkflowProjectDir);
        var desiredWorkflowDir = Path.GetFullPath(
            ProjectWorkspaceService.ResolveDesiredWorkflowProjectDir(sourceDir, newTitle));

        if (PathsEqual(currentWorkflowDir, desiredWorkflowDir) ||
            !Directory.Exists(desiredWorkflowDir))
        {
            return false;
        }

        RunWithReconciledTarget(
            sourceDir,
            newTitle,
            () => ProjectWorkspaceService.SyncWorkflowProjectDirName(
                sourceDir,
                newTitle,
                log),
            log);
        return true;
    }

    private static void EnsureProofMaterialOnly(string directory)
    {
        var unexpected = Directory.EnumerateFileSystemEntries(directory)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return File.Exists(path)
                    ? !AllowedRootFiles.Contains(name)
                    : !AllowedRootDirectories.Contains(name);
            })
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (unexpected.Length == 0)
            return;

        throw new InvalidOperationException(
            $"目标 workflow 已存在完整项目或未知文件，已停止自动合并：{directory}。" +
            $"冲突内容：{string.Join("、", unexpected)}");
    }

    private static void MergeDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir))
        {
            var destinationFile = Path.Combine(destinationDir, Path.GetFileName(sourceFile));
            MoveFilePreservingBoth(sourceFile, destinationFile);
        }

        foreach (var sourceChild in Directory.EnumerateDirectories(sourceDir))
        {
            var destinationChild = Path.Combine(destinationDir, Path.GetFileName(sourceChild));
            MergeDirectory(sourceChild, destinationChild);
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
        {
            try
            {
                Directory.Delete(sourceDir);
            }
            catch (IOException)
            {
                // An empty legacy directory can remain when another process has it
                // open. Metadata already points to the desired workflow, so it is
                // harmless and can be removed by normal cleanup later.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above: never fail recovery only because an empty old folder
                // cannot be removed immediately.
            }
        }
    }

    private static void MoveFilePreservingBoth(string sourceFile, string destinationFile)
    {
        if (!File.Exists(destinationFile))
        {
            File.Move(sourceFile, destinationFile);
            return;
        }

        var directory = Path.GetDirectoryName(destinationFile)!;
        var fileName = Path.GetFileNameWithoutExtension(destinationFile);
        var extension = Path.GetExtension(destinationFile);
        var index = 1;
        string preservedPath;
        do
        {
            preservedPath = Path.Combine(
                directory,
                $"{fileName}.恢复材料{index}{extension}");
            index++;
        } while (File.Exists(preservedPath));

        File.Move(sourceFile, preservedPath);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
