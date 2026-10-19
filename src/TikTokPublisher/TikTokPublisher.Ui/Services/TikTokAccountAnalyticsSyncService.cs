using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

public static class TikTokAccountAnalyticsSyncService
{
    public static async Task SyncAsync(
        TikTokAccountProfile account,
        TikTokAccountAnalyticsStore store,
        Action<string>? log,
        CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Id : account.DisplayName;
        var authPath = (account.TiktokStorageStatePath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(authPath) || !File.Exists(authPath))
            throw new InvalidOperationException($"[{name}] 还没有登录态，请先在浏览器页登录这个账号。");

        var cookie = TikTokAccountAnalyticsClient.ReadCookieHeader(authPath);
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException($"[{name}] 登录态里没有可用的 Cookie，请重新登录。");

        var proxy = TikTokProxyHelper.BuildFromAccount(account);
        var client = new TikTokAccountAnalyticsClient();
        var failures = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = TikTokAccountAnalyticsLogic.NextDailyStart(store.LatestDailyDate(account.Id), today);
        foreach (var (windowStart, windowEnd) in TikTokAccountAnalyticsLogic.Windows(start, today))
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"[{name}] 正在拉取 {windowStart:yyyy-MM-dd} 至 {windowEnd:yyyy-MM-dd} 的每日播放");
            try
            {
                var json = await client.PostDailyStatsAsync(cookie, proxy, windowStart, windowEnd, ct).ConfigureAwait(false);
                store.UpsertDaily(account.Id, TikTokAccountAnalyticsLogic.ParseDailyStats(json, windowStart, windowEnd));
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{windowStart:yyyy-MM-dd} 至 {windowEnd:yyyy-MM-dd} 的每日播放：{ex.Message}");
            }
        }

        log?.Invoke($"[{name}] 正在拉取剧集明细");
        var series = new List<TikTokSeriesSnapshot>();
        var seenSeries = new HashSet<string>(StringComparer.Ordinal);
        long total = long.MaxValue;
        const int pageSize = 50;
        try
        {
            for (var page = 1; series.Count < total && page <= 80; page++)
            {
                ct.ThrowIfCancellationRequested();
                var json = await client.PostSeriesPageAsync(cookie, proxy, page, pageSize, ct).ConfigureAwait(false);
                var parsed = TikTokAccountAnalyticsLogic.ParseSeriesPage(json, today);
                total = parsed.Total;
                var added = 0;
                foreach (var item in parsed.Items)
                {
                    if (!seenSeries.Add(item.SeriesId))
                        continue;
                    series.Add(item);
                    added++;
                }

                if (added == 0)
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (series.Count > 0)
            store.UpsertSeries(account.Id, series);

        log?.Invoke($"[{name}] 正在拉取账户收益");
        try
        {
            var revenueJson = await client.GetRevenueAsync(cookie, proxy, ct).ConfigureAwait(false);
            store.UpsertRevenue(account.Id, TikTokAccountAnalyticsLogic.ParseRevenue(revenueJson));
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        log?.Invoke($"[{name}] 已保存 {series.Count} 部剧和收益");
        if (failures.Count > 0)
            throw new InvalidOperationException($"[{name}] " + string.Join("；", failures));
    }
}
