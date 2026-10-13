using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>红果短剧下载器 2.3.7 的搜索、剧集目录和视频解析。</summary>
public static class PlayletDramaClient
{
    public const string BookPrefix = "playlet:";
    public const string EpisodePrefix = "playlet_ep:";
    public const string DefaultServerUrl = "https://startvlog.cn/start-prod-api";
    public const string ClientVersion = "2.3.7";
    public const string DefaultLine = "1";
    private const string DirectoryUrl =
        "https://api-sinfonlinec.fanqiesdk.com/api/novel/book/audio/directory/list/v1";
    private const string VideoPath = "/api/drama/hongguo/video";
    private const string DecryptVideoPath = "/api/drama/hongguo/decryptVideo";
    private const string DetailPath = "/api/drama/hongguo/detail";
    private const string PassId = "start-prod-api";
    private const string PassToken = "MkYQyRrrD2iG5WuDEV7DjYcq2jq7";
    private const string DirectoryUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0.60(0x18003c2c) NetType/WIFI Language/zh_CN";
    private const string ComicNewUrl =
        "https://api5-normal-sinfonlinec.fqnovel.com/reading/user/subscribe/list/v1/";
    private const string ComicNewUserAgent =
        "com.dragon.read/73532 (Linux; U; Android 12; zh_CN; V2366GA; Build/V417IR;tt-ok/3.12.13.17)";
    private const string ComicNewDeviceId = "3893822780890025";
    private const string ComicNewInstallId = "3893822780894121";
    private const string ComicNewClientId = "164b57db-6e60-4f3a-90ff-d3472e83f63d";
    private const string RankUrl =
        "https://api5-normal-sinfonlineb.fqnovel.com/reading/bookapi/bookmall/cell/change/v1/";
    private const string RankUserAgent =
        "com.dragon.read/72732 (Linux; U; Android 12; zh_CN; 22041216C; Build/V417IR;tt-ok/3.12.13.17)";
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC/EwSZCZTwnYhixLefB9Gvfa+X
        o4uMnG35UiNdPd20/CpgMjw0a9Zy79WjvMH4oCRCOL81HMy5/o6Iuks5Nj4t0reN
        KMHkDcrZdIgMW+DFaioJWEi4zfORC0amtHuDEMYaxfVQ1PxOfgnApbD+/3qzd4hr
        4AzoGhyxwpyUXtX6wQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private sealed record ComicNewPage(
        IReadOnlyList<DramaSearchItem> Items,
        bool HasMore,
        int NextOffset,
        string SessionId);

    public sealed record EpisodeItem(int Number, string Title, string VideoId);
    public sealed record VideoItem(string Url, string? Key);
    public sealed record ConnectivityResult(bool SearchOk, bool VideoOk, string SearchMessage, string VideoMessage);

    public static IReadOnlyList<string> BuildQualityFallbackCodes(string? quality)
    {
        var requested = MapQuality(quality);
        return requested switch
        {
            "0" => ["0", "1080", "1"],
            "1" => ["1", "1080", "2", "720"],
            "2" => ["2", "720", "1"],
            "3" => ["3", "2", "720"],
            "4" => ["4", "2", "720"],
            "5" => ["5", "4", "2"],
            _ => [requested, "1", "1080"]
        };
    }

