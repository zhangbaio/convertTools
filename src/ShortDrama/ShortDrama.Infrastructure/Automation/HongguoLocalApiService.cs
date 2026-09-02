using ShortDrama.Core.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoLocalApiService
{
    private const string HongguoLocalBookPrefix = "hglocal:";
    private const string HongguoLocalEpisodePrefix = "hglocal_ep:";

    private readonly HttpClient _httpClient;

    public HongguoLocalApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var trimmedKeyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedKeyword))
        {
            return [];
        }

        try
        {
            var directResults = await SearchDirectAsync(
                settings,
                trimmedKeyword,
                page,
                refresh: false,
                cancellationToken: cancellationToken);
            if (directResults.Count > 0)
            {
                return directResults;
            }

            directResults = await SearchDirectAsync(
                settings,
                trimmedKeyword,
                page,
                refresh: true,
                cancellationToken: cancellationToken);
            if (directResults.Count > 0)
            {
                return directResults;
            }
        }
        catch
        {
            // Fall back to recent snapshots when the direct index endpoint is unavailable.
        }

        return await SearchRecentSnapshotsAsync(settings, trimmedKeyword, page, cancellationToken);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchDirectAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置本地直连服务地址。");
        }

        var refreshQuery = refresh ? "&refresh=1" : string.Empty;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/search?q={Uri.EscapeDataString(keyword)}&limit=40&page={Math.Max(1, page)}&source=hglocal&live=true{refreshQuery}");
        ApplyHeaders(request, settings);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(MapSearchItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchRecentSnapshotsAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var genres = new[] { "short_play", "comic_series", "ai_series" };
        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var genre in genres)
        {
            var items = await FetchLatestItemsAsync(settings, genre, onlyToday: false, cancellationToken);
            foreach (var item in MapItems(items))
            {
                if (!MatchesKeyword(item, keyword) || !seen.Add(item.BookId))
                {
                    continue;
                }

                results.Add(item);
            }
        }

        return results
            .OrderByDescending(item => TryParsePublishDate(item.PublishTime, out var publishedAt) ? publishedAt : DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Skip(Math.Max(0, page - 1) * 40)
            .Take(40)
            .ToArray();
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayNewAsync(
        DramaSourceSettings settings,
        string genre,
        CancellationToken cancellationToken)
    {
        var items = await FetchLatestItemsAsync(settings, genre, onlyToday: true, cancellationToken);
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return MapItems(items.Where(item => item.ValueKind == JsonValueKind.Object && IsTodayItem(item, today)));
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetLatestByGenreAsync(
        DramaSourceSettings settings,
        string genre,
        int days,
        CancellationToken cancellationToken)
    {
        var queryDays = Math.Clamp(days, 1, 30);
        var items = await FetchLatestItemsAsync(
            settings,
            genre,
            onlyToday: queryDays <= 1,
            cancellationToken);
        var mapped = MapItems(items.Where(item => item.ValueKind == JsonValueKind.Object));
        return FilterByRecentDays(mapped, queryDays);
    }

    public async Task<IReadOnlyList<LocalEpisodeInfo>> GetEpisodesAsync(
        DramaSourceSettings settings,
        string prefixedOrRawBookId,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置本地直连服务地址。");
        }

        var bookId = StripPrefix(prefixedOrRawBookId, HongguoLocalBookPrefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/episodes?series_id={Uri.EscapeDataString(bookId)}&source=hglocal");
        ApplyHeaders(request, settings);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<LocalEpisodeInfo>();
        var index = 1;
        foreach (var item in episodes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var videoId = GetString(item, "vid") ?? GetString(item, "video_id") ?? GetString(item, "id");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var episodeNumber = GetInt(item, "index") ?? index;
            result.Add(new LocalEpisodeInfo(
                episodeNumber,
                GetString(item, "title") ?? $"第{episodeNumber}集",
                EnsurePrefixed(videoId, HongguoLocalEpisodePrefix),
                GetString(item, "cover") ?? string.Empty));
            index++;
        }

        return result;
    }

    public async Task<LocalVideoPlayback> GetVideoPlaybackAsync(
        DramaSourceSettings settings,
        string prefixedOrRawVideoId,
        string quality,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置本地直连服务地址。");
        }

        var videoId = StripPrefix(prefixedOrRawVideoId, HongguoLocalEpisodePrefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/video_url?vid={Uri.EscapeDataString(videoId)}&source=hglocal");
        ApplyHeaders(request, settings);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var url = GetString(root, "url")
                  ?? GetString(root, "play_url")
                  ?? GetString(root, "playUrl")
                  ?? GetString(root, "video_url")
                  ?? GetString(root, "videoUrl")
                  ?? GetString(root, "main_url")
                  ?? GetString(root, "backup");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("本地直连服务未返回可用播放链接。");
        }

        var encryptedUrls = new[]
            {
                "encrypted_url", "encryptedUrl", "main_url", "mainUrl",
                "cdn_url", "cdnUrl", "backup", "backup_url", "backupUrl"
            }
            .Select(key => GetString(root, key))
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var spadeA = GetString(root, "spade_a")
                     ?? GetString(root, "spadeA")
                     ?? string.Empty;
        var encrypted = GetBool(root, "encrypt")
                        ?? GetBool(root, "encrypted")
                        ?? false;

        return new LocalVideoPlayback(url, encryptedUrls, spadeA, encrypted);
    }

    private async Task<IReadOnlyList<JsonElement>> FetchLatestItemsAsync(
        DramaSourceSettings settings,
        string genre,
        bool onlyToday,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置本地直连服务地址。");
        }

        var onlyTodayQuery = onlyToday ? "&only_today=true" : string.Empty;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/latest?genre={Uri.EscapeDataString(genre)}{onlyTodayQuery}&limit=1000&source=hglocal");
        ApplyHeaders(request, settings);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => item.Clone())
            .ToArray();
    }

    private static IReadOnlyList<DramaSearchItem> MapItems(IEnumerable<JsonElement> items)
    {
        return items
            .Select(MapSearchItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private static bool MatchesKeyword(DramaSearchItem item, string keyword)
    {
        var normalizedKeyword = NormalizeSearchText(keyword);
        if (normalizedKeyword.Length == 0)
        {
            return false;
        }

        return new[]
            {
                item.Title,
                item.Category,
                item.Intro,
                item.Author
            }
            .Select(NormalizeSearchText)
            .Any(text => text.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
                         || normalizedKeyword.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSearchText(string? text)
    {
        return (text ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    private static DramaSearchItem MapSearchItem(JsonElement item)
    {
        return new DramaSearchItem(
            BookId: EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id") ?? GetString(item, "id"), HongguoLocalBookPrefix),
            Title: GetString(item, "title") ?? GetString(item, "name") ?? string.Empty,
            Category: GetString(item, "category") ?? GetString(item, "type") ?? string.Empty,
            EpisodeTotal: GetInt(item, "episode_cnt") ?? GetInt(item, "episode_total") ?? GetInt(item, "total") ?? 0,
            Intro: GetString(item, "intro") ?? GetString(item, "description") ?? GetString(item, "desc") ?? string.Empty,
            PosterUrl: GetString(item, "cover") ?? GetString(item, "poster") ?? GetString(item, "poster_url") ?? string.Empty,
            Author: GetString(item, "author") ?? GetString(item, "producer") ?? GetString(item, "copyright") ?? string.Empty,
            PublishTime: GetString(item, "publish_time") ?? GetString(item, "first_seen") ?? GetString(item, "created_at") ?? string.Empty,
            FavoriteCount: GetInt(item, "favorite_count") ?? GetInt(item, "collect_count") ?? 0);
    }

    private static IReadOnlyList<DramaSearchItem> FilterByRecentDays(
        IReadOnlyList<DramaSearchItem> items,
        int days)
    {
        if (days <= 1 || items.Count == 0)
        {
            return items;
        }

        var threshold = DateTime.Today.AddDays(-(days - 1));
        var filtered = items
            .Where(item => !TryParsePublishDate(item.PublishTime, out var publishedAt) || publishedAt.Date >= threshold)
            .ToArray();
        return filtered.Length > 0 ? filtered : items;
    }

    private static bool TryParsePublishDate(string? value, out DateTime date)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static bool IsTodayItem(JsonElement item, string today)
    {
        if (item.TryGetProperty("today", out var todayProperty))
        {
            if (todayProperty.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (todayProperty.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        foreach (var propertyName in new[] { "publish_time", "first_seen", "last_seen", "created_at", "updated_at" })
        {
            var value = GetString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith(today, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyHeaders(HttpRequestMessage request, DramaSourceSettings settings)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "ShortDramaDesktop/1.0");
        if (!string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.HongguoLocalApiKey);
        }
    }

    private static string NormalizeLocalBaseUrl(string value)
    {
        var baseUrl = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        return baseUrl.EndsWith("/api/hongguo", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/api/hongguo";
    }

    private static string EnsurePrefixed(string? value, string prefix)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text : prefix + text;
    }

    private static string StripPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value)
            ? value
            : null;
    }

    public sealed record LocalEpisodeInfo(int EpisodeNumber, string Title, string VideoId, string PosterUrl);

    public sealed record LocalVideoPlayback(
        string Url,
        IReadOnlyList<string> EncryptedUrls,
        string SpadeA,
        bool Encrypted)
    {
        public string EncryptedUrl => EncryptedUrls.FirstOrDefault() ?? Url;
    }
}
