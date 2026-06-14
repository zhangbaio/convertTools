using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class ArchivedProjectDeleteService : IArchivedProjectDeleteService
{
    public Task<ArchivedProjectDeleteResult> DeleteAsync(
        string rootDir,
        string archiveProjectDir,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            throw new DirectoryNotFoundException($"根目录不存在: {rootDir}");
        }

        if (string.IsNullOrWhiteSpace(archiveProjectDir) || !Directory.Exists(archiveProjectDir))
        {
            throw new DirectoryNotFoundException($"归档项目不存在: {archiveProjectDir}");
        }

        var archiveRoot = Path.GetFullPath(Path.Combine(rootDir, "archive"));
        var target = Path.GetFullPath(archiveProjectDir);
        var archiveRootPrefix = archiveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(archiveRootPrefix, StringComparison.Ordinal) &&
            !string.Equals(target, archiveRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"只允许删除 archive 目录下的归档项目: {archiveProjectDir}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(target, recursive: true);

        return Task.FromResult(new ArchivedProjectDeleteResult(
            Ok: true,
            ArchiveProjectDir: target,
            Message: $"已删除归档项目: {target}"));
    }
}
