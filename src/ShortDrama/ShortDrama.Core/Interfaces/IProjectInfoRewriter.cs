using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectInfoRewriter
{
    Task<ProjectInfoRewriteResult> RewriteAsync(
        ProjectInfoRewriteRequest request,
        CancellationToken cancellationToken);
}
