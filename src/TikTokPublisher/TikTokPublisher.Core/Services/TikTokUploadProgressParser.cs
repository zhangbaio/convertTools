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

    private static readonly Regex SeriesEpisodeRatioPattern =
        new(@"剧集正片\s*(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    // 翻译包没加载时，工具条原文是 i18n key，紧跟着筛选「全部 (N)」。
    private static readonly Regex EpisodeToolbarCountPattern =
        new(@"contentPartnerHub_seriesStatus_episodeCountValue\s*全部\s*[（(](\d+)[)）]", RegexOptions.Compiled);

    private static readonly (string Key, string Text)[] UntranslatedPageKeys =
    {
        ("contentPartnerHub_selfUpload_statusProcessing", "处理中"),
        ("contentPartnerHub_selfUpload_statusUploading", "上传中"),
        ("contentPartnerHub_selfUpload_statusWaiting", "等待中"),
        ("contentPartnerHub_selfUpload_statusChecking", "检测中"),
        ("contentPartnerHub_selfUpload_statusCheckFailed", "检测未通过"),
        ("contentPartnerHub_seriesStatus_episodeN", " "),
    };

    private static readonly Regex EpisodeLinePattern =
        new(@"(?:^|\n)\s*第\s*\d+\s*集", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex EpisodeNumberPattern =
        new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    // 一张卡片的正文以行首「第 N 集」开头；文件名里的「-第N集」不另起一张卡。
    private static readonly Regex EpisodeCardBoundaryPattern =
        new(@"(?:^|\n)(?=\s*第\s*\d+\s*集)", RegexOptions.Compiled);

    private static readonly Regex EpisodeFileNumberPattern =
        new(@"-\s*第\s*(\d+)\s*集", RegexOptions.Compiled);

    private static readonly Regex VideoFilePattern =
        new(@"\.(?:mp4|mov|m4v|webm)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] CompletedStatusMarkers =
    {
        "草稿",
        "已上传",
        "上传完成",
        "检测中",
        "Draft",
        "Uploaded",
        "Upload complete",
        "Completed",
    };

    public static int? ExtractReadyUploadedVideoCount(string bodyText, IReadOnlyList<string>? titleCandidates)
    {
        bodyText = TranslateUntranslatedPageKeys(bodyText);
        var normalizedCandidates = (titleCandidates ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();

        var ratioCount = ExtractSeriesEpisodeRatioCount(bodyText);
        if (ratioCount is not null)
            return ratioCount;

        var toolbarCount = ExtractEpisodeToolbarCount(bodyText);
        if (toolbarCount is not null)
            return toolbarCount;

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
        var oldEmptyPrompt = value.Contains("正片内容", StringComparison.Ordinal) &&
                             value.Contains("点击上传或拖拽视频到此处", StringComparison.Ordinal);
        var newEmptyPrompt = value.Contains("点击或拖拽视频文件到此处上传", StringComparison.Ordinal) ||
                             value.Contains("点击或拖拽视频文件到此处", StringComparison.Ordinal);
        return (oldEmptyPrompt || newEmptyPrompt) &&
               ExtractUploadedHeadingCount(value) is null &&
               !EpisodeLinePattern.IsMatch(value);
    }

    public static TikTokUploadActivity DetectUploadActivity(
        string bodyText,
        IReadOnlyList<string>? tableTexts)
    {
        var videoTableTexts = (tableTexts ?? Array.Empty<string>())
            .Select(TranslateUntranslatedPageKeys)
            .Where(LooksLikeVideoUploadTable)
            .ToArray();
        var tableScoped = videoTableTexts.Length > 0;
        var statusText = tableScoped
            ? string.Join('\n', videoTableTexts)
            : TranslateUntranslatedPageKeys(bodyText);

        return new TikTokUploadActivity(
            ActiveUploadStatusMarkers.Any(
                marker => statusText.Contains(marker, StringComparison.OrdinalIgnoreCase)),
            CountOccurrences(statusText, "等待中") +
            CountOccurrences(statusText, "等待上传"),
            tableScoped);
    }

    /// <summary>卡片上的真实集号。优先取文件名「-第N集」，否则取最后一个「第N集」。</summary>
    public static int ReadEpisodeCardNumber(string? cardText)
    {
        var text = cardText ?? "";
        var file = EpisodeFileNumberPattern.Match(text);
        if (file.Success && int.TryParse(file.Groups[1].Value, out var fromFile) && fromFile > 0)
            return fromFile;

        var matches = EpisodeNumberPattern.Matches(text);
        if (matches.Count == 0)
            return 0;
        return int.TryParse(matches[^1].Groups[1].Value, out var episode) && episode > 0 ? episode : 0;
    }

    public static bool EpisodeCardUploadFailed(string? cardText)
    {
        var text = cardText ?? "";
        return text.Contains("上传失败", StringComparison.Ordinal) ||
               text.Contains("Upload failed", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> SplitEpisodeCardTexts(IEnumerable<string>? texts)
    {
        var cards = new List<string>();
        foreach (var raw in texts ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var parts = EpisodeCardBoundaryPattern
                .Split(raw)
                .Where(part => EpisodeNumberPattern.IsMatch(part))
                .ToArray();
            if (parts.Length == 0)
                cards.Add(raw);
            else
                cards.AddRange(parts);
        }

        return cards;
    }

    public static IReadOnlyList<int> FailedEpisodeNumbers(IEnumerable<string>? cardTexts) =>
        SplitEpisodeCardTexts(cardTexts)
            .Where(EpisodeCardUploadFailed)
            .Select(ReadEpisodeCardNumber)
            .Where(episode => episode > 0)
            .Distinct()
            .OrderBy(episode => episode)
            .ToArray();

    public static bool BatchEpisodesAreOnPage(IEnumerable<string>? cardTexts, IReadOnlyCollection<int> episodes)
    {
        var wanted = episodes.Where(episode => episode > 0).Distinct().ToArray();
        if (wanted.Length == 0)
            return false;

        var present = SplitEpisodeCardTexts(cardTexts)
            .Select(ReadEpisodeCardNumber)
            .Where(episode => episode > 0)
            .ToHashSet();
        return wanted.All(present.Contains);
    }

    public static bool CurrentBatchUploadFailed(IEnumerable<string>? cardTexts, IReadOnlyCollection<int> episodes)
    {
        var wanted = episodes.Where(episode => episode > 0).ToHashSet();
        if (wanted.Count == 0)
            return false;

        return SplitEpisodeCardTexts(cardTexts).Any(card =>
            EpisodeCardUploadFailed(card) && wanted.Contains(ReadEpisodeCardNumber(card)));
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
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
            return Math.Max(0, count);

        return ExtractSeriesEpisodeRatioCount(bodyText ?? "");
    }

    private static string TranslateUntranslatedPageKeys(string? bodyText)
    {
        var value = bodyText ?? "";
        foreach (var (key, text) in UntranslatedPageKeys)
            value = value.Replace(key, text, StringComparison.Ordinal);
        return value;
    }

    private static int? ExtractEpisodeToolbarCount(string bodyText)
    {
        var match = EpisodeToolbarCountPattern.Match(bodyText ?? "");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count) && count >= 0)
            return count;
        return null;
    }

    private static int? ExtractSeriesEpisodeRatioCount(string bodyText)
    {
        var match = SeriesEpisodeRatioPattern.Match(bodyText ?? "");
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var ready) ||
            !int.TryParse(match.Groups[2].Value, out var total))
        {
            return null;
        }

        if (total <= 0 || ready < 0 || ready > total)
            return null;

        return ready;
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

            var statusText = line + "\n" + CollectEpisodeStatusText(lines, lineIndex + 1);
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
