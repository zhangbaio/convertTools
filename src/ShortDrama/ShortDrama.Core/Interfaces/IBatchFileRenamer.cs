using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IBatchFileRenamer
{
    Task<BatchFileRenameResult> RenameAsync(
        BatchFileRenameRequest request,
        CancellationToken cancellationToken);
}
