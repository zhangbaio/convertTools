using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public static partial class ForeignPlayletClient
{
    private const string ReelShortV2Origin = "https://reelshort.goodbos.online";
    private const string DramaBoxOrigin = "https://dramabox.goodbos.online";
    private const string DramaWaveOrigin = "https://dramawave.goodbos.online";
    private const string NetShortOrigin = "https://netshort.goodbos.online";
    private const string ShortMaxOrigin = "https://shortmax.goodbos.online";
    private const string MemberPrivateKey = """
        -----BEGIN PRIVATE KEY-----
        MIIChwIBADANBgkqhkiG9w0BAQEFAASCAmIwggJeAgEAAoGBAOQVmDXMZXrr0chG
        ML0R9qeo7pfThPNP24bfQcK6PE7tmgo72XJvJ/+/+EMVnIFQVzDEUTUlT0glyMRF
        OWASEfbZjJnO2x6t6A27vqVAJlIWCfC+Mp677E2+/LI4nHW3c8MRpbmMqFaVq+hj
        N0vLhz9MJwPeiugpxeFU49FfJcFNAgMBAAECgYEAv1m2lvvh71xEZWctoJGfg01g
        zhNwqMhIfkBzZTDlQ7pmz0sixLSotBGT8IQDByIl5A3vq0yf1QGOC8uftQ+z7PUQ
        V07XegnwmDIYuFdoYGEL7NtUmKt3k4vUQ4yaYd+MNZeGKViOgEPSdMV5pdGXf2ZD
        EIWEJgxPmo0yf1A05qkCQQDn8YhJl4NDiFGF+HxHiFVNwnfZ+j8k8wpC3Gn+0ORY
        36wy/eWEMGm96Z4UKPqv59jP2lCqUFsby0WeTvwaSMpzAkEA+72YmzMoYqka2vxa
        yl6lsqoPb1r8ks9dE8/6JhTq1z6p7aPUTL7qxiV2N0LvBtTzJ+Ro4/R4SVFIGIWF
        hD2VPwJBANGZ22dlnCFzFvjSTLz33t6eVmCNXHKvjx5MwRyc+T23I44RE1AZz/Tn
        IQtm3OQJyExNJYhDhZHgZ9Ff30KBdTECQQCHS3B3CNmNnMDYLIRG+vMmwIsnF8li
        xZuKiF4ylU9Ze04sb74ruPfIol2UeZ6pF03PhMxypvL2xH5w32HQnqBVAkA/ekGZ
        zLC8NDzo8DwNz/Azjcx+3RxKSgmr0u6BiHfpNA92LpsOD4Clmcm4ZTrrVSD8DT/7
        Zzad3ofVD1wXkDLyoA0wCwYDVR0PMQQDAgAQ
        -----END PRIVATE KEY-----
        """;

    private static async Task<IReadOnlyList<DramaSearchItem>> SearchPlatformAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        string source,
        string language,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var streamKey = await GetStreamApiKeyAsync(httpClient, serverUrl, deviceId, cancellationToken);
        var lang = ApiLanguage(language);
        using var document = await GetPlatformJsonAsync(
            httpClient,
            BuildSearchUrl(source, lang, keyword, page, streamKey),
            UsesDramaBoxToken(source) ? streamKey : null,
            cancellationToken);
        return source switch
        {
            "reelshortv2" => ReadReelShortV2Search(document.RootElement, source, language),
            "dramabox" or "dramaboxv2" => ReadDramaBoxSearch(document.RootElement, source, language),
            "dramawave" => ReadDramaWaveSearch(document.RootElement, source, language),
            "netshort" or "netshortv1" => ReadNetShortSearch(document.RootElement, source, language),
            "shortmax" => ReadShortMaxSearch(document.RootElement, source, language),
            _ => throw new InvalidOperationException($"{SourceLabel(source)} 的搜索还没有接入。")
        };
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<EpisodeItem> Items)> EpisodeCache = new(StringComparer.Ordinal);

    private static async Task<IReadOnlyList<EpisodeItem>> GetPlatformEpisodesAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        BookReference book,
        string? quality,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{book.Source}|{book.Language}|{book.BookId}|{quality}";
        if (EpisodeCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Items;
        var streamKey = await GetStreamApiKeyAsync(httpClient, serverUrl, deviceId, cancellationToken);
        var lang = ApiLanguage(book.Language);
        using var document = await GetPlatformJsonAsync(
            httpClient,
            BuildEpisodeUrl(book.Source, lang, book.BookId, streamKey),
            UsesDramaBoxToken(book.Source) ? streamKey : null,
            cancellationToken);
        var episodes = book.Source switch
        {
            "reelshortv2" => ReadReelShortV2Episodes(document.RootElement, book, quality),
            "dramabox" or "dramaboxv2" => ReadDramaBoxEpisodes(document.RootElement, book, quality),
            "dramawave" => ReadDramaWaveEpisodes(document.RootElement, book, quality),
            "netshort" or "netshortv1" => ReadNetShortEpisodes(document.RootElement, book),
            "shortmax" => ReadShortMaxEpisodes(document.RootElement, book, quality),
            _ => throw new InvalidOperationException($"{SourceLabel(book.Source)} 的下载还没有接入。")
        };
        if (episodes.Count == 0)
            throw new InvalidOperationException($"{SourceLabel(book.Source)} 没有返回分集。");
        EpisodeCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(2), episodes);
        return episodes;
    }

    private static bool UsesDramaBoxToken(string source) =>
        source is "dramabox" or "dramaboxv2";

    private static string ApiLanguage(string language) =>
        string.Equals(language, "zh", StringComparison.Ordinal) ? "zhHans" : language;

    private static string BuildSearchUrl(string source, string language, string keyword, int page, string streamKey)
    {
        var query = Uri.EscapeDataString(keyword.Trim());
        var pageText = Math.Max(1, page).ToString(CultureInfo.InvariantCulture);
        var code = Uri.EscapeDataString(streamKey);
        return source switch
        {
            "reelshortv2" => $"{ReelShortV2Origin}/search?q={query}&lang={language}&page={pageText}&code={code}",
            "dramabox" or "dramaboxv2" => $"{DramaBoxOrigin}/api/dramabox/search?query={query}&lang={language}",
            "dramawave" => $"{DramaWaveOrigin}/api/search?q={query}&lang={language}&next={pageText}&code={code}",
            "netshort" or "netshortv1" => $"{NetShortOrigin}/api/search?q={query}&lang={language}&page={pageText}&code={code}",
            "shortmax" => $"{ShortMaxOrigin}/api/v1/search?q={query}&lang={language}&page={pageText}&code={code}",
            _ => throw new InvalidOperationException($"{SourceLabel(source)} 的搜索还没有接入。")
        };
    }

    private static string BuildEpisodeUrl(string source, string language, string bookId, string streamKey)
    {
        var id = Uri.EscapeDataString(bookId);
        var code = Uri.EscapeDataString(streamKey);
        return source switch
        {
            "reelshortv2" => $"{ReelShortV2Origin}/allepisodes/{id}?code={code}",
            "dramabox" or "dramaboxv2" => $"{DramaBoxOrigin}/api/dramabox/allepisode?bookId={id}&lang={language}",
            "dramawave" => $"{DramaWaveOrigin}/api/drama/{id}?lang={language}&code={code}",
            "netshort" or "netshortv1" => $"{NetShortOrigin}/api/drama/{id}?lang={language}&code={code}",
            "shortmax" => $"{ShortMaxOrigin}/api/v1/alleps/{id}?lang={language}&code={code}",
            _ => throw new InvalidOperationException($"{SourceLabel(source)} 的下载还没有接入。")
        };
    }

    internal static IReadOnlyList<DramaSearchItem> ReadReelShortV2Search(JsonElement root, string source, string language)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        return ReadObjects(results, item =>
        {
            var id = ReadString(item, "id");
            var title = ReadString(item, "title");
            if (id.Length == 0 || title.Length == 0)
                return null;
            return new DramaSearchItem(
                FormatBookId(source, language, id),
                title,
                "",
                ReadInt(item, "chapters"),
                "",
                ReadString(item, "pic"));
        });
    }

    internal static IReadOnlyList<DramaSearchItem> ReadDramaBoxSearch(JsonElement root, string source, string language)
    {
        var list = root.ValueKind == JsonValueKind.Array ? root : default;
        if (list.ValueKind != JsonValueKind.Array)
            return [];
        return ReadObjects(list, item =>
        {
            var id = ReadString(item, "bookId");
            var title = StripMarkup(ReadString(item, "bookName"));
            if (id.Length == 0 || title.Length == 0)
                return null;
            return new DramaSearchItem(
                FormatBookId(source, language, id),
                title,
                ReadString(item, "tagNames"),
                0,
                ReadString(item, "introduction"),
                ReadString(item, "cover"),
                ReadString(item, "author"));
        });
    }

    internal static IReadOnlyList<DramaSearchItem> ReadDramaWaveSearch(JsonElement root, string source, string language)
    {
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return ReadObjects(list, item =>
        {
            var id = ReadString(item, "id");
            var title = StripMarkup(ReadString(item, "name"));
            if (id.Length == 0 || title.Length == 0)
                return null;
            return new DramaSearchItem(
                FormatBookId(source, language, id),
                title,
                ReadString(item, "series_tag"),
                ReadInt(item, "episode_count"),
                ReadString(item, "desc"),
                ReadString(item, "cover"));
        });
    }

    internal static IReadOnlyList<DramaSearchItem> ReadNetShortSearch(JsonElement root, string source, string language)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("searchCodeSearchResult", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return ReadObjects(list, item =>
        {
            var id = ReadString(item, "shortPlayId");
            var title = StripMarkup(ReadString(item, "shortPlayName"));
            if (id.Length == 0 || title.Length == 0)
                return null;
            var labels = item.TryGetProperty("labelNameList", out var labelList) && labelList.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", labelList.EnumerateArray().Select(label => StripMarkup(ReadString(label, "labelName"))).Where(text => text.Length > 0))
                : "";
            return new DramaSearchItem(
                FormatBookId(source, language, id),
                title,
                labels,
                0,
                "",
                ReadString(item, "shortPlayCover"));
        });
    }

    internal static IReadOnlyList<DramaSearchItem> ReadShortMaxSearch(JsonElement root, string source, string language)
    {
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data : default;
        if (list.ValueKind != JsonValueKind.Array)
            return [];
        return ReadObjects(list, item =>
        {
            var id = ReadString(item, "id");
            var title = StripMarkup(ReadString(item, "name"));
            if (id.Length == 0 || title.Length == 0)
                return null;
            return new DramaSearchItem(
                FormatBookId(source, language, id),
                title,
                "",
                ReadInt(item, "episodes"),
                ReadString(item, "summary"),
                ReadString(item, "cover"),
                PlayCount: ReadInt(item, "views"));
        });
    }

    private static IReadOnlyList<EpisodeItem> ReadReelShortV2Episodes(JsonElement root, BookReference book, string? quality)
    {
        if (!root.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<EpisodeItem>();
        foreach (var episode in episodes.EnumerateArray())
        {
            var number = ReadInt(episode, "episode");
            if (number <= 0 || !episode.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                continue;
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var stream in streams.EnumerateArray())
            {
                var qualityKey = Digits(ReadString(stream, "quality"));
                var url = ReadString(stream, "url");
                if (qualityKey.Length > 0 && url.Length > 0)
                    urls[qualityKey] = url;
            }

            var selected = PickByQuality(urls, quality);
            if (selected.Length == 0)
                continue;
            items.Add(new EpisodeItem(number, ReadString(episode, "name") is { Length: > 0 } name ? name : $"第{number}集", FormatApiEpisodeId(book, number), selected));
        }

        return items.OrderBy(item => item.Number).ToArray();
    }

    private static IReadOnlyList<EpisodeItem> ReadDramaBoxEpisodes(JsonElement root, BookReference book, string? quality)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<EpisodeItem>();
        foreach (var chapter in root.EnumerateArray())
        {
            var index = ReadInt(chapter, "chapterIndex");
            var number = index >= 0 ? index + 1 : items.Count + 1;
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            AddUrl(urls, "1080", ReadString(chapter, "videoUrl1080"));
            AddUrl(urls, "720", ReadString(chapter, "videoUrl720"));
            AddUrl(urls, "540", ReadString(chapter, "videoUrl540"));
            AddUrl(urls, "0", ReadString(chapter, "videoUrl"));
            var selected = PickByQuality(urls, quality);
            if (selected.Length == 0)
                continue;
            var title = ReadString(chapter, "chapterName");
            items.Add(new EpisodeItem(number, title.Length > 0 ? title : $"第{number}集", FormatApiEpisodeId(book, number), selected));
        }

        return items.OrderBy(item => item.Number).ToArray();
    }

    private static IReadOnlyList<EpisodeItem> ReadDramaWaveEpisodes(JsonElement root, BookReference book, string? quality)
    {
        var data = root.TryGetProperty("data", out var payload) ? payload : root;
        if (!data.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<EpisodeItem>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            var number = ReadInt(item, "serial_number");
            if (number <= 0)
                continue;
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            AddUrl(urls, "1080", ReadString(item, "1080p_mp4"));
            AddUrl(urls, "720", ReadString(item, "720p_mp4"));
            AddUrl(urls, "540", ReadString(item, "540p_mp4"));
            AddUrl(urls, "0", ReadString(item, "m3u8_path"));
            var selected = PickByQuality(urls, quality);
            if (selected.Length == 0)
                continue;
            items.Add(new EpisodeItem(number, $"第{number}集", FormatApiEpisodeId(book, number), selected));
        }

        return items.OrderBy(item => item.Number).ToArray();
    }

    private static IReadOnlyList<EpisodeItem> ReadNetShortEpisodes(JsonElement root, BookReference book)
    {
        var data = root.TryGetProperty("data", out var payload) ? payload : root;
        if (!data.TryGetProperty("shortPlayEpisodeList", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<EpisodeItem>();
        foreach (var episode in list.EnumerateArray())
        {
            var number = ReadInt(episode, "episodeNo");
            var url = ReadString(episode, "playVoucher");
            if (number <= 0 || url.Length == 0)
                continue;
            items.Add(new EpisodeItem(number, $"第{number}集", FormatApiEpisodeId(book, number), url));
        }

        return items.OrderBy(item => item.Number).ToArray();
    }

    private static IReadOnlyList<EpisodeItem> ReadShortMaxEpisodes(JsonElement root, BookReference book, string? quality)
    {
        var data = root.TryGetProperty("data", out var payload) ? payload : root;
        if (!data.TryGetProperty("episodes", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<EpisodeItem>();
        foreach (var episode in list.EnumerateArray())
        {
            var number = ReadInt(episode, "episode");
            if (number <= 0 || !episode.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
                continue;
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            AddUrl(urls, "1080", ReadString(video, "video_1080"));
            AddUrl(urls, "720", ReadString(video, "video_720"));
            AddUrl(urls, "480", ReadString(video, "video_480"));
            var selected = PickByQuality(urls, quality);
            if (selected.Length == 0)
                continue;
            items.Add(new EpisodeItem(number, $"第{number}集", FormatApiEpisodeId(book, number), selected));
        }

        return items.OrderBy(item => item.Number).ToArray();
    }

    internal static string PickByQuality(IReadOnlyDictionary<string, string> urls, string? quality)
    {
        foreach (var name in QualityPreference(quality))
        {
            if (urls.TryGetValue(name, out var url) && !string.IsNullOrWhiteSpace(url))
                return url;
        }

        return urls.Values.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? "";
    }

    internal static string StripMarkup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return Regex.Replace(value, "<[^>]+>", "").Replace("&nbsp;", " ").Trim();
    }

    private static IReadOnlyList<string> QualityPreference(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "720p" or "720" or "2" or "高清" or "hd")
            return ["720", "1080", "540", "480", "0"];
        if (normalized is "540p" or "540" or "480p" or "480" or "360p" or "标清" or "sd")
            return ["540", "480", "720", "1080", "0"];
        return ["1080", "720", "540", "480", "0"];
    }

    private static string FormatApiEpisodeId(BookReference book, int number) =>
        $"{EpisodePrefix}{book.Source}|{book.Language}|{book.BookId}|{number.ToString(CultureInfo.InvariantCulture)}";

    private static async Task<JsonDocument> GetPlatformJsonAsync(
        HttpClient httpClient,
        string url,
        string? dramaBoxToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(dramaBoxToken))
            request.Headers.TryAddWithoutValidation("x-token", dramaBoxToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"海外短剧接口 HTTP {(int)response.StatusCode}：{Trim(text)}");
        var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(error.GetString()))
        {
            var message = error.GetString()!;
            document.Dispose();
            throw new InvalidOperationException(message);
        }

        return document;
    }

    private static IReadOnlyList<DramaSearchItem> ReadObjects(JsonElement list, Func<JsonElement, DramaSearchItem?> map)
    {
        var items = new List<DramaSearchItem>();
        foreach (var item in list.EnumerateArray())
        {
            var mapped = map(item);
            if (mapped is not null)
                items.Add(mapped);
        }

        return items;
    }

    private static void AddUrl(IDictionary<string, string> urls, string quality, string url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            urls[quality] = url;
    }

    private static string Digits(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : digits;
    }

    private static string Trim(string text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= 180 ? value : value[..180];
    }
}
