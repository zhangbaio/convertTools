using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWeixinAutomationConfigLoader
{
    Task<WeixinAutomationConfig> LoadAsync(
        string? configPath,
        string projectDirectory,
        CancellationToken cancellationToken);
}
