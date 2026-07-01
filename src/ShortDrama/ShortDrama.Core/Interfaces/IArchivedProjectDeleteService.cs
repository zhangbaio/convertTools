using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IArchivedProjectDeleteService
{
    Task<ArchivedProjectDeleteResult> DeleteAsync(
        string rootDir,
        string archiveProjectDir,
        CancellationToken cancellationToken);
}
