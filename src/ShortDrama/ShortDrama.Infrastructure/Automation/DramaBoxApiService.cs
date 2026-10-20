using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaBoxApiService(HttpClient httpClient)
{
    public const string BookPrefix = "dramabox:";
    public const string EpisodePrefix = "dramabox_ep:";
    public const string DefaultBaseUrl = "http://127.0.0.1:17865";
    public const string UpstreamOrigin = "https://sapi.dramaboxdb.com";

    private static readonly SemaphoreSlim GatewayGate = new(1, 1);
    private static HttpListener? _listener;
    private static CancellationTokenSource? _listenerCts;

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings, string keyword, int page, CancellationToken cancellationToken)
    {
        await EnsureGatewayAsync(settings, cancellationToken);
        var root = await GetAsync(settings,
            $"/api/v1/search?q={Uri.EscapeDataString(keyword.Trim())}&page={Math.Max(1, page)}",
            cancellationToken);
        return ReadItems(root, "results");
    }

    public async Task<IReadOnlyList<GatewayEpisode>> GetEpisodesAsync(
        DramaSourceSettings settings, string bookReference, CancellationToken cancellationToken)
    {
        await EnsureGatewayAsync(settings, cancellationToken);
        var reference = StripPrefix(bookReference, BookPrefix);
        var root = await GetAsync(settings,
            $"/api/v1/episodes?bookId={Uri.EscapeDataString(reference)}",
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

    public async Task<IReadOnlyList<DramaSearchItem>> GetNewChannelAsync(CancellationToken cancellationToken)
    {
        var books = new List<JsonObjectLite>();
        for (var page = 1; page <= 5; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new
            {
                newChannelStyle = 1,
                isNeedRank = 1,
                pageNo = page,
                index = 1,
                channelId = 41,
            });
            using var document = await PostUpstreamAsync("/drama-box/he001/theater", body, cancellationToken);
            var before = books.Count;
            CollectBooks(document.RootElement, books);
            books = books
                .GroupBy(item => item.BookId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (books.Count == before)
                break;
        }

        return books.Select(item => new DramaSearchItem(
            BookPrefix + item.BookId,
            item.Title,
            item.Category,
            item.EpisodeCount,
            item.Intro,
            item.Cover,
            item.Author,
            item.PublishTime)).ToArray();
    }

    public async Task<GatewayPlayback> GetPlaybackAsync(
        DramaSourceSettings settings, string episodeReference, string quality, CancellationToken cancellationToken)
    {
        await EnsureGatewayAsync(settings, cancellationToken);
        var reference = StripPrefix(episodeReference, EpisodePrefix);
        var root = await GetAsync(settings,
            $"/api/v1/video-url?vid={Uri.EscapeDataString(reference)}&quality={Uri.EscapeDataString(quality)}",
            cancellationToken);
        var url = GetString(root, "url") ?? GetString(root, "main_url")
                  ?? throw new InvalidOperationException("DramaBox 未返回可下载的播放地址。");
        return new GatewayPlayback(url, string.Empty, false);
    }

    public sealed record GatewayEpisode(int EpisodeNumber, string Title, string VideoId);
    public sealed record GatewayPlayback(string Url, string SpadeA, bool Encrypted);

    private async Task EnsureGatewayAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(settings.DramaBoxApiBaseUrl);
        if (!baseUrl.StartsWith(DefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
            return;
        if (_listener is { IsListening: true })
            return;
        await GatewayGate.WaitAsync(cancellationToken);
        try
        {
            if (_listener is { IsListening: true })
                return;
            var listener = new HttpListener();
            listener.Prefixes.Add(DefaultBaseUrl + "/");
            listener.Start();
            _listenerCts = new CancellationTokenSource();
            _listener = listener;
            _ = Task.Run(() => ListenAsync(listener, _listenerCts.Token));
        }
        finally
        {
            GatewayGate.Release();
        }
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            var query = context.Request.QueryString;
            JsonElement payload = path switch
            {
                "/api/v1/health" => JsonSerializer.SerializeToElement(new { ok = true, source = "dramabox" }),
                "/api/v1/search" => await UpstreamSearchAsync(query["q"] ?? "", ParsePage(query["page"])),
                "/api/v1/episodes" => await UpstreamEpisodesAsync(query["bookId"] ?? ""),
                "/api/v1/video-url" => await UpstreamVideoAsync(query["vid"] ?? ""),
                _ => throw new InvalidOperationException("未知的 DramaBox 本机接口。"),
            };
            await WriteJsonAsync(context, 200, payload);
        }
        catch (Exception ex)
        {
            var error = JsonSerializer.SerializeToElement(new { error = new { message = ex.Message } });
            try { await WriteJsonAsync(context, 502, error); } catch { /* 客户端已断开。 */ }
        }
    }

    private async Task<JsonElement> UpstreamSearchAsync(string keyword, int page)
    {
        var body = JsonSerializer.Serialize(new
        {
            keyword = keyword.Trim(),
            pageNo = Math.Max(1, page),
            pageSize = 20,
            from = "search_home",
            searchSource = "his",
        });
        using var document = await PostUpstreamAsync("/drama-box/search/search", body, CancellationToken.None);
        var books = new List<JsonObjectLite>();
        CollectBooks(document.RootElement, books);
        var results = books
            .GroupBy(item => item.BookId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(item => new
            {
                series_id = item.BookId,
                title = item.Title,
                category = item.Category,
                episode_cnt = item.EpisodeCount,
                intro = item.Intro,
                cover = item.Cover,
                author = item.Author,
                publish_time = item.PublishTime,
            });
        return JsonSerializer.SerializeToElement(new { results });
    }

    private async Task<JsonElement> UpstreamEpisodesAsync(string bookId)
    {
        var id = bookId.Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("缺少 DramaBox 书号。");
        var body = JsonSerializer.Serialize(new { bookId = id, boundaryIndex = 0, loadDirection = 0, preLoad = false });
        using var document = await PostUpstreamAsync("/drama-box/chapterv2/batch/load", body, CancellationToken.None);
        var episodes = new List<(int Index, string Title, string VideoId)>();
        CollectEpisodes(document.RootElement, episodes);
        if (episodes.Count == 0)
        {
            var detailBody = JsonSerializer.Serialize(new { bookId = id });
            using var detail = await PostUpstreamAsync("/drama-box/chapterv2/detail", detailBody, CancellationToken.None);
            CollectEpisodes(detail.RootElement, episodes);
        }

        return JsonSerializer.SerializeToElement(new
        {
            episodes = episodes
                .GroupBy(item => item.VideoId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Index)
                .Select(item => new { index = item.Index, title = item.Title, vid = item.VideoId }),
        });
    }

    private async Task<JsonElement> UpstreamVideoAsync(string videoId)
    {
        var id = videoId.Trim();
        var split = id.Split('|', 2);
        var bookId = split.Length == 2 ? split[0] : "";
        var chapterId = split.Length == 2 ? split[1] : id;
        if (bookId.Length == 0)
            throw new InvalidOperationException("DramaBox 分集号无效。");
        var body = JsonSerializer.Serialize(new { bookId, chapterId, boundaryIndex = 0, loadDirection = 0 });
        using var document = await PostUpstreamAsync("/drama-box/chapterv2/batch/load", body, CancellationToken.None);
        var url = FindPlayUrl(document.RootElement, chapterId);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("DramaBox 没有返回该集的播放地址。加密成片仍由下载器解密，当前只接入可直接下载的地址。");
        return JsonSerializer.SerializeToElement(new { url });
    }

    private async Task<JsonDocument> PostUpstreamAsync(string path, string json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UpstreamOrigin + path);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("language", "en");
        request.Headers.TryAddWithoutValidation("User-Agent", "DramaBox/1.5.7");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DramaBox 接口 HTTP {(int)response.StatusCode}：{Trim(text)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static void CollectBooks(JsonElement element, List<JsonObjectLite> books)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var bookId = FirstString(element, "bookId", "book_id", "seriesId", "series_id");
                var title = FirstString(element, "bookName", "book_name", "title");
                if (!string.IsNullOrWhiteSpace(bookId) && !string.IsNullOrWhiteSpace(title))
                {
                    books.Add(new JsonObjectLite(
                        bookId,
                        title,
                        FirstString(element, "tagName", "category", "cornerName") ?? "",
                        FirstInt(element, "chapterCount", "episodeCount", "totalChapterNum") ?? 0,
                        FirstString(element, "introduction", "description", "summary") ?? "",
                        FirstString(element, "coverWap", "cover_url", "cover") ?? "",
                        FirstString(element, "author") ?? "",
                        FirstString(element, "bookShelfTime", "publishTime", "onlineTime") ?? ""));
                }
                foreach (var property in element.EnumerateObject())
                    CollectBooks(property.Value, books);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectBooks(item, books);
                break;
        }
    }

    private static void CollectEpisodes(JsonElement element, List<(int Index, string Title, string VideoId)> episodes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var chapterId = FirstString(element, "chapterId", "videoId", "video_id");
                var bookId = FirstString(element, "bookId", "book_id");
                var index = FirstInt(element, "chapterIndex", "episodeNumber", "ep_num");
                if (!string.IsNullOrWhiteSpace(chapterId) && index is > 0)
                {
                    var title = FirstString(element, "chapterName", "title") ?? $"第{index}集";
                    var videoId = string.IsNullOrWhiteSpace(bookId) ? chapterId : $"{bookId}|{chapterId}";
                    episodes.Add((index.Value, title, videoId));
                }
                foreach (var property in element.EnumerateObject())
                    CollectEpisodes(property.Value, episodes);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectEpisodes(item, episodes);
                break;
        }
    }

    private static string? FindPlayUrl(JsonElement element, string chapterId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = FirstString(element, "chapterId", "videoId", "video_id");
            if (string.IsNullOrWhiteSpace(chapterId) || string.Equals(id, chapterId, StringComparison.Ordinal))
            {
                var direct = FirstString(element, "playUrl", "play_url", "videoPath", "video_path", "downloadUrl", "download_url", "mainUrl", "main_url", "fileUrl", "file_url");
                if (direct is not null && direct.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return direct;
                if (element.TryGetProperty("cdnList", out var cdn) || element.TryGetProperty("videoPathList", out cdn))
                {
                    var nested = FindPlayUrl(cdn, "");
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindPlayUrl(property.Value, chapterId);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPlayUrl(item, chapterId);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(chapterId))
        {
            var text = element.GetString();
            if (text is not null && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return null;
    }

    private async Task<JsonElement> GetAsync(DramaSourceSettings settings, string path, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(string.IsNullOrWhiteSpace(settings.DramaBoxApiBaseUrl) ? DefaultBaseUrl : settings.DramaBoxApiBaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!response.IsSuccessStatusCode)
        {
            var message = document.RootElement.TryGetProperty("error", out var error)
                ? GetString(error, "message")
                : null;
            throw new InvalidOperationException(message ?? $"DramaBox 本机接口 HTTP {(int)response.StatusCode}");
        }
        return document.RootElement.Clone();
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, JsonElement payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.GetRawText());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static IReadOnlyList<DramaSearchItem> ReadItems(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new DramaSearchItem(
                EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id"), BookPrefix),
                GetString(item, "title") ?? string.Empty,
                GetString(item, "category") ?? string.Empty,
                GetInt(item, "episode_cnt") ?? 0,
                GetString(item, "intro") ?? string.Empty,
                GetString(item, "cover") ?? string.Empty,
                GetString(item, "author") ?? string.Empty,
                GetString(item, "publish_time") ?? string.Empty,
                0,
                0))
            .Where(item => item.BookId.Length > BookPrefix.Length)
            .ToArray();
    }

    private static string NormalizeBaseUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private static string EnsurePrefixed(string? value, string prefix)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text : prefix + text;
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static int ParsePage(string? value) => int.TryParse(value, out var page) ? Math.Max(1, page) : 1;

    private static string Trim(string text) =>
        text.Length <= 180 ? text.Replace('\r', ' ').Replace('\n', ' ') : text[..180];

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static int? FirstInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetInt(element, name);
            if (value is not null)
                return value;
        }
        return null;
    }

    private sealed record JsonObjectLite(
        string BookId,
        string Title,
        string Category,
        int EpisodeCount,
        string Intro,
        string Cover,
        string Author,
        string PublishTime);
}
