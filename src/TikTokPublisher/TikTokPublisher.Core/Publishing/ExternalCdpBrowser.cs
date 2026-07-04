using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

/// <summary>外部浏览器（指纹浏览器等）上传通道：直接经账号配置的 CDP 端点接入自动化，不使用内置 WebView2。</summary>
public sealed class ExternalCdpBrowser : IEmbeddedBrowser
{
    public ExternalCdpBrowser(TikTokAccountProfile account)
    {
        UserDataFolder = account.ProfileDir;
        var endpoint = (account.TiktokFingerprintBrowserCdpEndpoint ?? "").Trim();
        CdpEndpoint = endpoint.Length > 0 ? endpoint : null;
    }

    public string UserDataFolder { get; }

    public string? CdpEndpoint { get; }

    /// <summary>外部浏览器由 Playwright 连接后统一导航，此处无需预导航。</summary>
    public Task NavigateAsync(string url) => Task.CompletedTask;
}
