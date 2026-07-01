using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectImageGenerator
{
    Task<ProjectImageGenerateResult> GenerateAsync(
        ProjectImageGenerateRequest request,
        CancellationToken cancellationToken);
}
