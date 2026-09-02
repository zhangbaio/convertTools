using ShortDrama.Core.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>Mapleleaf 1.6.5 REST data source.</summary>
public sealed class MapleleafApiService
{
    public const string BookPrefix = "mapleleaf:";
    public const string EpisodePrefix = "mapleleaf_ep:";
    public const string ClientName = "Mapleleaf";
    public const string ClientVersion = "1.6.5";

    private static readonly string[] DefaultApiBases =
    [
        "http://106.54.36.244/api",
        "http://175.24.138.161/api",
        "http://124.221.67.210/api",
        "http://118.89.198.57/api",
        "http://111.229.141.69/api",
        "http://8.133.218.237/api"
    ];

    private const string PreferredSearchBase = "http://118.89.198.57/api";
    private const string DefaultPhpParseUrl = "http://47.116.45.15/index.php";
    private const string DefaultOfficialVideoParseUrl = "http://118.89.198.57/api/jxurl.php";
    private const int SearchPageSize = 10;
    private const int LatestMaxPages = 20;

    private readonly HttpClient _httpClient;
    private readonly HongguoLocalApiService _localService;
    private readonly IReadOnlyList<string> _apiBases;
    private readonly string _phpParseUrl;
    private readonly string _officialVideoParseUrl;
    private readonly Func<string, string?> _latestCachePathResolver;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string _token = string.Empty;
    private string _tokenAccount = string.Empty;
    private string _tokenDevice = string.Empty;

    public MapleleafApiService(HttpClient httpClient)
        : this(
            httpClient,
            new HongguoLocalApiService(httpClient),
            null,
            null,
            ResolveOfficialLatestCachePath,
            null)
    {
    }

    internal MapleleafApiService(
        HttpClient httpClient,
        HongguoLocalApiService localService,
        IReadOnlyList<string>? apiBases,
        string? phpParseUrl,
        Func<string, string?>? latestCachePathResolver = null,
        string? officialVideoParseUrl = null)
    {
        _httpClient = httpClient;
        _localService = localService;
        _apiBases = apiBases is { Count: > 0 } ? apiBases : DefaultApiBases;
        _phpParseUrl = string.IsNullOrWhiteSpace(phpParseUrl) ? DefaultPhpParseUrl : phpParseUrl.Trim();
        _officialVideoParseUrl = string.IsNullOrWhiteSpace(officialVideoParseUrl)
            ? DefaultOfficialVideoParseUrl
            : officialVideoParseUrl.Trim();
        _latestCachePathResolver = latestCachePathResolver ?? ResolveOfficialLatestCachePath;
    }

