namespace TikTokPublisher.Core.Publishing;

/// <summary>
/// 剧集上传两套独立方案。网页上传在剧集中心页面填表；接口上传只调用内容合作方接口。
/// 旧值 classic、batch 归到网页上传，并保留原来的正片投递方式。
/// </summary>
public static class TikTokUploadChannels
{
    public const string Api = "api";
    public const string Playwright = "playwright";
    public const string Classic = "classic";
    public const string Batch = "batch";

    public readonly record struct Selection(string Channel, string Delivery);

    public static Selection Resolve(string? strategy, string? delivery)
    {
        var value = strategy?.Trim() ?? "";
        if (value.Equals(Classic, StringComparison.OrdinalIgnoreCase))
            return new Selection(Playwright, Classic);
        if (value.Equals(Batch, StringComparison.OrdinalIgnoreCase))
            return new Selection(Playwright, Batch);
        if (value.Equals(Playwright, StringComparison.OrdinalIgnoreCase))
        {
            return new Selection(
                Playwright,
                (delivery ?? "").Trim().Equals(Batch, StringComparison.OrdinalIgnoreCase) ? Batch : Classic);
        }

        return new Selection(Api, Classic);
    }

    public static bool IsApi(string? strategy)
    {
        var value = strategy?.Trim() ?? "";
        return value.Length == 0 || value.Equals(Api, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBatchDelivery(string? strategy, string? delivery)
    {
        var value = strategy?.Trim() ?? "";
        if (value.Equals(Batch, StringComparison.OrdinalIgnoreCase))
            return true;
        return value.Equals(Playwright, StringComparison.OrdinalIgnoreCase) &&
               (delivery ?? "").Trim().Equals(Batch, StringComparison.OrdinalIgnoreCase);
    }
}
