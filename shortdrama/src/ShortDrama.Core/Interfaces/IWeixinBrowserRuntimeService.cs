using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWeixinBrowserRuntimeService
{
    Task<WeixinBrowserRuntimeStatus> InspectAsync(CancellationToken cancellationToken);

    void ConfigureEnvironment(WeixinBrowserRuntimeStatus status);
}
