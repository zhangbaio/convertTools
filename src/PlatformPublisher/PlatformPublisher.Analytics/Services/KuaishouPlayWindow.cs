using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Analytics.Services;

public static class KuaishouPlayWindow
{
    public const int Days = 30;

    public static DateOnly End(DateOnly today) => today.AddDays(-1);

    public static IReadOnlyList<DateOnly> Dates(DateOnly today)
    {
        var end = End(today);
        var start = end.AddDays(1 - Days);
        return Enumerable.Range(0, Days).Select(start.AddDays).ToArray();
    }

    public static IReadOnlyList<DateOnly> Visible(DateOnly today, int rangeDays)
    {
        var dates = Dates(today);
        var count = rangeDays >= Days ? Days : Math.Clamp(rangeDays, 1, Days);
        return dates.Skip(Days - count).ToArray();
    }

    public static IReadOnlyList<DateOnly> DatesToRefresh(IReadOnlyList<DateOnly> window, IEnumerable<DateOnly> successfulDates)
    {
        if (window.Count == 0) return [];
        var saved = successfulDates.ToHashSet();
        var end = window[^1];
        return window.Where(date => date == end || !saved.Contains(date)).ToArray();
    }
}

public static class KuaishouPlayFormat
{
    public static string Count(long? value) => value is null ? "—" : Count(value.Value);

    public static string Count(long value)
    {
        var abs = Math.Abs(value);
        if (abs >= 100_000_000) return Trim(value / 100_000_000d) + "亿";
        if (abs >= 10_000) return Trim(value / 10_000d) + "万";
        return value.ToString("N0");
    }

    private static string Trim(double value) => value.ToString("0.##");
}

public static class KuaishouPlayBoard
{
    public static string Key(AnalyticsAccount account) => ((int)account.Platform) + ":" + account.Id;

    public static string Edition(PublishPlatform platform) =>
        platform == PublishPlatform.KuaishouEnterpriseRevenue ? "企业" : "个人";

    public static string Label(AnalyticsAccount account) => account.Name + " · " + Edition(account.Platform);

    public static KuaishouPlayBoardResult Build(
        IReadOnlyList<AnalyticsAccount> accounts,
        IReadOnlyList<DailyAnalyticsRecord> records,
        DateOnly today,
        int rangeDays,
        string? selectedKey)
    {
        var window = KuaishouPlayWindow.Dates(today);
        var visible = KuaishouPlayWindow.Visible(today, rangeDays);
        var ordered = accounts
            .Where(account => account.Platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
            .OrderBy(account => account.Platform)
            .ThenBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var cards = new List<KuaishouPlayCard>();
        var series = new List<KuaishouPlaySeries>();
        foreach (var account in ordered)
        {
            var mine = records.Where(record => SameAccount(record, account) && record.Status == AnalyticsRecordStatus.Success && record.SeriesViewsTotal.HasValue)
                .GroupBy(record => record.MetricDate)
                .ToDictionary(group => group.Key, group => group.Last().SeriesViewsTotal!.Value);
            var values = mine.Where(pair => pair.Key >= window[0] && pair.Key <= window[^1]).Select(pair => pair.Value).ToArray();
            var hasData = values.Length > 0;
            var latest = mine.TryGetValue(window[^1], out var latestValue) ? KuaishouPlayFormat.Count(latestValue) : "未出数";
            cards.Add(new KuaishouPlayCard(
                Key(account),
                account.Name,
                Edition(account.Platform),
                hasData ? KuaishouPlayFormat.Count(values.Sum()) : "暂无数据",
                latest,
                hasData));
            if (!string.IsNullOrEmpty(selectedKey) && !string.Equals(selectedKey, Key(account), StringComparison.Ordinal))
                continue;
            series.Add(new KuaishouPlaySeries(Key(account), Label(account), visible.Select(date =>
                new KuaishouPlayPoint(date, mine.TryGetValue(date, out var views) ? views : null)).ToArray()));
        }
        return new KuaishouPlayBoardResult(cards, series);
    }

    private static bool SameAccount(DailyAnalyticsRecord record, AnalyticsAccount account) =>
        record.Platform == account.Platform &&
        string.Equals(record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase);
}

public static class KuaishouPlayStore
{
    public static int Save(AnalyticsRepository repository, AnalyticsAccount account, IReadOnlyDictionary<DateOnly, long> plays, DateOnly today)
    {
        var window = KuaishouPlayWindow.Dates(today);
        var saved = repository.ListDaily(window[0], window[^1])
            .Where(record => record.Platform == account.Platform &&
                             string.Equals(record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase) &&
                             record.Status == AnalyticsRecordStatus.Success &&
                             record.SeriesViewsTotal.HasValue)
            .Select(record => record.MetricDate)
            .ToHashSet();
        var refresh = KuaishouPlayWindow.DatesToRefresh(window, saved).ToHashSet();
        var written = 0;
        foreach (var pair in plays)
        {
            if (!refresh.Contains(pair.Key)) continue;
            repository.UpsertDaily(new DailyAnalyticsRecord
            {
                Platform = account.Platform,
                AccountId = account.Id,
                MetricDate = pair.Key,
                CollectedAt = DateTimeOffset.Now,
                Status = AnalyticsRecordStatus.Success,
                SeriesViewsTotal = pair.Value,
            });
            written++;
        }
        repository.DeleteDailyBefore(account.Platform, account.Id, window[0]);
        return written;
    }
}

public sealed record KuaishouPlayCard(string Key, string Name, string Edition, string TotalText, string LatestText, bool HasData);

public sealed record KuaishouPlayPoint(DateOnly Date, long? Views);

public sealed record KuaishouPlaySeries(string Key, string Name, IReadOnlyList<KuaishouPlayPoint> Points);

public sealed record KuaishouPlayBoardResult(IReadOnlyList<KuaishouPlayCard> Cards, IReadOnlyList<KuaishouPlaySeries> Series);
