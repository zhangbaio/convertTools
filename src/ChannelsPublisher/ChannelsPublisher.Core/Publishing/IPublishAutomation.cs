namespace ChannelsPublisher.Core.Publishing;

/// <summary>驱动单个账号的内嵌浏览器完成一条素材的视频号发表。
///
/// 实现（Desktop 侧 PlaywrightPublishAutomation）经账号 WebView2 的 CDP 端点
/// ConnectOverCDP，跑 P1 已验证的发表流程：上传视频→描述→短标题→封面→原创声明
/// →挂载视频号剧集→（可选）发表。cdpEndpoint 来自 WebView2Host.CdpEndpoint。</summary>
public interface IPublishAutomation
{
    Task<PublishResult> PublishAsync(
        PublishItem item,
        string cdpEndpoint,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct);
}
