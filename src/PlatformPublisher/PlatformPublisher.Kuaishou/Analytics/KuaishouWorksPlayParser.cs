using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Analytics;

public static class KuaishouWorksPlayParser
{
    private static readonly string[] DateNames = ["date", "statDate", "stat_date", "time", "dateTime", "datetime", "day", "pDate", "dataDate", "recordDate", "ds"];
    private static readonly string[] PlayNames = ["playCnt", "playCount", "play_count", "viewCount", "view_count", "displayPlayCnt", "photoPlayCnt", "validPlayCnt", "play"];
    private static readonly string[] GenericValueNames = ["value", "count", "num", "data", "cnt"];
    private static readonly string[] VideoNames = ["photoId", "photo_id", "caption", "coverUrl", "videoId", "playUrl"];
    private static readonly Regex PlayCardPattern = new(@"播放量(?!分析)\s*([0-9]+(?:\.[0-9]+)?(?:万|亿)?)", RegexOptions.Compiled);
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public static Dictionary<DateOnly, long> Parse(string json)
    {
        var result = new Dictionary<DateOnly, long>();
        Merge(result, json);
        return result;
    }

    public static void Merge(IDictionary<DateOnly, long> target, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, "", target);
        }
        catch (JsonException)
        {
        }
    }

    public static long? ReadLabeledPlay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = PlayCardPattern.Match(text);
        if (!match.Success) return null;
        try { return Whole(KuaishouAnalyticsCollector.ParseNumber(match.Groups[1].Value)); }
        catch (FormatException) { return null; }
    }

    public static bool ShowsSingleDay(string text, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var compact = Regex.Replace(text, @"\s+", "");
        return compact.Contains(iso + "~" + iso, StringComparison.Ordinal)
            || compact.Contains(iso + "至" + iso, StringComparison.Ordinal)
            || compact.Contains(iso + "到" + iso, StringComparison.Ordinal);
    }

    private static void Walk(JsonElement element, string parent, IDictionary<DateOnly, long> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                TryReadDateMap(element, parent, target);
                TryReadRange(element, target);
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, property.Name, target);
                break;
            case JsonValueKind.Array:
                if (!TryReadSeries(element, parent, target))
                {
                    foreach (var item in element.EnumerateArray())
                        Walk(item, parent, target);
                }
                break;
        }
    }

    private static bool TryReadSeries(JsonElement array, string parent, IDictionary<DateOnly, long> target)
    {
        var points = new List<(DateOnly Date, long Views)>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || LooksLikeVideo(item)) return false;
            if (!TryReadPoint(item, parent, out var date, out var views)) return false;
            points.Add((date, views));
        }
        if (points.Count == 0 || points.Select(point => point.Date).Distinct().Count() != points.Count) return false;
        foreach (var point in points) target[point.Date] = point.Views;
        return true;
    }

    private static bool TryReadPoint(JsonElement item, string parent, out DateOnly date, out long views)
    {
        date = default;
        views = 0;
        DateOnly? foundDate = null;
        foreach (var property in item.EnumerateObject())
        {
            if (IsNamed(property.Name, DateNames) && TryDate(property.Value, out var parsed))
                foundDate = parsed;
        }
        if (foundDate is null) return false;
        long? foundViews = null;
        foreach (var property in item.EnumerateObject())
        {
            if (IsPlayName(property.Name, parent) && TryLong(property.Value, out var parsed))
                foundViews = parsed;
        }
        if (foundViews is null) return false;
        date = foundDate.Value;
        views = foundViews.Value;
        return true;
    }

    private static void TryReadDateMap(JsonElement element, string parent, IDictionary<DateOnly, long> target)
    {
        if (!ParentIsPlay(parent)) return;
        var points = new Dictionary<DateOnly, long>();
        foreach (var property in element.EnumerateObject())
        {
            if (!TryDate(property.Name, out var date) || !TryLong(property.Value, out var views)) return;
            points[date] = views;
        }
        if (points.Count == 0) return;
        foreach (var pair in points) target[pair.Key] = pair.Value;
    }

    private static void TryReadRange(JsonElement element, IDictionary<DateOnly, long> target)
    {
        if (!TryProperty(element, out var start, "startDate", "beginDate", "startTime", "fromTime") ||
            !TryProperty(element, out var end, "endDate", "endTime", "toTime") ||
            !TryDate(start, out var startDate) ||
            !TryDate(end, out var endDate) ||
            startDate != endDate)
            return;
        foreach (var property in element.EnumerateObject())
        {
            if (IsNamed(property.Name, PlayNames) && TryLong(property.Value, out var views))
            {
                target[startDate] = views;
                return;
            }
        }
    }

    private static bool LooksLikeVideo(JsonElement item)
    {
        foreach (var property in item.EnumerateObject())
            if (IsNamed(property.Name, VideoNames)) return true;
        return false;
    }

    private static bool IsPlayName(string name, string parent) =>
        IsNamed(name, PlayNames) || (ParentIsPlay(parent) && IsNamed(name, GenericValueNames));

    private static bool ParentIsPlay(string parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return false;
        if (parent.Contains("点赞", StringComparison.Ordinal) || parent.Contains("评论", StringComparison.Ordinal) ||
            parent.Contains("粉丝", StringComparison.Ordinal) || parent.Contains("like", StringComparison.OrdinalIgnoreCase) ||
            parent.Contains("comment", StringComparison.OrdinalIgnoreCase))
            return false;
        return parent.Contains("播放", StringComparison.Ordinal) ||
               parent.Contains("play", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!IsNamed(property.Name, names)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryDate(JsonElement element, out DateOnly date)
    {
        date = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryDate(element.GetString(), out date);
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var number)) return false;
                return TryDateNumber(number, out date);
            default:
                return false;
        }
    }

    private static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length == 8 && trimmed.All(char.IsDigit) &&
            DateOnly.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && TryDateNumber(number, out date))
            return true;
        try
        {
            date = KuaishouAnalyticsCollector.ParseDate(trimmed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDateNumber(long number, out DateOnly date)
    {
        date = default;
        if (number is >= 20000101 and <= 21001231)
        {
            var text = number.ToString(CultureInfo.InvariantCulture);
            return DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        if (number is >= 1_000_000_000_000 and <= 9_000_000_000_000)
        {
            date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(number).ToOffset(ChinaOffset).DateTime);
            return true;
        }
        if (number is >= 1_000_000_000 and <= 9_000_000_000)
        {
            date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(number).ToOffset(ChinaOffset).DateTime);
            return true;
        }
        return false;
    }

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var whole) && whole >= 0)
                {
                    value = whole;
                    return true;
                }
                if (element.TryGetDecimal(out var number) && number >= 0)
                {
                    value = Whole(number);
                    return true;
                }
                return false;
            case JsonValueKind.String:
                try
                {
                    value = Whole(KuaishouAnalyticsCollector.ParseNumber(element.GetString() ?? ""));
                    return value >= 0;
                }
                catch (FormatException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private static bool IsNamed(string name, params string[] expected) =>
        expected.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    private static long Whole(decimal value) => checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
}
