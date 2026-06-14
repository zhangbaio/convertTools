using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectArchiveService
{
    Task<ProjectArchiveResult> ArchiveAsync(
        string rootDir,
        ScannedProject project,
        ProjectArchiveOptions? options,
        CancellationToken cancellationToken);
}
