using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IPosterRenamer
{
    Task<PosterRenameResult> RenameAsync(
        PosterRenameRequest request,
        CancellationToken cancellationToken);
}
