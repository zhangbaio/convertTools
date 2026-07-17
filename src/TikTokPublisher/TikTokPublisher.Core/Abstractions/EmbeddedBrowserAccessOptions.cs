namespace TikTokPublisher.Core.Abstractions;

/// <summary>控制获取内置浏览器会话时的 UI 行为。</summary>
public sealed class EmbeddedBrowserAccessOptions
{
    /// <summary>是否将对应账号浏览器切到前台（队列/多账号后台上传应为 false）。</summary>
    public bool BringToFront { get; init; }

    public static EmbeddedBrowserAccessOptions Background => new() { BringToFront = false };

    public static EmbeddedBrowserAccessOptions Interactive => new() { BringToFront = true };
}
