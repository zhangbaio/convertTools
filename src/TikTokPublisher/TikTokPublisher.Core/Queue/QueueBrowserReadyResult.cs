namespace TikTokPublisher.Core.Queue;

public sealed record QueueBrowserReadyResult(bool Ok, string Message = "")
{
    public static QueueBrowserReadyResult Ready() => new(true);

    public static QueueBrowserReadyResult NotReady(string message) =>
        new(false, string.IsNullOrWhiteSpace(message)
            ? "内置浏览器未就绪或未登录，请先在「浏览器」页完成登录"
            : message.Trim());
}
