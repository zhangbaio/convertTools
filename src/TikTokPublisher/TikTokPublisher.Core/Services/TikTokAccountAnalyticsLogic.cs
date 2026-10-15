using System.Globalization;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokAccountAnalyticsLogic
{
    public static readonly DateOnly EarliestDate = new(2026, 3, 1);
    public const int MaxWindowDays = 180;

    public static DateOnly NextDailyStart(DateOnly? latestStored, DateOnly today)
    {
        var earliest = EarliestDate > today ? today : EarliestDate;
        if (latestStored is null)
            return earliest;

        var start = latestStored.Value;
        if (start < earliest) start = earliest;
        if (start > today) start = today;
        return start;
    }

    public static IEnumerable<(DateOnly Start, DateOnly End)> Windows(DateOnly start, DateOnly end)
    {
        if (end < start) yield break;
        var cursor = start;
        while (cursor <= end)
        {
            var windowEnd = cursor.AddDays(MaxWindowDays - 1);
            if (windowEnd > end) windowEnd = end;
            yield return (cursor, windowEnd);
            cursor = windowEnd.AddDays(1);
        }
    }

    public static IReadOnlyList<TikTokAccountDailyStat> ParseDailyStats(string json, DateOnly requestedStart, DateOnly requestedEnd)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        EnsureSuccess(root);
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("dailyRows", out var rows))
            throw new InvalidOperationException("剧集表现没有返回每日播放记录。");

        var parsed = new Dictionary<DateOnly, TikTokAccountDailyStat>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!DateOnly.TryParse(ReadString(row, "eventDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            if (date < requestedStart || date > requestedEnd) continue;
            var metrics = row.TryGetProperty("metrics", out var node) ? node : row;
            parsed[date] = new TikTokAccountDailyStat(date, ReadLong(metrics, "vv"), ReadLong(metrics, "innerfeedVv"));
        }

        var latest = ReadDate(data, "latestEventDate");
        var last = requestedEnd < latest ? requestedEnd : latest;
        if (last < requestedStart) return [];

        var result = new List<TikTokAccountDailyStat>();
        for (var date = requestedStart; date <= last; date = date.AddDays(1))
            result.Add(parsed.GetValueOrDefault(date) ?? new TikTokAccountDailyStat(date, 0, 0));
        return result;
    }

    public static (IReadOnlyList<TikTokSeriesSnapshot> Items, long Total) ParseSeriesPage(string json, DateOnly snapshotDate)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        EnsureSuccess(root);
        var data = root.GetProperty("data");
        var total = ReadLong(data, "total");
        var items = new List<TikTokSeriesSnapshot>();
        if (!data.TryGetProperty("contractBlocks", out var blocks))
            return (items, total);

        foreach (var block in blocks.EnumerateArray())
        {
            var info = block.TryGetProperty("contractInfo", out var infoNode) ? infoNode : default;
            var metrics = block.TryGetProperty("aggregatedMetrics", out var metricNode) ? metricNode : default;
            var seriesId = ReadString(block, "contractID");
            if (string.IsNullOrWhiteSpace(seriesId)) continue;
            var published = ReadUnixDate(info.ValueKind == JsonValueKind.Undefined ? default : info, "publishTimeSec");
            items.Add(new TikTokSeriesSnapshot(
                seriesId,
                ReadString(info.ValueKind == JsonValueKind.Undefined ? default : info, "contractTitle"),
                snapshotDate,
                published,
                ReadLong(metrics.ValueKind == JsonValueKind.Undefined ? default : metrics, "innerfeedVv"),
                (int)Math.Min(int.MaxValue, ReadLong(metrics.ValueKind == JsonValueKind.Undefined ? default : metrics, "avgPlayDurationPerUser")),
                ReadLong(metrics.ValueKind == JsonValueKind.Undefined ? default : metrics, "favouriteCnt"),
                ReadLong(metrics.ValueKind == JsonValueKind.Undefined ? default : metrics, "commentCnt"),
                ReadLong(metrics.ValueKind == JsonValueKind.Undefined ? default : metrics, "likeCnt")));
        }

        return (items, total);
    }

    public static TikTokAccountRevenue ParseRevenue(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        EnsureSuccess(root);
        var data = root.GetProperty("data");
        var balance = data.TryGetProperty("totalBalance", out var total) ? total : default;
        var companies = new List<string>();
        if (data.TryGetProperty("enterprises", out var enterprises))
        {
            foreach (var enterprise in enterprises.EnumerateArray())
            {
                var name = ReadString(enterprise, "name");
                if (!string.IsNullOrWhiteSpace(name)) companies.Add(name);
            }
        }

        return new TikTokAccountRevenue(
            ReadMoney(balance, "totalAmount"),
            ReadMoney(balance, "pendingSettlementAmount"),
            ReadMoney(balance, "paymentProcessingAmount"),
            ReadMoney(balance, "paidAmount"),
            ReadCurrency(balance),
            string.Join("、", companies));
    }

    public static IReadOnlyList<TikTokSeriesGrowthPoint> BuildGrowth(IReadOnlyList<TikTokSeriesSnapshot> snapshots) =>
        BuildGrowth(snapshots, snapshot => snapshot.ValidViews);

    public static IReadOnlyList<TikTokSeriesGrowthPoint> BuildGrowth(
        IReadOnlyList<TikTokSeriesSnapshot> snapshots,
        Func<TikTokSeriesSnapshot, long> value)
    {
        var ordered = snapshots
            .GroupBy(item => item.SnapshotDate)
            .Select(group => group.OrderByDescending(value).First())
            .OrderBy(item => item.SnapshotDate)
            .ToList();
        var points = new List<TikTokSeriesGrowthPoint>();
        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var span = current.SnapshotDate.DayNumber - previous.SnapshotDate.DayNumber;
            if (span <= 0) continue;
            points.Add(new TikTokSeriesGrowthPoint(
                previous.SnapshotDate,
                current.SnapshotDate,
                value(current) - value(previous),
                span));
        }

        return points;
    }

    public static IReadOnlyList<(string Month, long ValidViews)> SumByMonth(IEnumerable<TikTokAccountDailyStat> rows) =>
        rows.GroupBy(row => row.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Sum(row => row.ValidViews)))
            .ToList();

    private static void EnsureSuccess(JsonElement root)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var text) ? text.GetString() : "未知错误";
            throw new InvalidOperationException($"TikTok 返回错误：{message}");
        }
    }

    private static string ReadMoney(JsonElement balance, string name)
    {
        if (balance.ValueKind != JsonValueKind.Object || !balance.TryGetProperty(name, out var money))
            return "0.00";
        if (money.ValueKind == JsonValueKind.Object && money.TryGetProperty("amount", out var amount))
            return amount.GetString() ?? "0.00";
        return money.ToString();
    }

    private static string ReadCurrency(JsonElement balance)
    {
        if (balance.ValueKind == JsonValueKind.Object &&
            balance.TryGetProperty("totalAmount", out var money) &&
            money.TryGetProperty("currency", out var currency))
            return currency.GetString() ?? "USD";
        return "USD";
    }

    private static DateOnly? ReadUnixDate(JsonElement element, string name)
    {
        var seconds = ReadLong(element, name);
        if (seconds <= 0) return null;
        try
        {
            return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly ReadDate(JsonElement element, string name) =>
        DateOnly.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.MinValue;

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }
}
