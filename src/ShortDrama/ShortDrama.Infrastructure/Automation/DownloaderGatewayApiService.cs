using System.Net.Http.Headers;
using System.Text.Json;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DownloaderGatewayApiService(HttpClient httpClient)
{
    public const string BookPrefix = "downloader:";
    public const string EpisodePrefix = "downloader_ep:";

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings, string keyword, int page, CancellationToken cancellationToken)
    {
        var root = await GetAsync(settings,
            $"/api/v1/catalog/search?q={Uri.EscapeDataString(keyword.Trim())}&page={Math.Max(1, page)}",
            cancellationToken);
        return ReadItems(root, "results");
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetLatestAsync(
        DramaSourceSettings settings, string kind, int days, CancellationToken cancellationToken)
    {
        var root = await GetAsync(settings,
            $"/api/v1/catalog/latest?kind={Uri.EscapeDataString(kind)}&days={Math.Clamp(days, 1, 30)}",
            cancellationToken);
        return ReadItems(root, "items");
    }

    public async Task<IReadOnlyList<GatewayEpisode>> GetEpisodesAsync(
        DramaSourceSettings settings, string bookReference, CancellationToken cancellationToken)
    {
        var reference = StripPrefix(bookReference, BookPrefix);
        var root = await GetAsync(settings,
            $"/api/v1/catalog/episodes?series_id={Uri.EscapeDataString(reference)}",
            cancellationToken);
        if (!root.TryGetProperty("episodes", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            .Select((item, index) => new GatewayEpisode(
                GetInt(item, "index") ?? index + 1,
                GetString(item, "title") ?? $"第{index + 1}集",
                EnsurePrefixed(GetString(item, "vid") ?? GetString(item, "video_id"), EpisodePrefix)))
            .Where(item => item.VideoId.Length > EpisodePrefix.Length)
            .ToArray();
    }

    public async Task<GatewayPlayback> GetPlaybackAsync(
        DramaSourceSettings settings, string episodeReference, string quality,
        CancellationToken cancellationToken)
    {
        var reference = StripPrefix(episodeReference, EpisodePrefix);
        var root = await GetAsync(settings,
            $"/api/v1/catalog/video-url?vid={Uri.EscapeDataString(reference)}&quality={Uri.EscapeDataString(quality)}",
            cancellationToken);
        var url = GetString(root, "url") ?? GetString(root, "main_url")
                  ?? throw new InvalidOperationException("统一下载器未返回播放地址。");
        return new GatewayPlayback(
            url,
            GetString(root, "spade_a") ?? string.Empty,
            GetBool(root, "encrypt") ?? false);
    }

    public async Task<GatewayHealth> GetHealthAsync(
        DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var root = await GetAsync(settings, "/api/v1/health", cancellationToken, authenticate: false);
        _ = await GetAsync(settings, "/api/v1/capabilities", cancellationToken);
        return new GatewayHealth(
            GetBool(root, "ok") ?? false,
            GetString(root, "activeSource") ?? string.Empty,
            GetString(root, "highEdition") ?? string.Empty);
    }

    private async Task<JsonElement> GetAsync(
        DramaSourceSettings settings, string path, CancellationToken cancellationToken,
        bool authenticate = true)
    {
        var baseUrl = NormalizeBaseUrl(settings.DownloaderApiBaseUrl);
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("未配置统一下载器地址。");
        var apiKey = string.Empty;
        if (authenticate)
        {
            apiKey = string.IsNullOrWhiteSpace(settings.DownloaderApiKey)
                ? DownloaderGatewayDiscovery.TryReadLocalApiKey()
                : settings.DownloaderApiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("未找到统一下载器 API Key，请先启动一次下载器。");
        }
        HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", "TikTokPublisher/1.0");
            if (authenticate) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            return request;
        }

        HttpResponseMessage response;
        using (var request = CreateRequest())
        {
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException) when (DownloaderGatewayDiscovery.TryStartInstalledDownloader())
            {
                response = await RetryAfterStartAsync(CreateRequest, cancellationToken);
            }
        }
        using (response)
        {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(text);
        if (!response.IsSuccessStatusCode)
        {
            var message = document.RootElement.TryGetProperty("error", out var error)
                ? GetString(error, "message") : null;
            throw new InvalidOperationException(message ?? $"统一下载器 HTTP {(int)response.StatusCode}");
        }
        return document.RootElement.Clone();
        }
    }

    private async Task<HttpResponseMessage> RetryAfterStartAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        HttpRequestException? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(200, cancellationToken);
            using var request = requestFactory();
            try
            {
                return await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("统一下载器启动后仍无法连接。");
    }

    private static IReadOnlyList<DramaSearchItem> ReadItems(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new DramaSearchItem(
                EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id"), BookPrefix),
                GetString(item, "title") ?? string.Empty,
                GetString(item, "category") ?? GetString(item, "source") ?? string.Empty,
                GetInt(item, "episode_cnt") ?? 0,
                GetString(item, "intro") ?? string.Empty,
                GetString(item, "cover") ?? string.Empty,
                GetString(item, "author") ?? string.Empty,
                GetString(item, "publish_time") ?? string.Empty,
                GetInt(item, "favorite_count") ?? 0))
            .Where(item => item.BookId.Length > BookPrefix.Length)
            .ToArray();
    }

    private static string NormalizeBaseUrl(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('/');

    private static string EnsurePrefixed(string? value, string prefix)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text : prefix + text;
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    public sealed record GatewayEpisode(int EpisodeNumber, string Title, string VideoId);
    public sealed record GatewayPlayback(string Url, string SpadeA, bool Encrypted);
    public sealed record GatewayHealth(bool Ok, string ActiveSource, string HighEdition);
}
