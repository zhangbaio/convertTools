namespace TikTokPublisher.Core.Abstractions;

/// <summary>每账号一个内嵌浏览器会话（对应参考图右侧的浏览器区）。
///
/// P1 已验证的落地路线：内嵌浏览器用 WebView2（Edge 内核，含 H.264/AAC 专有编解码器，
/// 通过TikTok 短剧中心「浏览器格式校验」），以独立 UserDataFolder 隔离每账号登录态；启动时带
/// remote-debugging，暴露 CDP 端点供 PuppeteerSharp/Playwright 连上去驱动自动发布
/// （TikTok 短剧中心发表表单在 wujie 微前端的 open Shadow DOM 里，需能穿透 shadow 的引擎）。
///
/// P0 先定义此抽象，UI/账号/会话骨架都面向它编程；联网后加 Microsoft.Web.WebView2
/// 实现该接口并替换占位视图即可接入，不动上层。</summary>
public interface IEmbeddedBrowser
{
    /// <summary>该账号的独立会话目录（WebView2 UserDataFolder）。</summary>
    string UserDataFolder { get; }

    /// <summary>CDP 端点（如 http://127.0.0.1:&lt;port&gt;）；未就绪时为 null。自动发布经此驱动。</summary>
    string? CdpEndpoint { get; }

    /// <summary>导航到指定地址（如TikTok 短剧中心登录页/发表页）。</summary>
    Task NavigateAsync(string url);
}
