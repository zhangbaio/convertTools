namespace TikTokPublisher.Core.Publishing;

public static class TikTokPlatformSubmitFailureDetector
{
    private static readonly string[] TemporaryFailureMarkers =
    [
        "操作失败请重试",
        "操作失败，请重试",
        "操作失败 请重试",
    ];

    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));
        return TemporaryFailureMarkers.FirstOrDefault(marker =>
            normalized.Contains(
                string.Concat(marker.Where(ch => !char.IsWhiteSpace(ch))),
                StringComparison.Ordinal));
    }

    public static string BuildRetryMessage(string platformText) =>
        $"TikTok 平台暂时性提交失败：{platformText}。这是平台端临时异常，请稍后重新执行当前项目。";
}
