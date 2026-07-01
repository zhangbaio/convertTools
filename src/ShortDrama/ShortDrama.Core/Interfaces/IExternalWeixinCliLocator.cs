using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IExternalWeixinCliLocator
{
    Task<ExternalWeixinCliCommand> ResolveAsync(CancellationToken cancellationToken);
}
