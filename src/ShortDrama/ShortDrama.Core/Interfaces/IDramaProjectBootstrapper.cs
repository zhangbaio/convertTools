using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IDramaProjectBootstrapper
{
    Task<DramaProjectBootstrapResult> BootstrapAsync(
        DramaProjectBootstrapRequest request,
        CancellationToken cancellationToken);
}