    public static async Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(
        HttpClient httpClient,
        string? serverUrl,
        string bookId,
        CancellationToken cancellationToken)
    {
        var normalizedBookId = StripBookPrefix(bookId);
        if (string.IsNullOrWhiteSpace(normalizedBookId))
        {
            return [];
        }

        try
        {
            var directory = await GetDirectoryEpisodesAsync(httpClient, normalizedBookId, cancellationToken);
            if (directory.Count > 0)
            {
                return directory;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // 目录接口失败时改走皮卡丘 detail。
        }

        return await GetDetailEpisodesAsync(httpClient, serverUrl, normalizedBookId, cancellationToken);
    }

    public static async Task<VideoItem> GetVideoAsync(
        HttpClient httpClient,
        string? serverUrl,
        string videoId,
        string? quality,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var normalizedVideoId = StripEpisodePrefix(videoId);
        var normalizedDeviceId = (deviceId ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedVideoId))
        {
            throw new InvalidOperationException("红果短剧下载器缺少视频编号。");
        }

        if (string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            throw new InvalidOperationException("未配置红果短剧下载器设备号。请在下载器「软件设置」中复制设备信息后保存。");
        }

        var server = NormalizeServerUrl(serverUrl);
        string? lastMessage = null;
        foreach (var qualityCode in BuildQualityFallbackCodes(quality))
        {
            var video = await RequestVideoAsync(
                httpClient,
                server,
                VideoPath,
                normalizedVideoId,
                qualityCode,
                normalizedDeviceId,
                cancellationToken);
            if (video.Success)
            {
                return new VideoItem(video.Url!, video.Key);
            }

            lastMessage = video.Message;
        }

        var fallback = await RequestVideoAsync(
            httpClient,
            server,
            DecryptVideoPath,
            normalizedVideoId,
            BuildQualityFallbackCodes(quality)[0],
            normalizedDeviceId,
            cancellationToken);
        if (fallback.Success)
        {
            return new VideoItem(fallback.Url!, fallback.Key);
        }

        throw new InvalidOperationException(
            "红果短剧下载器视频解析失败：" +
            (string.IsNullOrWhiteSpace(fallback.Message) ? lastMessage ?? "未知错误" : fallback.Message));
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetComicNewTodayAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var todayDate = ChinaToday();
        var today = todayDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var publishDate = todayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var items = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var sessionId = "";
        for (var page = 0; page < 8; page++)
        {
            var batch = await FetchComicNewPageAsync(httpClient, today, publishDate, offset, sessionId, cancellationToken);
            foreach (var item in batch.Items)
            {
                if (seen.Add(item.BookId))
                    items.Add(item);
            }

            if (!string.IsNullOrWhiteSpace(batch.SessionId))
                sessionId = batch.SessionId;
            if (!batch.HasMore || batch.NextOffset <= offset || batch.Items.Count == 0)
                break;
            offset = batch.NextOffset;
        }

        return items;
    }

    public static async Task<HongguoRankPage> GetRankPageAsync(
        HttpClient httpClient,
        string? category,
        string? board,
        int offset,
        CancellationToken cancellationToken)
    {
        if (!HongguoRankCatalog.TryResolve(category, board, out var query))
        {
            throw new InvalidOperationException(HongguoRankCatalog.UnavailableReason(category, board) ?? "未知榜单");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRankUrl(query, Math.Max(0, offset)));
        request.Headers.TryAddWithoutValidation("User-Agent", RankUserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"红果短剧下载器榜单请求失败: HTTP {(int)response.StatusCode}");
        }

        var payload = JsonNode.Parse(text);
        var code = payload?["code"]?.ToString();
        if (!string.Equals(code, "0", StringComparison.Ordinal))
        {
            var message = payload?["message"]?.ToString() ?? payload?["msg"]?.ToString() ?? "未知错误";
            throw new InvalidOperationException($"红果短剧下载器榜单失败: {message}");
        }