    public async Task<MapleleafLoginProbeResult> ProbeLoginAsync(
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(settings);
        var data = await LoginAsync(credentials, cancellationToken);
        var token = ReadString(data, "accessToken", "token");
        return new MapleleafLoginProbeResult(
            token,
            ReadString(data, "email") is { Length: > 0 } email ? email : credentials.Account,
            ReadString(data, "memberEndDate", "vipExpDate"));
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (keyword.Length == 0)
            return [];

        var body = new JsonObject
        {
            ["query"] = keyword,
            ["tabType"] = ResolveTabType(settings),
            ["offset"] = Math.Max(0, page - 1) * SearchPageSize,
            ["count"] = SearchPageSize,
            ["pointsRequired"] = 1
        };

        Exception? lastError = null;
        foreach (var baseUrl in SearchBases())
        {
            try
            {
                var inner = await SendAuthenticatedUrlAsync(
                    settings, baseUrl + "/search.php", body, cancellationToken);
                var mapped = MapSearchItems(inner);
                if (mapped.Count > 0)
                    return mapped;
            }
            catch (MapleleafException ex)
            {
                lastError = ex;
                if (ex.Message.Contains("会员", StringComparison.Ordinal))
                    throw;
            }
        }

        foreach (var path in new[] { "/ThirdParty/newsearch", "/ThirdParty/search" })
        {
            try
            {
                var inner = await SendAuthenticatedAsync(settings, path, body, cancellationToken);
                var mapped = MapSearchItems(inner);
                if (mapped.Count > 0)
                    return mapped;
            }
            catch (MapleleafException ex)
            {
                lastError = ex;
                if (ex.Message.Contains("会员", StringComparison.Ordinal))
                    throw;
            }
        }

        if (HasLocalParser(settings))
        {
            var local = await _localService.SearchAsync(settings, keyword, page, cancellationToken);
            return local.Select(item => item with { BookId = EnsureBookPrefix(StripKnownPrefix(item.BookId)) }).ToArray();
        }

        if (lastError is not null && !IsUnavailableSearchError(lastError))
            throw lastError;
        return [];
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetLatestAsync(
        DramaSourceSettings settings,
        string mode,
        int days,
        CancellationToken cancellationToken)
    {
        var action = mode.Trim().ToLowerInvariant() switch
        {
            "djnew" => "short",
            "mjnew" => "comic",
            "aiju" => "ai_real",
            _ => throw new MapleleafException($"不支持的 Mapleleaf 上新类型：{mode}")
        };
        var collected = new List<DramaSearchItem>();
        var seenBookIds = new HashSet<string>(StringComparer.Ordinal);
        var officialCacheItems = LoadOfficialLatestCache(action);
        for (var page = 1; page <= LatestMaxPages; page++)
        {
            JsonNode? inner;
            try
            {
                inner = await SendAuthenticatedAsync(
                    settings,
                    "/ThirdParty/latest",
                    new JsonObject
                    {
                        ["type"] = action,
                        ["action"] = action,
                        ["page"] = page,
                        ["pageSize"] = 50,
                        ["pointsRequired"] = 1
                    },
                    cancellationToken);
            }
            catch (MapleleafException) when (officialCacheItems.Count > 0)
            {
                collected.Clear();
                collected.AddRange(officialCacheItems);
                break;
            }

            if (inner is JsonObject warming && ReadBool(warming, "warming") && !ReadBool(warming, "ready"))
                throw new MapleleafException(ReadString(warming, "message") is { Length: > 0 } message ? message : "数据预热中，请稍后重试");

            foreach (var item in MapSearchItems(inner))
            {
                if (seenBookIds.Add(item.BookId))
                    collected.Add(item);
            }
            if (inner is not JsonObject pageResult || !ReadBool(pageResult, "has_more"))
                break;
        }

        var indexes = collected
            .Select((item, index) => (item.BookId, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .GroupBy(item => item.BookId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        foreach (var cachedItem in officialCacheItems)
        {
            if (indexes.TryGetValue(cachedItem.BookId, out var existingIndex))
            {
                var current = collected[existingIndex];
                if (!TryParseDate(current.PublishTime, out var currentTime) ||
                    (TryParseDate(cachedItem.PublishTime, out var cachedTime) && cachedTime >= currentTime))
                {
                    collected[existingIndex] = cachedItem;
                }
                continue;
            }

            indexes[cachedItem.BookId] = collected.Count;
            collected.Add(cachedItem);
        }

        var cutoff = DateTimeOffset.Now.Date.AddDays(-Math.Max(1, days) + 1);
        return collected
            .Where(item => !TryParseDate(item.PublishTime, out var date) || date.Date >= cutoff)
            .OrderByDescending(item => TryParseDate(item.PublishTime, out var date) ? date : DateTimeOffset.MinValue)
            .ToArray();
    }

    public async Task<IReadOnlyList<MapleleafEpisodeInfo>> GetEpisodesAsync(
        DramaSourceSettings settings,
        string prefixedOrRawBookId,
        CancellationToken cancellationToken)
    {
        var bookId = StripPrefix(prefixedOrRawBookId, BookPrefix);
        if (bookId.Length == 0)
            throw new MapleleafException("book_id 不能为空");
        var inner = await SendAuthenticatedAsync(
            settings,
            "/ThirdParty/videolist",
            new JsonObject { ["bookId"] = bookId, ["pointsRequired"] = 1 },
            cancellationToken);

        var result = new List<MapleleafEpisodeInfo>();
        var index = 1;
        foreach (var item in ExtractItems(inner, "剧集"))
        {
            var videoId = ReadString(item, "video_id", "videoId", "id");
            if (videoId.Length == 0)
                continue;
            var title = ReadString(item, "title", "name");
            var number = ReadInt(item, "episode", "episodeNumber", "index") ?? ExtractEpisodeNumber(title, index);
            result.Add(new MapleleafEpisodeInfo(
                number,
                title.Length > 0 ? title : $"第{number}集",
                EnsureEpisodePrefix(videoId),
                ReadString(item, "cover", "poster", "thumb_url")));
            index++;
        }
        return result;
    }

    private static string? ResolveOfficialLatestCachePath(string action)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "HongGuo", "Client", $"latest-cache-{action}.json");
    }

    private IReadOnlyList<DramaSearchItem> LoadOfficialLatestCache(string action)
    {
        string? path;
        try
        {
            path = _latestCachePathResolver(action);
        }
        catch
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
                return [];
            var cacheDate = ReadString(root, "Date", "date")
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("/", string.Empty, StringComparison.Ordinal);
            if (!string.Equals(cacheDate, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                return [];
            var rawItems = root["Items"] as JsonArray ?? root["items"] as JsonArray;
            if (rawItems is null)
                return [];
            return rawItems
                .OfType<JsonObject>()
                .Select(MapOfficialCacheItem)
                .Where(item => item.BookId.Length > BookPrefix.Length)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DramaSearchItem MapOfficialCacheItem(JsonObject item) =>
        new(
            EnsureBookPrefix(ReadString(item, "BookId", "book_id", "bookId")),
            ReadString(item, "Title", "title"),
            ReadString(item, "Type", "type"),
            ReadInt(item, "ChapterCountStr", "episode_cnt", "episodeCount") ?? 0,
            ReadString(item, "Intro", "intro"),
            ReadString(item, "CoverUrl", "cover", "coverUrl"),
            ReadString(item, "Author", "author"),
            ReadString(item, "OnlineTimeStr", "online_time", "onlineTime"),
            ReadInt(item, "PlayCountStr", "favorite_count", "play_cnt", "playCnt") ?? 0);

    public async Task<MapleleafVideoPlayback> GetVideoPlaybackAsync(
        DramaSourceSettings settings,
        string prefixedOrRawVideoId,
        string quality,
        CancellationToken cancellationToken) =>
        await GetVideoPlaybackAsync(
            settings,
            prefixedOrRawVideoId,
            quality,
            requestTimeoutSeconds: 15,
            cancellationToken).ConfigureAwait(false);

    public async Task<MapleleafVideoPlayback> GetVideoPlaybackAsync(
        DramaSourceSettings settings,
        string prefixedOrRawVideoId,
        string quality,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        requestTimeoutSeconds = Math.Clamp(requestTimeoutSeconds, 5, 60);
        var videoId = StripPrefix(prefixedOrRawVideoId, EpisodePrefix);
        if (videoId.Length == 0)
            throw new MapleleafException("video_id 不能为空");

        if (LooksLikeHttpUrl(videoId))
            return await ParseShareUrlAsync(
                settings,
                videoId,
                quality,
                requestTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

        // Mapleleaf 1.6.5 uses a dedicated PHP endpoint for episode parsing. It is not
        // /ThirdParty/videoparse: the official client posts both videoId aliases and wrap=1
        // to 118.89.198.57/api/jxurl.php, authenticated with the Mapleleaf bearer token.
        Exception? lastError = null;
        try
        {
            return await ParseVideoViaOfficialClientAsync(
                settings,
                videoId,
                quality,
                requestTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        // Keep the historical REST endpoint as a compatibility fallback in case a
        // deployment still exposes it, but never let it replace the official PHP path.
        try
        {
            var inner = await SendAuthenticatedAsync(
                settings,
                "/ThirdParty/videoparse",
                new JsonObject { ["videoId"] = videoId, ["level"] = NormalizeQuality(quality) },
                cancellationToken,
                requestTimeoutSeconds).ConfigureAwait(false);
            var url = ExtractPlayUrl(inner);
            if (url.Length > 0)
                return new MapleleafVideoPlayback(url, ReadSize(inner));
            lastError = new MapleleafException(ReadString(inner, "message", "msg") is { Length: > 0 } message ? message : "未找到可用的视频流");
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        // Keep the local Hongguo API as a compatibility fallback for periods when
        // the official parser is unavailable. Its URL may be a slower proxy stream.
        if (HasLocalParser(settings))
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
                var local = await _localService.GetVideoPlaybackAsync(
                    settings,
                    videoId,
                    quality,
                    timeoutCts.Token).ConfigureAwait(false);
                return new MapleleafVideoPlayback(
                    local.Url,
                    0,
                    local.EncryptedUrls,
                    local.SpadeA,
                    local.Encrypted);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new MapleleafException(
                    $"本地红果解析超过 {requestTimeoutSeconds} 秒，已停止等待",
                    408,
                    ex);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var hint = "Mapleleaf 后端未返回播放直链";
        if (!HasLocalParser(settings))
            hint += "。请在系统设置配置‘本地直连红果 API’后重试";
        if (!string.IsNullOrWhiteSpace(lastError?.Message))
            hint += $"（{lastError.Message}）";
        throw new MapleleafException(hint, inner: lastError);
    }

    private async Task<MapleleafVideoPlayback> ParseVideoViaOfficialClientAsync(
        DramaSourceSettings settings,
        string videoId,
        string quality,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var inner = await SendAuthenticatedUrlAsync(
            settings,
            _officialVideoParseUrl,
            new JsonObject
            {
                ["videoId"] = videoId,
                ["video_id"] = videoId,
                ["level"] = NormalizeQuality(quality),
                ["wrap"] = "1"
            },
            cancellationToken,
            requestTimeoutSeconds).ConfigureAwait(false);
        var url = ExtractPlayUrlForQuality(inner, NormalizeQuality(quality));
        if (url.Length == 0)
        {
            throw new MapleleafException(
                ReadString(inner, "message", "msg") is { Length: > 0 } message
                    ? message
                    : "Mapleleaf 官方 jxurl.php 未返回播放直链");
        }

        return new MapleleafVideoPlayback(url, ReadSize(inner));
    }

    private static string ExtractPlayUrlForQuality(JsonNode? node, string requestedQuality)
    {
        if (node is not JsonArray options || options.Count == 0)
        {
            return ExtractPlayUrl(node);
        }

        var order = new[] { "2160p", "1440p", "1080p", "720p", "540p", "480p", "360p" };
        var normalized = NormalizeQuality(requestedQuality);
        var requestedIndex = Array.IndexOf(order, normalized);
        IEnumerable<string> candidates = requestedIndex >= 0 ? order.Skip(requestedIndex) : [normalized];
        foreach (var quality in candidates)
        {
            foreach (var option in options.OfType<JsonObject>())
            {
                var actual = ReadString(option, "quality", "definition").ToLowerInvariant();
                if (!string.Equals(actual, quality, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = ExtractPlayUrl(option);
                if (url.Length > 0)
                {
                    return url;
                }
            }
        }

        return ExtractPlayUrl(options[^1]);
    }

    private async Task<MapleleafVideoPlayback> ParseShareUrlAsync(
        DramaSourceSettings settings,
        string url,
        string quality,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(settings);
        var token = await EnsureTokenAsync(
            credentials,
            false,
            cancellationToken,
            requestTimeoutSeconds: requestTimeoutSeconds).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_phpParseUrl}?url={Uri.EscapeDataString(url)}&level={Uri.EscapeDataString(NormalizeQuality(quality))}");
        ApplyHeaders(request, credentials.DeviceId, token, hasJsonBody: false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        var node = await ReadResponseAsync(response, timeoutCts.Token).ConfigureAwait(false);
        var inner = Unwrap(node);
        var playUrl = ExtractPlayUrl(inner);
        if (playUrl.Length == 0)
            throw new MapleleafException("Mapleleaf PHP 解析响应缺少视频直链", (int)response.StatusCode);
        return new MapleleafVideoPlayback(playUrl, ReadSize(inner));
    }

    private async Task<JsonNode?> SendAuthenticatedAsync(
        DramaSourceSettings settings,
        string path,
        JsonObject body,
        CancellationToken cancellationToken,
        int? requestTimeoutSeconds = null)
    {
        Exception? lastError = null;
        foreach (var baseUrl in _apiBases)
        {
            try
            {
                return await SendAuthenticatedUrlAsync(
                    settings,
                    baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'),
                    body,
                    cancellationToken,
                    requestTimeoutSeconds).ConfigureAwait(false);
            }
            catch (MapleleafException ex) when (IsHostFailoverError(ex))
            {
                lastError = ex;
            }
        }
        throw lastError ?? new MapleleafException("Mapleleaf 所有 API 主机均不可用");
    }

    private async Task<JsonNode?> SendAuthenticatedUrlAsync(
        DramaSourceSettings settings,
        string url,
        JsonObject body,
        CancellationToken cancellationToken,
        int? requestTimeoutSeconds = null)
    {
        var credentials = ResolveCredentials(settings);
        var token = await EnsureTokenAsync(
            credentials,
            false,
            cancellationToken,
            requestTimeoutSeconds: requestTimeoutSeconds).ConfigureAwait(false);
        try
        {
            return await SendJsonAsync(
                url,
                credentials.DeviceId,
                token,
                body,
                cancellationToken,
                requestTimeoutSeconds: requestTimeoutSeconds).ConfigureAwait(false);
        }
        catch (MapleleafException ex) when (ShouldRefreshToken(ex))
        {
            token = await EnsureTokenAsync(
                credentials,
                true,
                cancellationToken,
                token,
                requestTimeoutSeconds).ConfigureAwait(false);
            return await SendJsonAsync(
                url,
                credentials.DeviceId,
                token,
                body,
                cancellationToken,
                requestTimeoutSeconds: requestTimeoutSeconds).ConfigureAwait(false);
        }
    }

    private async Task<string> EnsureTokenAsync(
        Credentials credentials,
        bool forceRefresh,
        CancellationToken cancellationToken,
        string? staleToken = null,
        int? requestTimeoutSeconds = null)
    {
        if (!forceRefresh && TokenMatches(credentials))
            return _token;
        if (forceRefresh && staleToken is not null && TokenMatches(credentials) &&
            !string.Equals(_token, staleToken, StringComparison.Ordinal))
            return _token;
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && TokenMatches(credentials))
                return _token;
            if (forceRefresh && staleToken is not null && TokenMatches(credentials) &&
                !string.Equals(_token, staleToken, StringComparison.Ordinal))
                return _token;
            var data = await LoginAsync(credentials, cancellationToken, requestTimeoutSeconds).ConfigureAwait(false);
            var token = ReadString(data, "accessToken", "token");
            if (token.Length == 0)
                throw new MapleleafException("登录响应中未找到 accessToken");
            _token = token;
            _tokenAccount = credentials.Account;
            _tokenDevice = credentials.DeviceId;
            return token;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private bool TokenMatches(Credentials credentials) =>
        _token.Length > 0 &&
        string.Equals(_tokenAccount, credentials.Account, StringComparison.Ordinal) &&
        string.Equals(_tokenDevice, credentials.DeviceId, StringComparison.Ordinal);

    private async Task<JsonNode?> LoginAsync(
        Credentials credentials,
        CancellationToken cancellationToken,
        int? requestTimeoutSeconds = null)
    {
        Exception? lastError = null;
        var body = new JsonObject
        {
            ["email"] = credentials.Account,
            ["password"] = credentials.Password,
            ["deviceId"] = credentials.DeviceId,
            ["deviceInfo"] = $"Windows {Environment.OSVersion.Version} | {Environment.MachineName}"
        };
        foreach (var baseUrl in _apiBases)
        {
            try
            {
                return await SendJsonAsync(
                    baseUrl.TrimEnd('/') + "/User/login",
                    credentials.DeviceId,
                    null,
                    body,
                    cancellationToken,
                    unwrap: false,
                    requestTimeoutSeconds: requestTimeoutSeconds).ConfigureAwait(false);
            }
            catch (MapleleafException ex) when (IsHostFailoverError(ex))
            {
                lastError = ex;
            }
        }
        throw lastError ?? new MapleleafException("Mapleleaf 所有 API 主机均不可用");
    }

    private async Task<JsonNode?> SendJsonAsync(
        string url,
        string deviceId,
        string? token,
        JsonObject body,
        CancellationToken cancellationToken,
        bool unwrap = true,
        int? requestTimeoutSeconds = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        ApplyHeaders(request, deviceId, token, hasJsonBody: true);
        using var timeoutCts = requestTimeoutSeconds is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSeconds!.Value, 5, 60)));
        }
        var requestToken = timeoutCts?.Token ?? cancellationToken;
        try
        {
            using var response = await _httpClient.SendAsync(request, requestToken).ConfigureAwait(false);
            var outer = await ReadResponseAsync(response, requestToken).ConfigureAwait(false);
            return unwrap ? Unwrap(outer) : outer?["data"] ?? outer;
        }
        catch (HttpRequestException ex)
        {
            throw new MapleleafException($"Mapleleaf 网络异常：{ex.Message}", inner: ex);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested &&
            (timeoutCts?.IsCancellationRequested == true || ex is TaskCanceledException))
        {
            var seconds = requestTimeoutSeconds is > 0
                ? Math.Clamp(requestTimeoutSeconds.Value, 5, 60)
                : Math.Max(1, (int)_httpClient.Timeout.TotalSeconds);
            throw new MapleleafException($"Mapleleaf 网络请求超过 {seconds} 秒", 408, ex);
        }
    }

    private static async Task<JsonNode?> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (text.Length == 0)
            throw new MapleleafException($"Mapleleaf 响应为空（HTTP {(int)response.StatusCode}）", (int)response.StatusCode);
        JsonNode? outer;
        try
        {
            outer = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new MapleleafException($"Mapleleaf 返回了非 JSON 响应：{text[..Math.Min(200, text.Length)]}", (int)response.StatusCode, ex);
        }
        if (outer is not JsonObject obj)
            throw new MapleleafException("Mapleleaf 响应格式异常：外层不是对象", (int)response.StatusCode);
        if (ReadBool(obj, "success") == false && obj.ContainsKey("success"))
            throw new MapleleafException(ReadString(obj, "message", "msg") is { Length: > 0 } message ? message : "请求失败", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode && !ReadBool(obj, "success"))
            throw new MapleleafException($"HTTP {(int)response.StatusCode}：{ReadString(obj, "message", "msg")}", (int)response.StatusCode);
        return outer;
    }

    private static JsonNode? Unwrap(JsonNode? outer)
    {
        if (outer is not JsonObject obj)
            return outer;
        if (obj["rawData"] is JsonValue raw)
            return ParseRawValue(raw);
        if (obj["data"] is JsonObject dataObj && dataObj["rawData"] is JsonValue nestedRaw)
            return ParseRawValue(nestedRaw);
        return obj["data"] ?? outer;
    }

    private static JsonNode? ParseRawValue(JsonValue raw)
    {
        var text = raw.ToString().Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
            return raw;
        try { return JsonNode.Parse(text); }
        catch (JsonException ex) { throw new MapleleafException($"Mapleleaf rawData 解析失败：{ex.Message}", inner: ex); }
    }

    private static IReadOnlyList<DramaSearchItem> MapSearchItems(JsonNode? inner)
    {
        return ExtractItems(inner, "搜索")
            .Select(MapSearchItem)
            .Where(item => item.BookId.Length > BookPrefix.Length)
            .ToArray();
    }

    private static DramaSearchItem MapSearchItem(JsonObject item)
    {
        var nested = FirstNestedBook(item);
        string First(params string[] keys)
        {
            var value = ReadString(item, keys);
            return value.Length > 0 ? value : ReadString(nested, keys);
        }
        var rawBookId = First("book_id", "bookId", "id");
        var publishTime = First("publish_time", "publishTime", "online_time", "onlineTime", "ctime");
        return new DramaSearchItem(
            EnsureBookPrefix(rawBookId),
            First("title", "book_name", "name"),
            First("category", "type"),
            ReadInt(item, "episode_cnt", "episodeCount", "chapterCount") ?? ReadInt(nested, "episode_cnt", "episodeCount") ?? 0,
            First("intro", "abstract", "description"),
            First("cover", "cover_url", "coverUrl", "thumb_url", "poster"),
            First("author", "author_name", "authorName"),
            publishTime,
            ReadInt(item, "favorite_count", "play_cnt", "playCnt") ?? ReadInt(nested, "favorite_count", "play_cnt") ?? 0);
    }

    private static List<JsonObject> ExtractItems(JsonNode? inner, string label)
    {
        if (inner is JsonArray array)
            return array.OfType<JsonObject>().ToList();
        if (inner is not JsonObject obj)
            throw new MapleleafException($"{label}响应格式异常");
        var code = ReadInt(obj, "code") ?? 0;
        if (code != 0 && code != 200)
            throw new MapleleafException(ReadString(obj, "msg", "message") is { Length: > 0 } message ? message : $"{label}失败 code={code}", code);
        foreach (var key in new[] { "data", "items", "list", "records", "searchData", "SearchData" })
        {
            if (obj[key] is JsonArray items)
                return items.OfType<JsonObject>().ToList();
            if (obj[key] is JsonObject nested)
            {
                try { return ExtractItems(nested, label); }
                catch (MapleleafException) { }
            }
        }
        return [];
    }

    private IEnumerable<string> SearchBases()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { PreferredSearchBase }.Concat(_apiBases))
        {
            var normalized = value.TrimEnd('/');
            if (seen.Add(normalized)) yield return normalized;
        }
    }

    private static Credentials ResolveCredentials(DramaSourceSettings settings)
    {
        var account = (settings.MapleleafAccount ?? string.Empty).Trim();
        var password = settings.MapleleafPassword ?? string.Empty;
        var deviceId = (settings.MapleleafUdid ?? string.Empty).Trim();
        if (account.Length == 0) throw new MapleleafException("请先在系统设置填写 Mapleleaf 账号");
        if (password.Length == 0) throw new MapleleafException("请先在系统设置填写 Mapleleaf 密码");
        if (deviceId.Length == 0) throw new MapleleafException("请先填写、读取或生成 Mapleleaf 设备号（DeviceUDID）");
        return new Credentials(account, password, deviceId);
    }

    private static void ApplyHeaders(HttpRequestMessage request, string deviceId, string? token, bool hasJsonBody)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", $"{ClientName}-Client/{ClientVersion} (Windows)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Client-Name", ClientName);
        request.Headers.TryAddWithoutValidation("X-Client-Version", ClientVersion);
        request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.ConnectionClose = true;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _ = hasJsonBody;
    }

    private static bool ShouldRefreshToken(MapleleafException ex)
    {
        if (ex.Code is 401 or 403) return true;
        var message = ex.Message.ToLowerInvariant();
        return new[] { "token不存在", "token已失效", "登录已失效", "登录过期", "未登录", "重新登录", "unauthorized", "jwt" }
            .Any(message.Contains);
    }

    private static bool IsHostFailoverError(MapleleafException ex) =>
        ex.Code is 0 or 404 or 405 or 408 or 429 || ex.Code >= 500;

    private static bool IsUnavailableSearchError(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return ex is MapleleafException { Code: 404 } || message.Contains("未搜索到") || message.Contains("没有更多") || message.Contains("timeout") || message.Contains("网络异常");
    }

    private static bool HasLocalParser(DramaSourceSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.HongguoLocalBaseUrl) &&
        !string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey);

    private static JsonObject FirstNestedBook(JsonObject item) =>
        (item["books"] ?? item["Books"]) is JsonArray { Count: > 0 } books && books[0] is JsonObject book ? book : new JsonObject();

    private static string ReadString(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj) return string.Empty;
        foreach (var key in keys)
        {
            if (obj[key] is not JsonValue value) continue;
            var text = value.ToString().Trim();
            if (text.Length > 0 && !string.Equals(text, "null", StringComparison.OrdinalIgnoreCase)) return text;
        }
        return string.Empty;
    }

    private static int? ReadInt(JsonNode? node, params string[] keys)
    {
        var text = ReadString(node, keys);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return (int)number;
        return null;
    }

    private static bool ReadBool(JsonNode? node, string key)
    {
        var text = ReadString(node, key);
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    private static string ExtractPlayUrl(JsonNode? node)
    {
        if (node is JsonValue value)
            return LooksLikeHttpUrl(value.ToString()) ? value.ToString().Trim() : string.Empty;
        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = ExtractPlayUrl(child);
                if (found.Length > 0) return found;
            }
            return string.Empty;
        }
        if (node is not JsonObject obj) return string.Empty;
        foreach (var key in new[] { "url", "play_url", "playUrl", "video_url", "videoUrl", "download_url", "downloadUrl", "down_url", "downUrl", "backup_url", "backupUrl", "parsedVideoUrl", "parsedBackupVideoUrl" })
        {
            var candidate = ReadString(obj, key);
            if (LooksLikeHttpUrl(candidate)) return candidate;
        }
        foreach (var key in new[] { "data", "info" })
        {
            var found = ExtractPlayUrl(obj[key]);
            if (found.Length > 0) return found;
        }
        return string.Empty;
    }

