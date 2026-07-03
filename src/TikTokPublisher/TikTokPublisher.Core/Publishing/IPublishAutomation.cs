using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

/// <summary>驱动单个账号的内嵌浏览器完成 TikTok 短剧中心剧集上传。</summary>
public interface IPublishAutomation
{
    Task<PublishResult> PublishAsync(
        TikTokAccountProfile account,
        PublishItem item,
        string cdpEndpoint,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct);
}