        var page = HongguoHighCalendarMapper.ReadRankPage(payload);
        var items = page.Items
            .Select(item => item with { BookId = BookPrefix + HongguoHighCrypto.StripBookPrefix(item.BookId) })
            .ToArray();
        return new HongguoRankPage(items, page.HasMore, page.NextOffset);
    }

    public static async Task<ConnectivityResult> TestConnectivityAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? dramaType,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        string searchMessage;
        var searchOk = false;
        try
        {
            using var document = await PikachuDramaClient.RequestFanqieSearchAsync(
                httpClient,
                fanqieCookie: "",
                string.Equals(dramaType, "manga", StringComparison.OrdinalIgnoreCase) ? "manga" : "short",
                "财神",
                1,
                cancellationToken,
                allowMissingCookie: true);
            searchOk = true;
            searchMessage = "搜索正常";
        }
        catch (Exception ex)
        {
            searchMessage = $"搜索失败：{ex.Message}";
        }

        string videoMessage;
        var videoOk = false;
        try
        {
            var episodes = await GetEpisodesAsync(httpClient, serverUrl, "7599558182226119705", cancellationToken);
            videoMessage = $"剧集目录正常，测试剧返回 {episodes.Count} 集";
            if (episodes.Count > 0 && !string.IsNullOrWhiteSpace(deviceId))
            {
                var video = await GetVideoAsync(
                    httpClient,
                    serverUrl,
                    episodes[0].VideoId,
                    "1080P",
                    deviceId,
                    cancellationToken);
                videoOk = !string.IsNullOrWhiteSpace(video.Url);
                videoMessage += videoOk ? "；视频直链正常" : "；视频未返回地址";
            }
            else if (string.IsNullOrWhiteSpace(deviceId))
            {
                videoMessage += "；未配置设备号，未测试视频";
            }
        }
        catch (Exception ex)
        {
            videoMessage = $"视频解析失败：{ex.Message}";
        }

        return new ConnectivityResult(searchOk, videoOk, searchMessage, videoMessage);
    }

    private static async Task<IReadOnlyList<EpisodeItem>> GetDirectoryEpisodesAsync(
        HttpClient httpClient,
        string bookId,
        CancellationToken cancellationToken)
    {
        var url = $"{DirectoryUrl}?book_id={Uri.EscapeDataString(bookId)}&aid=1967";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DirectoryUserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"剧集目录 HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(text);
        if (GetInt(document.RootElement, "code") != 0 ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("item_list", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var episodes = new List<EpisodeItem>();
        foreach (var item in items.EnumerateArray())
        {
            var videoId = ReadVideoId(item);
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var number = episodes.Count + 1;
            episodes.Add(new EpisodeItem(number, $"第{number}集", videoId));
        }

        return episodes;
    }

    private static async Task<IReadOnlyList<EpisodeItem>> GetDetailEpisodesAsync(
        HttpClient httpClient,
        string? serverUrl,
        string bookId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bookId"] = Encrypt(bookId)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeServerUrl(serverUrl)}{DetailPath}")
        {
            Content = content
        };
        ApplyGatewayHeaders(request, deviceId: null);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!string.Equals(ReadCode(document.RootElement), "200", StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var episodeList) ||
            episodeList.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var episodes = new List<EpisodeItem>();
        foreach (var item in episodeList.EnumerateArray())
        {
            var videoId = GetString(item, "videoId") ?? GetString(item, "video_id");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var number = episodes.Count + 1;
            episodes.Add(new EpisodeItem(number, GetString(item, "title") ?? $"第{number}集", videoId));
        }

        return episodes;
    }

    private static async Task<VideoAttempt> RequestVideoAsync(
        HttpClient httpClient,
        string serverUrl,
        string path,
        string videoId,
        string qualityCode,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["videoId"] = Encrypt(videoId),
            ["quality"] = Encrypt(qualityCode),
            ["deviceId"] = Encrypt(deviceId),
            ["version"] = Encrypt(ClientVersion),
            ["line"] = Encrypt(DefaultLine)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}{path}")
        {
            Content = content
        };
        ApplyGatewayHeaders(request, deviceId);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new VideoAttempt(false, null, null, $"HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(text);
        var code = ReadCode(document.RootElement);
        if (!string.Equals(code, "200", StringComparison.Ordinal))
        {
            return new VideoAttempt(false, null, null, ReadMessage(document.RootElement) ?? $"code={code}");
        }

        var url = document.RootElement.TryGetProperty("data", out var data) ? GetString(data, "url") : null;
        var key = document.RootElement.TryGetProperty("data", out data) ? GetString(data, "key") : null;
        return string.IsNullOrWhiteSpace(url)
            ? new VideoAttempt(false, null, null, "未返回播放地址")
            : new VideoAttempt(true, url, key, null);
    }

    private static void ApplyGatewayHeaders(HttpRequestMessage request, string? deviceId)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{today}_{PassId}_{PassToken}")));
        request.Headers.TryAddWithoutValidation("auth-pass-id", PassId);
        request.Headers.TryAddWithoutValidation("auth-signature", signature);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("memberType", "drama_cn");
        request.Headers.TryAddWithoutValidation("version", ClientVersion);
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            request.Headers.TryAddWithoutValidation("deviceId", deviceId.Trim());
        }
    }

    private static string MapQuality(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "1080p+" or "1080+" => "0",
            "720p" or "720" or "高清" or "hd" => "2",
            "540p" or "540" => "3",
            "480p" or "480" or "标清" or "sd" => "4",
            "360p" or "360" => "5",
            "0" or "1" or "2" or "3" or "4" or "5" => normalized,
            _ => "1"
        };
    }

    private static async Task<ComicNewPage> FetchComicNewPageAsync(
        HttpClient httpClient,
        string today,
        string publishDate,
        int offset,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildComicNewUrl(today, offset, sessionId));
        request.Headers.TryAddWithoutValidation("User-Agent", ComicNewUserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"红果短剧下载器漫剧上新请求失败: HTTP {(int)response.StatusCode}");

        var payload = JsonNode.Parse(text);
        var code = payload?["code"]?.ToString();
        if (!string.Equals(code, "0", StringComparison.Ordinal))
        {
            var message = payload?["message"]?.ToString() ?? payload?["msg"]?.ToString() ?? "未知错误";
            throw new InvalidOperationException($"红果短剧下载器漫剧上新失败: {message}");
        }

        var data = payload?["data"] as JsonObject;
        var items = ReadSubscribeItems(data, publishDate);
        var hasMore = ReadBool(data?["has_more"]);
        var nextOffset = ReadInt(data?["next_offset"]);
        if (items.Count == 0)
            hasMore = false;
        return new ComicNewPage(items, hasMore, nextOffset, ReadString(data?["session_id"]));
    }

    private static string BuildComicNewUrl(string today, int offset, string sessionId)
    {
        var parameters = new Dictionary<string, string>
        {
            ["bdhm_bid"] = "novelread_lynx",
            ["single_tab"] = "0",
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["need_calendar_schema"] = "true",
            ["active_panel"] = "6",
            ["panels"] = "",
            ["session_id"] = sessionId,
            ["version"] = "0",
            ["gender_type"] = "2",
            ["swipe_type"] = "3",
            ["bdhm_pid"] = "subscribe-list",
            ["tab_type"] = "5",
            ["subscribe_list_opt"] = "0",
            ["new_series_opt"] = "0",
            ["tab_style"] = "2",
            ["device_platform"] = "android",
            ["iid"] = ComicNewInstallId,
            ["device_id"] = ComicNewDeviceId,
            ["ac"] = "wifi",
            ["channel"] = "vivo_8662_64",
            ["aid"] = "8662",
            ["app_name"] = "novelread",
            ["version_code"] = "73532",
            ["version_name"] = "7.3.5.32",
            ["os"] = "android",
            ["ssmix"] = "a",
            ["device_type"] = "V2366GA",
            ["device_brand"] = "vivo",
            ["language"] = "zh",
            ["os_api"] = "32",
            ["os_version"] = "12",
            ["manifest_version_code"] = "73532",
            ["resolution"] = "1440*2560",
            ["dpi"] = "640",
            ["update_version_code"] = "73532",
            ["host_abi"] = "arm64-v8a",
            ["dragon_device_type"] = "phone",
            ["pv_player"] = "73532",
            ["compliance_status"] = "0",
            ["need_personal_recommend"] = "1",
            ["player_so_load"] = "1",
            ["is_android_pad_screen"] = "0",
            ["rom_version"] = "V417IR+release-keys",
            ["cdid"] = ComicNewClientId,
            ["target_date"] = today,
            ["select_items"] = """{"genre":["comic_series"]}""",
        };
        return ComicNewUrl + "?" + string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static IReadOnlyList<DramaSearchItem> ReadSubscribeItems(JsonObject? data, string publishDate)
    {
        if (data?["subscribe_items"] is not JsonArray list)
            return [];

        var items = new List<DramaSearchItem>();
        foreach (var node in list)
        {
            if (node is not JsonObject item)
                continue;
            var source = item["subscribe_data"] as JsonObject ?? item["book_info"] as JsonObject ?? item;
            var mapped = HongguoHighCalendarMapper.TryMapItem(source);
            if (mapped is null && item["video_data"] is not null)
            {
                mapped = HongguoHighCalendarMapper.ExtractSearchTabItems(item)
                    .Select(HongguoHighCalendarMapper.TryMapItem)
                    .FirstOrDefault(candidate => candidate is not null);
            }

            if (mapped is null || string.IsNullOrWhiteSpace(mapped.BookId))
                continue;
            var intro = string.IsNullOrWhiteSpace(mapped.Intro) ? ReadSynopsis(source) : mapped.Intro;
            items.Add(mapped with
            {
                BookId = BookPrefix + HongguoHighCrypto.StripBookPrefix(mapped.BookId),
                Intro = intro,
                PublishTime = publishDate,
            });
        }

        return items;
    }

    private static string ReadSynopsis(JsonObject source)
    {
        var direct = ReadJsonString(source, "video_desc")
            ?? ReadJsonString(source, "series_intro")
            ?? ReadJsonString(source, "abstract");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        if (source["video_detail"] is JsonObject detail)
        {
            return ReadJsonString(detail, "series_intro")
                ?? ReadJsonString(detail, "video_desc")
                ?? "";
        }

        return "";
    }

    private static string? ReadJsonString(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static DateOnly ChinaToday()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }

    private static string ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return "";
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value)
            return false;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetValue<int>(out var number) && number != 0,
            JsonValueKind.String => value.GetValue<string>() is "1" or "true",
            _ => false
        };
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number))
            return number;
        return value.GetValueKind() == JsonValueKind.String &&
               int.TryParse(value.GetValue<string>(), out var parsed)
            ? parsed
            : 0;
    }

    private static string BuildRankUrl(HongguoRankQuery query, int offset)
    {
        var parameters = new Dictionary<string, string>
        {
            ["aid"] = "8662",
            ["app_name"] = "novelread",
            ["version_code"] = "72732",
            ["version_name"] = "7.2.7.32",
            ["device_platform"] = "android",
            ["os"] = "android",
            ["os_version"] = "12",
            ["os_api"] = "32",
            ["manifest_version_code"] = "72732",
            ["update_version_code"] = "72732",
            ["channel"] = "huawei_8662_64",
            ["language"] = "zh",
            ["resolution"] = "720*1280",
            ["dpi"] = "320",
            ["ac"] = "wifi",
            ["ssmix"] = "a",
            ["device_type"] = "22041216C",
            ["device_brand"] = "Redmi",
            ["cell_id"] = HongguoRankCatalog.CellId,
            ["tab_type"] = HongguoRankCatalog.TabType,
            ["client_req_type"] = "2",
            ["client_template"] = "2",
            ["screen_width_px"] = "1440",
            ["selected_items"] = query.SelectedItems,
            ["sub_selected_items"] = query.SubSelectedItems,
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["gender"] = "2",
        };
        var queryText = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return RankUrl + "?" + queryText;
    }

    private static string NormalizeServerUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultServerUrl : normalized;
    }

    private static string StripBookPrefix(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(BookPrefix, StringComparison.OrdinalIgnoreCase)
            ? text[BookPrefix.Length..]
            : text;
    }

    private static string StripEpisodePrefix(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(EpisodePrefix, StringComparison.OrdinalIgnoreCase)
            ? text[EpisodePrefix.Length..]
            : text;
    }

    private static string? ReadVideoId(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString()?.Trim(),
        JsonValueKind.Number => item.GetRawText(),
        JsonValueKind.Object => GetString(item, "item_id") ?? GetString(item, "video_id") ?? GetString(item, "videoId"),
        _ => null
    };

    private static string Encrypt(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKey);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.Pkcs1));
    }

    private static string? ReadCode(JsonElement element)
    {
        if (!element.TryGetProperty("code", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? ReadMessage(JsonElement element)
    {
        foreach (var name in new[] { "msg", "message", "error" })
        {
            var value = GetString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
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

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record VideoAttempt(bool Success, string? Url, string? Key, string? Message);
}
