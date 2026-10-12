using System.Net;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed class TikTokAccountAnalyticsClient
{
    private const string Origin = "https://www.tiktokdramacenter.com";

    public async Task<string> PostDailyStatsAsync(
        string cookieHeader,
        TikTokProxyHelper.ProxyConfig? proxy,
        DateOnly start,
        DateOnly end,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            dateRange = new
            {
                startDate = start.ToString("yyyy-MM-dd"),
                endDate = end.ToString("yyyy-MM-dd"),
            },
        });
        return await SendAsync(
            HttpMethod.Post,
            Origin + "/api/content-partner/analytics/institution/daily-stats",
            Origin + "/analytics/content-performance",
            cookieHeader,
            proxy,
            body,
            ct).ConfigureAwait(false);
    }

    public async Task<string> PostSeriesPageAsync(
        string cookieHeader,
        TikTokProxyHelper.ProxyConfig? proxy,
        int pageNum,
        int pageSize,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            pageNum,
            pageSize,
            sortField = "innerfeedVv",
            sortOrder = "desc",
        });
        return await SendAsync(
            HttpMethod.Post,
            Origin + "/api/content-partner/analytics/institution/contract-stats-list",
            Origin + "/analytics/content-performance",
            cookieHeader,
            proxy,
            body,
            ct).ConfigureAwait(false);
    }

    public async Task<string> GetRevenueAsync(
        string cookieHeader,
        TikTokProxyHelper.ProxyConfig? proxy,
        CancellationToken ct) =>
        await SendAsync(
            HttpMethod.Get,
            Origin + "/api/content-partner/enterprise/batch-get-amount",
            Origin + "/settlement/confirmed",
            cookieHeader,
            proxy,
            null,
            ct).ConfigureAwait(false);

    public static string ReadCookieHeader(string storageStatePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(storageStatePath));
        if (!document.RootElement.TryGetProperty("cookies", out var cookies))
            return "";
        var parts = new List<string>();
        foreach (var cookie in cookies.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var domainNode) ? domainNode.GetString() ?? "" : "";
            if (!domain.Contains("tiktokdramacenter.com", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = cookie.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : "";
            var value = cookie.TryGetProperty("value", out var valueNode) ? valueNode.GetString() : "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;
            parts.Add(name + "=" + value);
        }

        return string.Join("; ", parts);
    }

    private static async Task<string> SendAsync(
        HttpMethod method,
        string url,
        string referer,
        string cookieHeader,
        TikTokProxyHelper.ProxyConfig? proxy,
        string? jsonBody,
        CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy.Server)
            {
                Credentials = string.IsNullOrEmpty(proxy.Username)
                    ? null
                    : new NetworkCredential(proxy.Username, proxy.Password),
            };
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TikTok 接口失败（{(int)response.StatusCode}）。请确认该账号已在浏览器页登录。");
        if (!text.TrimStart().StartsWith('{'))
            throw new InvalidOperationException("该账号登录态已失效，请先在浏览器页登录后再更新。");
        return text;
    }
}
