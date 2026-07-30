using System.Security.Cryptography;
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
        var stagedDir = BuildStagingDirectory(desiredWorkflowDir);
        Directory.Move(desiredWorkflowDir, stagedDir);
        log?.Invoke(
            $"发现仅含旧证明材料的目标 workflow，已暂存并准备复用：{desiredWorkflowDir}");

        try
        {
            var result = switchWorkflow();
            if (!Directory.Exists(desiredWorkflowDir))
            {
                throw new InvalidOperationException(
                    $"项目目录切换后未生成目标 workflow：{desiredWorkflowDir}");
            }

            MergeDirectory(stagedDir, desiredWorkflowDir);
            log?.Invoke($"旧证明材料已合并到恢复后的项目目录：{desiredWorkflowDir}");
            return result;
        }
        catch
        {
            RestoreStagedDirectory(stagedDir, desiredWorkflowDir);
            throw;
        }
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

    private static string BuildStagingDirectory(string desiredWorkflowDir)
    {
        var parent = Path.GetDirectoryName(desiredWorkflowDir)
                     ?? throw new InvalidOperationException(
                         $"无法解析 workflow 根目录：{desiredWorkflowDir}");
        string candidate;
        do
        {
            candidate = Path.Combine(
                parent,
                $".deleted-proof-recovery-{Guid.NewGuid():N}");
        } while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
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
            Directory.Delete(sourceDir);
    }

    private static void MoveFilePreservingBoth(string sourceFile, string destinationFile)
    {
        if (!File.Exists(destinationFile))
        {
            File.Move(sourceFile, destinationFile);
            return;
        }

        if (FilesEqual(sourceFile, destinationFile))
        {
            File.Delete(sourceFile);
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

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
            return false;

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftHash = SHA256.HashData(leftStream);
        var rightHash = SHA256.HashData(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static void RestoreStagedDirectory(string stagedDir, string desiredWorkflowDir)
    {
        if (!Directory.Exists(stagedDir))
            return;

        if (!Directory.Exists(desiredWorkflowDir))
        {
            Directory.Move(stagedDir, desiredWorkflowDir);
            return;
        }

        // If the switching action already created the desired directory, preserve every
        // staged file there instead of deleting either side.
        MergeDirectory(stagedDir, desiredWorkflowDir);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
