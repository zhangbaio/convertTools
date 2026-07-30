namespace TikTokPublisher.Core.Services;

public enum TikTokPublishedSeriesMatchKind
{
    Published,
    NotPublished,
    Missing,
    Conflict,
    Failed,
}

public sealed record TikTokPublishedSeriesMatch(
    string InputTitle,
    TikTokPublishedSeriesMatchKind Kind,
    string PlatformStatus = "",
    string SeriesId = "",
    string DetailUrl = "",
    string Message = "")
{
    public bool IsPublished => Kind == TikTokPublishedSeriesMatchKind.Published;
}

public static class TikTokPublishedSeriesMatchText
{
    private static readonly TikTokPublishedSeriesMatchKind[] DisplayOrder =
    [
        TikTokPublishedSeriesMatchKind.Published,
        TikTokPublishedSeriesMatchKind.NotPublished,
        TikTokPublishedSeriesMatchKind.Missing,
        TikTokPublishedSeriesMatchKind.Conflict,
        TikTokPublishedSeriesMatchKind.Failed,
    ];

    public static IReadOnlyList<string> ParseNewTitles(string? input) =>
        (input ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(title => !string.IsNullOrWhiteSpace(title))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static bool IsPublishedStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        return string.Equals(value, "已发布", StringComparison.Ordinal) ||
               string.Equals(value, "Published", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildPublishedTitlesCopyText(
        IEnumerable<TikTokPublishedSeriesMatch> matches) =>
        string.Join(
            Environment.NewLine,
            matches
                .Where(match => match.IsPublished)
                .Select(match => match.InputTitle));

    public static string BuildAllResultsCopyText(
        IEnumerable<TikTokPublishedSeriesMatch> matches)
    {
        var lines = new List<string>
        {
            "匹配结果\t新剧名\t平台状态\t剧集ID\t说明",
        };
        lines.AddRange(matches.Select(match => string.Join(
            '\t',
            KindLabel(match.Kind),
            SanitizeCell(match.InputTitle),
            SanitizeCell(match.PlatformStatus),
            SanitizeCell(match.SeriesId),
            SanitizeCell(match.Message))));
        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildDisplayText(
        IEnumerable<TikTokPublishedSeriesMatch> matches)
    {
        var items = matches.ToArray();
        var sections = new List<string>();
        foreach (var kind in DisplayOrder)
        {
            var group = items.Where(match => match.Kind == kind).ToArray();
            if (group.Length == 0)
                continue;

            var lines = new List<string> { $"【{KindLabel(kind)}（{group.Length}）】" };
            lines.AddRange(group.Select(FormatDisplayLine));
            sections.Add(string.Join(Environment.NewLine, lines));
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections);
    }

    public static string KindLabel(TikTokPublishedSeriesMatchKind kind) =>
        kind switch
        {
            TikTokPublishedSeriesMatchKind.Published => "已发布",
            TikTokPublishedSeriesMatchKind.NotPublished => "未发布",
            TikTokPublishedSeriesMatchKind.Missing => "未找到",
            TikTokPublishedSeriesMatchKind.Conflict => "同名冲突",
            TikTokPublishedSeriesMatchKind.Failed => "查询失败",
            _ => "未知",
        };

    private static string FormatDisplayLine(TikTokPublishedSeriesMatch match)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(match.PlatformStatus))
            details.Add(match.PlatformStatus.Trim());
        if (!string.IsNullOrWhiteSpace(match.SeriesId))
            details.Add($"ID {match.SeriesId.Trim()}");
        if (!string.IsNullOrWhiteSpace(match.Message))
            details.Add(match.Message.Trim());
        return details.Count == 0
            ? match.InputTitle
            : $"{match.InputTitle}    [{string.Join("；", details)}]";
    }

    private static string SanitizeCell(string? value) =>
        (value ?? string.Empty)
        .Replace('\t', ' ')
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
}
