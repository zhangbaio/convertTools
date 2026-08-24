using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

/// <summary>解析 TikTok 上传页正文中的已就绪集数（对齐 Python <c>browser_actions.py</c>）。</summary>
public static class TikTokUploadProgressParser
{
    private static readonly string[] ActiveUploadStatusMarkers =
    {
        "上传中",
        "正在上传",
        "处理中",
        "Transcoding",
        "Uploading",
        "Processing",
    };

    private static readonly Regex UploadedContentCountPattern =
        new(@"正片内容\s*[\(（](\d+)[\)）]", RegexOptions.Compiled);

    private static readonly Regex EpisodeLinePattern =
        new(@"(?:^|\n)\s*第\s*\d+\s*集", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex EpisodeNumberPattern =
        new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    private static readonly Regex VideoFilePattern =
        new(@"\.(?:mp4|mov|m4v|webm)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] CompletedStatusMarkers =
    {
        "草稿",
        "已上传",
        "上传完成",
        "Draft",
        "Uploaded",
        "Upload complete",
        "Completed",
    };

    public static int? ExtractReadyUploadedVideoCount(string bodyText, IReadOnlyList<string>? titleCandidates)
    {
        var normalizedCandidates = (titleCandidates ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();

        var completedIndexes = ExtractCompletedUploadedEpisodeIndexes(bodyText, normalizedCandidates);
        if (completedIndexes.Count > 0)
            return completedIndexes.Count;

        if (normalizedCandidates.Count > 0)
        {
            var indexes = ExtractUploadedEpisodeIndexesMatchingTitles(bodyText, normalizedCandidates);
            if (indexes.Count > 0)
                return null;
        }
        else
        {
            var indexes = ExtractUploadedEpisodeIndexes(bodyText, null);
            if (indexes.Count > 0)
                return null;
        }

        var headingCount = ExtractUploadedHeadingCount(bodyText);
        if (headingCount is not null)
            return headingCount;

        if (normalizedCandidates.Count > 0)
            return null;

        return ExtractUploadedVideoCountFromEpisodeLines(bodyText);
    }

    public static int EstimateDisplayPercent(int? uploadedCount, int expectedCount, int waitingCount)
    {
        if (expectedCount <= 0)
            return 0;

        if (uploadedCount is not null)
            return Math.Min(100, Math.Max(0, (int)Math.Round(uploadedCount.Value / (double)expectedCount * 100)));

        // With no ready count, waiting=0 is ambiguous: it also describes an empty upload
        // control or an unrelated page. Reporting 100% in that state is actively misleading.
        if (waitingCount > 0 && waitingCount <= expectedCount)
        {
            return Math.Min(
                100,
                Math.Max(0, (int)Math.Round((expectedCount - waitingCount) / (double)expectedCount * 100)));
        }

        return 0;
    }

    public static bool IsClearlyEmptyUploadQueue(
        string bodyText,
        int? readyCount,
        TikTokUploadActivity activity)
    {
        if (readyCount is not null || activity.Uploading || activity.WaitingCount > 0)
            return false;

        var value = bodyText ?? "";
        return value.Contains("正片内容", StringComparison.Ordinal) &&
               value.Contains("点击上传或拖拽视频到此处", StringComparison.Ordinal) &&
               ExtractUploadedHeadingCount(value) is null &&
               !EpisodeLinePattern.IsMatch(value);
    }

    public static TikTokUploadActivity DetectUploadActivity(
        string bodyText,
        IReadOnlyList<string>? tableTexts)
    {
        var videoTableTexts = (tableTexts ?? Array.Empty<string>())
            .Where(LooksLikeVideoUploadTable)
            .ToArray();
        var tableScoped = videoTableTexts.Length > 0;
        var statusText = tableScoped
            ? string.Join('\n', videoTableTexts)
            : bodyText ?? "";

        return new TikTokUploadActivity(
            ActiveUploadStatusMarkers.Any(
                marker => statusText.Contains(marker, StringComparison.OrdinalIgnoreCase)),
            CountOccurrences(statusText, "等待中") +
            CountOccurrences(statusText, "等待上传"),
            tableScoped);
    }

    public static bool IsUploadComplete(
        int? readyCount,
        int expectedCount,
        TikTokUploadActivity activity) =>
        readyCount is not null &&
        readyCount.Value >= Math.Max(1, expectedCount) &&
        activity.WaitingCount == 0 &&
        !activity.Uploading;

    private static int? ExtractUploadedHeadingCount(string bodyText)
    {
        var match = UploadedContentCountPattern.Match(bodyText ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out var count)
            ? Math.Max(0, count)
            : null;
    }

    private static int? ExtractUploadedVideoCountFromEpisodeLines(string bodyText)
    {
        var headingCount = ExtractUploadedHeadingCount(bodyText);
        if (headingCount is not null)
            return headingCount;

        var matches = EpisodeLinePattern.Matches(bodyText ?? "");
        return matches.Count > 0 ? matches.Count : null;
    }

    private static List<int> ExtractUploadedEpisodeIndexes(string bodyText, IReadOnlyList<string>? titleCandidates)
    {
        var normalizedCandidates = (titleCandidates ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();
        if (normalizedCandidates.Count > 0)
        {
            var filtered = ExtractUploadedEpisodeIndexesMatchingTitles(bodyText, normalizedCandidates);
            if (filtered.Count > 0)
                return filtered;
        }

        var values = new List<int>();
        var seen = new HashSet<int>();
        foreach (Match match in EpisodeNumberPattern.Matches(bodyText ?? ""))
        {
            if (!int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || !seen.Add(value))
                continue;
            values.Add(value);
        }

        return values;
    }

    private static List<int> ExtractUploadedEpisodeIndexesMatchingTitles(
        string bodyText,
        IReadOnlyList<string> titleCandidates)
    {
        var filtered = new List<int>();
        var seen = new HashSet<int>();
        foreach (var rawLine in (bodyText ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (!titleCandidates.Any(candidate => line.Contains(candidate, StringComparison.Ordinal)))
                continue;

            var match = EpisodeNumberPattern.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || !seen.Add(value))
                continue;

            filtered.Add(value);
        }

        return filtered;
    }

    public static List<int> ExtractCompletedUploadedEpisodeIndexes(
        string bodyText,
        IReadOnlyList<string> titleCandidates)
    {
        var lines = (bodyText ?? "").Split('\n').Select(line => line.Trim()).ToArray();
        var indexes = new List<int>();
        var seen = new HashSet<int>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0)
                continue;
            if (titleCandidates.Count > 0 &&
                !titleCandidates.Any(candidate => line.Contains(candidate, StringComparison.Ordinal)))
            {
                continue;
            }

            var match = EpisodeNumberPattern.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || !seen.Add(value))
                continue;

            var statusText = CollectEpisodeStatusText(lines, lineIndex + 1);
            if (!IsCompletedUploadStatusText(statusText))
                continue;

            indexes.Add(value);
        }

        return indexes;
    }

    private static string CollectEpisodeStatusText(string[] lines, int startIndex, int maxLines = 12)
    {
        var collected = new List<string>();
        var upperBound = Math.Min(lines.Length, startIndex + Math.Max(1, maxLines));
        for (var index = startIndex; index < upperBound; index++)
        {
            var line = lines[index];
            if (EpisodeNumberPattern.IsMatch(line))
                break;
            if (line.Length > 0)
                collected.Add(line);
        }

        return string.Join('\n', collected);
    }

    private static bool IsCompletedUploadStatusText(string text) =>
        CompletedStatusMarkers.Any(marker => (text ?? "").Contains(marker, StringComparison.Ordinal));

    private static bool LooksLikeVideoUploadTable(string text)
    {
        var value = text ?? "";
        return EpisodeNumberPattern.IsMatch(value) ||
               VideoFilePattern.IsMatch(value) ||
               value.Contains("正片内容", StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

public readonly record struct TikTokUploadActivity(
    bool Uploading,
    int WaitingCount,
    bool IsTableScoped);
