using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Abstractions;

/// <summary>由 UI 层为发布流程提供账号对应的内置浏览器会话。</summary>
public interface IEmbeddedBrowserProvider
{
    Task<IEmbeddedBrowser?> GetBrowserAsync(
        TikTokAccountProfile account,
        CancellationToken ct,
        EmbeddedBrowserAccessOptions? options = null);
}
