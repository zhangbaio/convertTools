namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouFormChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// 与短剧助手快手配置页下拉框一致的显示文案和保存值。
/// </summary>
public static class KuaishouFormChoices
{
    public static IReadOnlyList<KuaishouFormChoice> ContentTypes { get; } =
    [
        new("短剧", "短剧"),
        new("漫剧", "漫剧"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> ComicMethods { get; } =
    [
        new("简笔动画", "1"),
        new("小说剧", "2"),
        new("AIGC剧", "3"),
        new("沙雕动画", "4"),
        new("3D动画", "5"),
        new("2D动画", "6"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> AuthorDeclarations { get; } =
    [
        new("含AI生成内容", "1"),
        new("内容无需添加声明（作品不涉及AI生成）", "0"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> ProductionForms { get; } =
    [
        new("自动识别", "auto"),
        new("横屏", "横屏"),
        new("竖屏", "竖屏"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> SaleTypes { get; } =
    [
        new("全剧付费", "0"),
        new("单集+全剧付费", "2"),
        new("观看广告解锁", "3"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> CopyrightProofTypes { get; } =
    [
        new("自有版权", "1"),
        new("授权版权", "2"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> BroadcastChannels { get; } =
    [
        new("大屏有线电视APP", "大屏有线电视APP"),
        new("大屏有线电视SDK", "大屏有线电视SDK"),
        new("大屏IPTV APP", "大屏IPTV APP"),
        new("大屏IPTV SDK", "大屏IPTV SDK"),
        new("大屏OTT APP", "大屏OTT APP"),
        new("大屏OTT SDK", "大屏OTT SDK"),
        new("小屏小程序", "小屏小程序"),
        new("小屏APP", "小屏APP"),
        new("PC端", "PC端"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> BroadcastTimeModes { get; } =
    [
        new("今日", "today"),
        new("指定日期", "fixed"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> Genders { get; } =
    [
        new("未设置", ""),
        new("男", "男"),
        new("女", "女"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> BrowserModes { get; } =
    [
        new("有头浏览器", "false"),
        new("无头浏览器", "true"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> FirstPageActions { get; } =
    [
        new("下一步", "next"),
        new("保存草稿", "draft"),
        new("保持", "keep"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> FinalActions { get; } =
    [
        new("提交审核", "submit_review"),
        new("保持", "keep"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> AliasPositions { get; } =
    [
        new("后缀", "suffix"),
        new("前缀", "prefix"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> SubmitPreWaits { get; } =
    [
        new("自动（根据集数和视频大小计算）", "-1"),
        new("不等待", "0"),
        new("60 秒", "60"),
        new("120 秒", "120"),
        new("180 秒", "180"),
        new("300 秒", "300"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> VideoStorageProviders { get; } =
    [
        new("阿里云 OSS", "aliyun_oss"),
        new("七牛云 Kodo", "qiniu_kodo"),
    ];

    public static IReadOnlyList<KuaishouFormChoice> DistributionModes { get; } =
    [
        new("官方 API（推荐）", "api"),
        new("网页自动化（原方案）", "browser"),
    ];

    public static string CanonicalValue(IReadOnlyList<KuaishouFormChoice> choices, string? raw, string fallback)
    {
        var text = (raw ?? string.Empty).Trim();
        var match = choices.FirstOrDefault(choice =>
            string.Equals(choice.Value, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(choice.Label, text, StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? (string.IsNullOrWhiteSpace(text) ? fallback : text);
    }

    public static int SaleTypeCode(string? raw)
    {
        var value = CanonicalValue(SaleTypes, raw, "3");
        return int.TryParse(value, out var code) ? code : 3;
    }

    public static string LabelOf(IReadOnlyList<KuaishouFormChoice> choices, string? raw, string fallbackValue)
    {
        var value = CanonicalValue(choices, raw, fallbackValue);
        return choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))?.Label
            ?? (string.IsNullOrWhiteSpace(raw) ? fallbackValue : raw.Trim());
    }
}
