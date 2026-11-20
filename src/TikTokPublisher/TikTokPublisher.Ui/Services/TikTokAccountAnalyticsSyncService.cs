using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
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
        var (cookie, proxy) = ReadConnection(account, name);
        var mode = TikTokUploadChannels.IsApi(account.TiktokUploadStrategy) ? " 接口统计 ·" : "";
        var prefix = $"[{name}]{mode}";
        var client = new TikTokAccountAnalyticsClient();
        var failures = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = TikTokAccountAnalyticsLogic.NextDailyStart(store.LatestDailyDate(account.Id), today);
        foreach (var (windowStart, windowEnd) in TikTokAccountAnalyticsLogic.Windows(start, today))
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"{prefix} 正在拉取 {windowStart:yyyy-MM-dd} 至 {windowEnd:yyyy-MM-dd} 的每日播放");
            try
            {
                var json = await client.PostDailyStatsAsync(cookie, proxy, windowStart, windowEnd, ct).ConfigureAwait(false);
                store.UpsertDaily(account.Id, TikTokAccountAnalyticsLogic.ParseDailyStats(json, windowStart, windowEnd));
            }
            catch (Exception ex) when (IsRequestFailure(ex, ct))
            {
                failures.Add($"{windowStart:yyyy-MM-dd} 至 {windowEnd:yyyy-MM-dd} 的每日播放：{DescribeFailure(ex)}");
                break;
            }
        }

        log?.Invoke($"{prefix} 正在拉取剧集明细");
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
                if (page > 1 && parsed.Total != total)
                    throw new InvalidOperationException("分页期间接口返回的剧集总数发生变化。");
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
            if (series.Count != total)
                throw new InvalidOperationException($"已获取 {series.Count} 条，接口报告共 {total} 条。");

            ct.ThrowIfCancellationRequested();
            if (series.Count > 0)
                store.UpsertSeries(account.Id, series);
            log?.Invoke($"{prefix} 已拉取 {series.Count} 条剧集明细");
        }
        catch (Exception ex) when (IsRequestFailure(ex, ct))
        {
            failures.Add($"剧集明细更新不完整：{DescribeFailure(ex)}");
        }

        log?.Invoke($"{prefix} 正在拉取账户收益");
        try
        {
            var revenueJson = await client.GetRevenueAsync(cookie, proxy, ct).ConfigureAwait(false);
            store.UpsertRevenue(account.Id, TikTokAccountAnalyticsLogic.ParseRevenue(revenueJson));
        }
        catch (Exception ex) when (IsRequestFailure(ex, ct))
        {
            failures.Add($"账户收益：{DescribeFailure(ex)}");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException($"[{name}] " + string.Join("；", failures));
    }

    internal static (string Cookie, TikTokProxyHelper.ProxyConfig? Proxy) ReadConnection(
        TikTokAccountProfile account, string name)
    {
        var authPath = Environment.ExpandEnvironmentVariables((account.TiktokStorageStatePath ?? "").Trim());
        if (string.IsNullOrWhiteSpace(authPath) || !File.Exists(authPath))
            throw new InvalidOperationException($"[{name}] 还没有登录态，请先在浏览器页登录这个账号。");

        var cookie = TikTokAccountAnalyticsClient.ReadCookieHeader(authPath);
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException($"[{name}] 登录态里没有可用的 Cookie，请重新登录。");

        return (cookie, TikTokProxyHelper.BuildFromAccount(account));
    }

    private static bool IsRequestFailure(Exception exception, CancellationToken ct) =>
        exception is InvalidOperationException or HttpRequestException or JsonException
        || (exception is OperationCanceledException && !ct.IsCancellationRequested);

    private static string DescribeFailure(Exception exception) =>
        exception is OperationCanceledException ? "接口请求超时。" : exception.Message;
}