    private static long ReadSize(JsonNode? node)
    {
        var text = ReadString(node, "size");
        if (long.TryParse(text, out var raw)) return raw;
        var match = System.Text.RegularExpressions.Regex.Match(text.ToUpperInvariant().Replace(" ", string.Empty), @"^([0-9]*\.?[0-9]+)([KMGT]?B)$");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return 0;
        var multiplier = match.Groups[2].Value switch { "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, "TB" => 1024d * 1024 * 1024 * 1024, _ => 1d };
        return (long)(number * multiplier);
    }

    private static int ResolveTabType(DramaSourceSettings settings) =>
        (settings.PikachuDramaType ?? string.Empty).Trim().ToLowerInvariant() is "manga" or "comic" ? 1 : 0;

    private static string NormalizeQuality(string quality) =>
        (quality ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "1080p+" or "4k" or "2160" or "2160p" => "2160p",
            "1080" or "1080p" => "1080p",
            "720" or "720p" => "720p",
            "480" or "480p" => "480p",
            _ => "360p"
        };

    private static bool TryParseDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date) ||
        DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out date);

    private static int ExtractEpisodeNumber(string title, int fallback)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title ?? string.Empty, @"(?:第\s*)?(\d+)\s*集");
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : fallback;
    }

    private static bool LooksLikeHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public static string EnsureBookPrefix(string value) =>
        value.StartsWith(BookPrefix, StringComparison.OrdinalIgnoreCase) ? value : BookPrefix + value.Trim();

    public static string EnsureEpisodePrefix(string value) =>
        value.StartsWith(EpisodePrefix, StringComparison.OrdinalIgnoreCase) ? value : EpisodePrefix + value.Trim();

    public static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value.Trim();

    private static string StripKnownPrefix(string value)
    {
        var separator = value.IndexOf(':');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private sealed record Credentials(string Account, string Password, string DeviceId);
    public sealed record MapleleafLoginProbeResult(string Token, string Email, string VipExpiresAt);
    public sealed record MapleleafEpisodeInfo(int EpisodeNumber, string Title, string VideoId, string PosterUrl);
    public sealed record MapleleafVideoPlayback(
        string Url,
        long Size,
        IReadOnlyList<string>? EncryptedUrls = null,
        string SpadeA = "",
        bool Encrypted = false)
    {
        public IReadOnlyList<string> CdnUrls => EncryptedUrls ?? [];
    }
}
