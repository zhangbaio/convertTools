using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

/// <summary>标记「上传时由程序用 Playwright 独立启动浏览器」的载体；实际浏览器由发布自动化内部 launch。
/// 不走 CDP 连接，故 CdpEndpoint 恒为 null。</summary>
public sealed class PlaywrightLaunchBrowser : IEmbeddedBrowser
{
    public PlaywrightLaunchBrowser(TikTokAccountProfile account) => UserDataFolder = account.ProfileDir;

    public string UserDataFolder { get; }

    public string? CdpEndpoint => null;

    public Task NavigateAsync(string url) => Task.CompletedTask;
}
