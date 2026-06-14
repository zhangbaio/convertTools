using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IMaterialValidationService
{
    Task<MaterialValidationResult> ValidateAsync(
        string workflowProjectDir,
        CancellationToken cancellationToken);
}
