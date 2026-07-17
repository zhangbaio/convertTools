using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWeixinAuthStateService
{
    Task<WeixinAuthStateInfo> ResolveAsync(
        WeixinAutomationConfig config,
        CancellationToken cancellationToken);
}
