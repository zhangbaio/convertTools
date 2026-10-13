using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouOnlineQueueProcessor
{
    private readonly HttpClient _httpClient;
    private readonly KuaishouOnlineQueueStore _queueStore;
    private readonly KuaishouDistributionService _distributionService;

    public KuaishouOnlineQueueProcessor(
        HttpClient httpClient,
        KuaishouOnlineQueueStore queueStore,
        KuaishouDistributionService distributionService)
    {
        _httpClient = httpClient;
        _queueStore = queueStore;
        _distributionService = distributionService;
    }

    public async Task<int> ProcessDueAsync(
        string accountId,
        PublishPlatform platform,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(new PublishJob { AccountId = accountId, Platform = platform });
        if (!config.AutoOnlineEnabled && !config.StepOnlineSeries) return 0;
        EnsureApiConfiguration(config);

        var queue = _queueStore.Load(accountId, platform).ToList();
        var due = queue
            .Where(item => item.Status is "pending_audit" or "audit_passed" or "retry")
            .Where(item => item.NextCheckAt <= DateTimeOffset.UtcNow)
            .OrderBy(item => item.NextCheckAt)
            .Take(Math.Clamp(config.AutoOnlineMaxItemsPerRound, 1, 200))
            .ToArray();
        var onlineCount = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await QueryStatusAsync(item, config, cancellationToken);
                item.CheckedCount++;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                if (status.AuditStatus == 4)
                {
                    item.Status = "rejected";
                    item.LastError = "平台审核未通过";
                }
                else if (status.AuditStatus != 3)
                {
                    item.Status = "pending_audit";
                    item.LastError = string.Empty;
                    item.NextCheckAt = NextCheck(config);
                }
                else if (status.SellingStatus == 9)
                {
                    item.Status = "risk_offline";
                    item.LastError = "平台状态为风控下架";
                }
                else if (status.SellingStatus == 1)
                {
                    item.Status = "online";
                    item.LastError = string.Empty;
                }
                else
                {
                    await SetOnlineAsync(item, config, cancellationToken);
                    item.Status = "online";
                    item.LastError = string.Empty;
                    onlineCount++;
                    progress?.Report($"{platform.DisplayName()}：短剧《{item.Title}》已自动上架。 ");
                }

                if (item.Status == "online" && config.OnlineAutoDistributionEnabled)
                    await _distributionService.ApplyAsync(item.MiniSeriesId, config, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Status = "retry";
                item.LastError = ex.Message.Length <= 300 ? ex.Message : ex.Message[..300];
                item.NextCheckAt = NextCheck(config);
                item.UpdatedAt = DateTimeOffset.UtcNow;
                progress?.Report($"{platform.DisplayName()}：自动上架检查失败，将稍后重试：{item.LastError}");
            }
            finally
            {
                _queueStore.Save(accountId, platform, queue);
            }
        }
        return onlineCount;
    }

    private async Task<(int AuditStatus, int SellingStatus)> QueryStatusAsync(
        KuaishouOnlineQueueItem item,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(config.SeriesBaseInfoPath)
            ? "/rest/openapi/gw/dsp/series/material/seriesBaseInfo"
            : config.SeriesBaseInfoPath;
        var json = await PostAsync(path, item, config, new
        {
            advertiser_id = NumericOrText(item.AdvertiserId),
            series_id = NumericOrText(item.MiniSeriesId),
        }, cancellationToken);
        return (FindInt(json, "audit_status", "auditStatus"), FindInt(json, "selling_status", "sellingStatus"));
    }

    private async Task SetOnlineAsync(
        KuaishouOnlineQueueItem item,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(config.OnlineOfflinePath)
            ? "/rest/openapi/gw/dsp/series/material/onlineOfflineManage"
            : config.OnlineOfflinePath;
        await PostAsync(path, item, config, new
        {
            advertiser_id = NumericOrText(item.AdvertiserId),
            series_id = NumericOrText(item.MiniSeriesId),
            act = 1,
        }, cancellationToken);
    }

    private async Task<JsonNode> PostAsync(
        string path,
        KuaishouOnlineQueueItem item,
        KuaishouPersonalConfig config,
        object payload,
        CancellationToken cancellationToken)
    {
        var url = config.ApiBaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation(
            string.IsNullOrWhiteSpace(config.TokenHeader) ? "Access-Token" : config.TokenHeader.Trim(),
            config.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{Trim(text)}");
        try { return JsonNode.Parse(text) ?? new JsonObject(); }
        catch (JsonException ex) { throw new InvalidOperationException($"平台返回了无效 JSON：{ex.Message}", ex); }
    }

    private static int FindInt(JsonNode? node, params string[] names)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (names.Contains(property.Key, StringComparer.OrdinalIgnoreCase) &&
                    int.TryParse(property.Value?.ToString(), out var value)) return value;
                var nested = FindInt(property.Value, names);
                if (nested != 0) return nested;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
            {
                var nested = FindInt(child, names);
                if (nested != 0) return nested;
            }
        return 0;
    }

    private static object NumericOrText(string value) => long.TryParse(value, out var number) ? number : value;
    private static DateTimeOffset NextCheck(KuaishouPersonalConfig config) =>
        DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(config.AutoOnlineIntervalMinutes, 1, 1440));
    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];

    private static void EnsureApiConfiguration(KuaishouPersonalConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl)) throw new InvalidOperationException("自动上架需要 API Base URL。");
        if (string.IsNullOrWhiteSpace(config.AccessToken)) throw new InvalidOperationException("自动上架需要 Access Token。");
    }
}
