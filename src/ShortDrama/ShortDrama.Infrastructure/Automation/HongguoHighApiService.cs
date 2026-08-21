using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoHighApiService
{
    public const string FanqieWeChatUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) " +
        "AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 " +
        "MicroMessenger/8.0.60(0x18003c2c) NetType/WIFI Language/zh_CN";

    public const string NovelFmSearchUrl =
        "https://api5-sinfonlinea.novelfm.com/novelfm/bookmall/search/page/v1/";

    public const string FanqieDirectoryUrl =
        "https://api-sinfonlinec.fanqiesdk.com/api/novel/book/audio/directory/list/v1";

    private static readonly Dictionary<string, string> NovelFmSearchQuery = new()
    {
        ["device_platform"] = "android",
        ["aid"] = "3040",
        ["manifest_version_code"] = "628",
        ["update_version_code"] = "62832"
    };

    private readonly HttpClient _httpClient;
    private readonly object _gate = new();
    private readonly HongguoHighSession _session = new();
    private HongguoHighDevice? _device;

    public HongguoHighApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<HongguoLoginProbeResult> ProbeLoginAsync(
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var data = await LoginAsync(settings, cancellationToken);
        var token = HongguoHighCrypto.TrimBearer(GetString(data, "token") ?? GetString(data, "accessToken"));
        var info = data["info"] as JsonObject;
        return new HongguoLoginProbeResult(
            Token: token,
            Email: GetString(info, "email") ?? GetString(data, "email") ?? settings.HghighAccount,
            VipExpiresAt: GetString(info, "memberEndDate")
                          ?? GetString(info, "expiresAt")
                          ?? GetString(data, "memberEndDate")
                          ?? "");
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var offset = Math.Max(0, (Math.Max(1, page) - 1) * 20);
        var body = new JsonObject
        {
            ["limit"] = 20,
            ["offset"] = offset,
            ["query"] = trimmed,
            ["search_ctx_info"] = "",
            ["search_entrance"] = """{"bottom_type":1,"default_tab_type":10,"search_tab_id":13,"tab_type":39,"type":1}""",
            ["search_id"] = "",
            ["sub_tab_type"] = 31,
            ["tab_type"] = 13
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildNovelFmSearchUri())
        {
            Content = JsonContent(body)
        };
        request.Headers.TryAddWithoutValidation("User-Agent", FanqieWeChatUa);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await ReadJsonAsync(response, cancellationToken);
        EnsureBusinessOk(payload, "搜索失败");
        var data = payload["data"] as JsonObject;
        var searchData = data?["search_data"] as JsonArray;
        if (searchData is null)
        {
            return [];
        }

        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryNode in searchData)
        {
            if (entryNode is not JsonObject entry)
            {
                continue;
            }

            var books = entry["books"] as JsonArray;
            var book = books?.FirstOrDefault() as JsonObject ?? entry;
            var bookId = GetString(book, "book_id") ?? GetString(entry, "book_id");
            if (string.IsNullOrWhiteSpace(bookId) || !seen.Add(bookId))
            {
                continue;
            }

            results.Add(new DramaSearchItem(
                BookId: HongguoHighCrypto.EnsureBookPrefix(bookId),
                Title: GetString(book, "book_name") ?? GetString(book, "title") ?? "",
                Category: GetString(book, "category") ?? "",
                EpisodeTotal: GetInt(book, "chapter_number") ?? GetInt(book, "serial_count") ?? 0,
                Intro: GetString(book, "abstract") ?? "",
                PosterUrl: GetString(book, "audio_thumb_uri") ?? GetString(book, "thumb_uri") ?? "",
                Author: GetString(book, "author") ?? GetString(book, "anchor") ?? "",
                PublishTime: "",
                FavoriteCount: GetInt(book, "favorite_count") ?? 0));
        }

        return results;
    }

    public async Task<IReadOnlyList<HongguoNewApiService.HongguoEpisodeInfo>> GetEpisodesAsync(
        DramaSourceSettings settings,
        string bookId,
        CancellationToken cancellationToken)
    {
        var rawBookId = HongguoHighCrypto.StripBookPrefix(bookId);
        if (string.IsNullOrWhiteSpace(rawBookId))
        {
            return [];
        }

        var uri = new UriBuilder(FanqieDirectoryUrl)
        {
            Query = $"book_id={Uri.EscapeDataString(rawBookId)}&aid={HongguoHighCrypto.FanqieDirectoryAid}"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", FanqieWeChatUa);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await ReadJsonAsync(response, cancellationToken);
        EnsureBusinessOk(payload, "剧集目录失败");
        var data = payload["data"] as JsonObject ?? payload;
        var itemList = data["item_list"] as JsonArray;
        if (itemList is null)
        {
            return [];
        }

        var episodes = new List<HongguoNewApiService.HongguoEpisodeInfo>();
        var index = 0;
        foreach (var item in itemList)
        {
            var videoId = NodeToText(item);
            if (string.IsNullOrWhiteSpace(videoId) || videoId is "null")
            {
                continue;
            }

            index++;
            episodes.Add(new HongguoNewApiService.HongguoEpisodeInfo(
                index,
                $"第{index}集",
                HongguoHighCrypto.EncodeEpisodeId(rawBookId, index, videoId),
                ""));
        }

        return episodes;
    }

    public async Task<HongguoNewApiService.HongguoVideoPlayback> GetVideoPlaybackAsync(
        DramaSourceSettings settings,
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        if (!HongguoHighCrypto.TryDecodeEpisodeId(videoId, out var bookId, out var episodeNumber, out var rawVideoId))
        {
            throw new HongguoHighException("高码率剧集标识无效");
        }

        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        var inner = await AuthedRequestAsync(
            settings,
            "/video/batch-parse",
            new JsonObject
            {
                ["bookId"] = bookId,
                ["book_id"] = bookId,
                ["episodes"] = new JsonArray { HongguoHighCrypto.BatchParseEpisodePayload(rawVideoId, episodeNumber) },
                ["quality"] = HongguoHighCrypto.NormalizeQuality(quality),
                ["resolution"] = HongguoHighCrypto.NormalizeQuality(quality)
            },
            timeout,
            cancellationToken);

        JsonObject? item = null;
        if (inner is JsonArray array)
        {
            item = array.FirstOrDefault() as JsonObject;
        }
        else if (inner is JsonObject obj)
        {
            foreach (var key in new[] { "data", "items", "results", "episodes" })
            {
                if (obj[key] is JsonArray nested)
                {
                    item = nested.FirstOrDefault() as JsonObject;
                    break;
                }
            }

            item ??= obj;
        }

        var url = GetString(item, "url")
                  ?? GetString(item, "video_url")
                  ?? GetString(item, "download_url")
                  ?? GetString(item, "playUrl")
                  ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new HongguoHighException("高码率解析未返回播放地址");
        }

        return new HongguoNewApiService.HongguoVideoPlayback(url, GetLong(item, "size") ?? 0);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetManjuNewAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken)
    {
        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        var pageSize = 20;
        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 8; page++)
        {
            var offset = (page - 1) * pageSize;
            var inner = await AuthedRequestAsync(
                settings,
                "/redguo/fanqie-new",
                new JsonObject
                {
                    ["append"] = false,
                    ["limit"] = pageSize,
                    ["page"] = page,
                    ["category"] = "comic",
                    ["channel"] = "fanqieBackup",
                    ["offset"] = offset
                },
                timeout,
                cancellationToken);
            var mapped = MapCalendarItems(inner);
            if (mapped.Count == 0)
            {
                break;
            }

            var added = 0;
            foreach (var item in mapped)
            {
                if (!seen.Add(item.BookId))
                {
                    continue;
                }

                results.Add(item);
                added++;
            }

            if (added == 0)
            {
                break;
            }
        }

        _ = days;
        return results;
    }

    private async Task<JsonObject> LoginAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var account = (settings.HghighAccount ?? "").Trim();
        var password = settings.HghighPassword ?? "";
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
        {
            throw new HongguoHighException("红果高码率未配置账号或密码（请在「系统设置 → 登录设置」填写独立账号）");
        }

        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        var device = LoadDevice();
        var node = await RequestAsync(
            device,
            _session,
            "POST",
            "/auth/login",
            new JsonObject
            {
                ["email"] = account,
                ["password"] = password,
                ["deviceId"] = device.DeviceId
            },
            timeout,
            cancellationToken);
        var data = node as JsonObject
                   ?? throw new HongguoHighException("登录响应格式异常");

        var token = HongguoHighCrypto.TrimBearer(
            GetString(data, "accessToken") ?? GetString(data, "token") ?? GetString(data, "j"));
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new HongguoHighException("登录响应中未找到 token");
        }

        lock (_gate)
        {
            _session.AccessToken = token;
            _session.Account = account;
            _session.BoundDeviceId = device.DeviceId;
            _session.FlowId = HongguoHighCrypto.ToBase64Url(RandomNumberGeneratorBytes(18))[..24];
            _session.SessionId = GetString(data, "session_id") ?? GetString(data, "sessionId") ?? GetString(data, "l") ?? "";
            _session.SessionKeyId = GetString(data, "sessionKeyId") ?? GetString(data, "session_key_id") ?? "session-v1";
            _session.SessionKeyB64 = GetString(data, "session_key") ?? GetString(data, "sessionKey") ?? GetString(data, "key") ?? "";
            data["token"] = token;
        }

        return data;
    }

    private async Task<JsonNode?> AuthedRequestAsync(
        DramaSourceSettings settings,
        string path,
        JsonObject data,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(settings, timeoutSeconds, cancellationToken);
        try
        {
            return await RequestAsync(LoadDevice(), _session, "POST", path, data, timeoutSeconds, cancellationToken);
        }
        catch (HongguoHighException ex) when (ShouldRelogin(ex))
        {
            lock (_gate)
            {
                _session.Clear();
            }

            await EnsureTokenAsync(settings, timeoutSeconds, cancellationToken);
            return await RequestAsync(LoadDevice(), _session, "POST", path, data, timeoutSeconds, cancellationToken);
        }
    }

    private async Task EnsureTokenAsync(DramaSourceSettings settings, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var account = (settings.HghighAccount ?? "").Trim();
        var device = LoadDevice();
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_session.AccessToken) &&
                _session.Account == account &&
                _session.BoundDeviceId == device.DeviceId)
            {
                return;
            }
        }

        _ = timeoutSeconds;
        await LoginAsync(settings, cancellationToken);
    }

    private async Task<JsonNode> RequestAsync(
        HongguoHighDevice device,
        HongguoHighSession session,
        string method,
        string path,
        JsonObject data,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedPath = "/" + (path ?? "").TrimStart('/');
        JsonObject envelope;
        lock (_gate)
        {
            var proof = HongguoHighDeviceStore.ResolveDeviceProof(device);
            if (HongguoHighCrypto.AuthPaths.Contains(normalizedPath))
            {
                var inner = HongguoHighCrypto.BuildStartupInner(device, normalizedPath, data, proof);
                var masters = HongguoHighDeviceStore.LoadStartupMasters(device);
                var (encKey, signKey) = HongguoHighCrypto.DeriveStartupKeys(masters.Enc, masters.Sign);
                envelope = HongguoHighCrypto.BuildStartupEnvelope(inner, method, normalizedPath, encKey, signKey);
            }
            else
            {
                var inner = HongguoHighCrypto.BuildBusinessInner(device, session, normalizedPath, data, proof);
                envelope = HongguoHighCrypto.BuildLetterEnvelope(device, session, inner, method, normalizedPath);
            }
        }

        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), JoinApi(normalizedPath))
        {
            Content = JsonContent(envelope)
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.TryAddWithoutValidation("X-App-Id", HongguoHighCrypto.AppId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", device.DeviceId);
        request.Headers.TryAddWithoutValidation("X-Client-Version", HongguoHighCrypto.ClientVersion);
        var bearer = HongguoHighCrypto.TrimBearer(session.AccessToken);
        if (!string.IsNullOrWhiteSpace(bearer) && !HongguoHighCrypto.AuthPaths.Contains(normalizedPath))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 120)));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        var payload = await ReadJsonAsync(response, timeoutCts.Token);
        if (response.StatusCode >= System.Net.HttpStatusCode.BadRequest && payload["code"] is null)
        {
            throw new HongguoHighException($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }

        return Unwrap(payload);
    }

    private HongguoHighDevice LoadDevice()
    {
        lock (_gate)
        {
            _device ??= HongguoHighDeviceStore.DetectDevice();
            return _device;
        }
    }

    private static IReadOnlyList<DramaSearchItem> MapCalendarItems(JsonNode? payload)
    {
        var results = new List<DramaSearchItem>();
        foreach (var obj in EnumerateObjects(payload))
        {
            var bookId = FirstString(obj, "book_id", "bookId", "series_id", "seriesId");
            if (string.IsNullOrWhiteSpace(bookId))
            {
                continue;
            }

            results.Add(new DramaSearchItem(
                BookId: HongguoHighCrypto.EnsureBookPrefix(bookId),
                Title: FirstString(obj, "title", "series_title", "book_name", "bookName", "name") ?? bookId,
                Category: FirstString(obj, "category", "tag_text", "type") ?? "",
                EpisodeTotal: GetInt(obj, "episode_cnt") ?? GetInt(obj, "chapter_number") ?? 0,
                Intro: FirstString(obj, "intro", "series_intro", "abstract", "description") ?? "",
                PosterUrl: FirstString(obj, "cover", "series_cover", "audio_thumb_uri", "thumb_uri", "poster") ?? "",
                Author: FirstString(obj, "author", "anchor") ?? "",
                PublishTime: FirstString(obj, "publish_time", "create_time", "online_time") ?? "",
                FavoriteCount: GetInt(obj, "favorite_count") ?? 0));
        }

        return results
            .GroupBy(item => item.BookId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IEnumerable<JsonObject> EnumerateObjects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var property in obj)
                {
                    foreach (var nested in EnumerateObjects(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in EnumerateObjects(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static JsonNode Unwrap(JsonObject payload)
    {
        var code = payload["code"]?.GetValue<int>() ?? 0;
        if (code is not (0 or 200))
        {
            throw new HongguoHighException(
                GetString(payload, "message") ?? GetString(payload, "msg") ?? "请求失败",
                code);
        }

        return payload["data"] ?? payload;
    }

    private static string NodeToText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>()?.Trim() ?? "",
            JsonValueKind.Number => node.ToJsonString(),
            _ => ""
        };
    }

    private static void EnsureBusinessOk(JsonObject payload, string fallback)
    {
        var code = payload["code"]?.GetValue<int>() ?? 0;
        if (code is not (0 or 200))
        {
            throw new HongguoHighException(GetString(payload, "message") ?? GetString(payload, "msg") ?? fallback, code);
        }
    }

    private static bool ShouldRelogin(HongguoHighException ex)
    {
        var message = ex.Message ?? "";
        if (message.Contains("路径签名", StringComparison.Ordinal) ||
            message.Contains("请求签名", StringComparison.Ordinal) ||
            message.Contains("path signature", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ex.Code is 401 or 403 ||
               message.Contains("登录", StringComparison.Ordinal) ||
               message.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(text)?.AsObject()
                   ?? throw new HongguoHighException($"HTTP {(int)response.StatusCode}：响应不是 JSON", (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new HongguoHighException($"HTTP {(int)response.StatusCode}：响应不是 JSON", (int)response.StatusCode, ex);
        }
    }

    private static StringContent JsonContent(JsonNode node) =>
        new(node.ToJsonString(HongguoHighCrypto.CompactJson), Encoding.UTF8, "application/json");

    private static Uri JoinApi(string path) =>
        new(HongguoHighCrypto.ApiBase.TrimEnd('/') + "/" + path.TrimStart('/'));

    private static Uri BuildNovelFmSearchUri()
    {
        var query = string.Join("&", NovelFmSearchQuery.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(NovelFmSearchUrl + "?" + query);
    }

    private static int ParseTimeout(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? Math.Clamp(parsed, 10, 120) : 30;

    private static byte[] RandomNumberGeneratorBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string? GetString(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>()?.Trim(),
            JsonValueKind.Number => node.ToJsonString(),
            _ => null
        };
    }

    private static string? FirstString(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(obj, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetInt(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<int>();
            }

            return int.TryParse(node.GetValue<string>(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? GetLong(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<long>();
            }

            return long.TryParse(node.GetValue<string>(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }
}
