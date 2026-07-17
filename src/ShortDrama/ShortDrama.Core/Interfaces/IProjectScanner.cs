using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectScanner
{
    Task<ProjectScanResult> ScanAsync(
        string rootDir,
        string? backupRootDir,
        CancellationToken cancellationToken);
}
