using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果 APP 下载的搜索、剧集和播放地址。
/// 搜索、剧集和播放优先用本机模拟器 7.4.3.32 的已注册设备，按应用自己的精简查询参数签名。
/// 模拟器身份不可用时，搜索和剧集回退 audit 签名，播放再按勾选走链路 1 和链路 2。
/// 已有模拟器设备时，播放空包只在这条链路上退避重试，不再注册新设备。
/// </summary>
internal sealed class HongguoAppDirectClient
{
    internal const string PlaybackHost = "https://api5-normal-sinfonlineb.fqnovel.com";
    internal const string ReadingHost = "https://reading.snssdk.com";
    private const string SearchPath = "/reading/bookapi/search/tab/v";
    private const string EpisodePath = "/novel/player/multi_video_detail/preload/v1";
    private const string PlaybackPath = "/novel/player/multi_video_model/preload/v1/";
    private const string EmulatorUserAgent =
        "com.phoenix.read/74332 (Linux; U; Android 13; zh_CN; sdk_gphone64_arm64; Build/TE1A.220922.034)";

    private static readonly (string Key, string Value)[] EmulatorQuery =
    [
        ("device_platform", "android"),
        ("channel", "update_64"),
        ("aid", "8662"),
        ("app_name", "novelread"),
        ("version_code", "74332"),
        ("version_name", "7.4.3.32"),
        ("manifest_version_code", "74332"),
        ("update_version_code", "74332"),
        ("resolution", "1080*2337"),
        ("dpi", "420"),
        ("device_type", "sdk_gphone64_arm64"),
        ("device_brand", "google"),
        ("os_api", "33"),
        ("os_version", "13"),
        ("ac", "wifi"),
        ("pv_player", "74332"),
    ];

    internal static int EmulatorChainUses;

    private readonly HttpClient _httpClient;
    private readonly HongguoGuestPlayback _playback;
    private readonly HongguoSignedPlayback _signed;

    internal HongguoAppDirectClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _playback = new HongguoGuestPlayback(httpClient);
        _signed = new HongguoSignedPlayback(_playback);
    }

    internal async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken,
        string? searchCategory = null,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        var trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
            return [];
        if (IsBookIdQuery(trimmed, searchCategory))
        {
            return page > 1
                ? []
                : [new DramaSearchItem(trimmed, trimmed, "", 0, "", "")];
        }

        var offset = Math.Max(0, (Math.Max(1, page) - 1) * 10);
        var extra = new KeyValuePair<string, string>[]
        {
            new("query", trimmed),
            new("tab_name", "feed"),
            new("search_source", "1"),
            new("offset", offset.ToString()),
            new("count", "10"),
            new("use_correct", "true"),
        };
        JsonNode? root = null;
        Exception? emulatorError = null;
        if (TryResolveIdentity(deviceId, installId, out var did, out var iid))
        {
            try
            {
                root = await SendEmulatorAsync(
                    HttpMethod.Get, PlaybackHost, SearchPath, null, extra, did, iid, cancellationToken);
                EmulatorChainUses++;
                Remember(deviceId, installId, did, iid, persist);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                emulatorError = ex;
            }
        }

        if (root is null)
        {
            try
            {
                root = await SendAsync(HttpMethod.Get, SearchPath, null, extra, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = emulatorError is null ? "红果搜索失败" : $"红果搜索失败：{emulatorError.Message}";
                throw new HongguoHighException(detail, 0, ex);
            }
        }

        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in SearchCells(root))
        {
            foreach (var item in MapCell(cell))
            {
                if (item.BookId.Length > 0 && seen.Add(item.BookId))
                    results.Add(item);
            }
        }
        return results;
    }

    internal async Task<IReadOnlyList<HongguoNewApiService.HongguoEpisodeInfo>> GetEpisodesAsync(
        string bookId,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        var seriesId = RawSeriesId(bookId);
        if (seriesId.Length == 0)
            return [];

        var body = HongguoPureSign.EpisodesBody(seriesId);
        if (TryResolveIdentity(deviceId, installId, out var did, out var iid))
        {
            var episodes = await ReadEpisodesAttempts(
                () => SendEmulatorAsync(HttpMethod.Post, ReadingHost, EpisodePath, body, null, did, iid, cancellationToken, attempts: 1),
                seriesId,
                cancellationToken);
            if (episodes.Count > 0)
            {
                EmulatorChainUses++;
                Remember(deviceId, installId, did, iid, persist);
                return episodes;
            }
        }

        var audit = await ReadEpisodesAttempts(
            () => SendAsync(HttpMethod.Post, EpisodePath, body, null, cancellationToken, attempts: 1),
            seriesId,
            cancellationToken);
        if (audit.Count > 0)
            return audit;
        throw new HongguoHighException("剧集列表获取失败");
    }

    internal async Task<HongguoHighVideoPlayback> GetVideoPlaybackAsync(
        string videoId,
        string quality,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null,
        bool streamLink1 = true,
        bool streamLink2 = true)
    {
        if (!HongguoHighCrypto.TryDecodeEpisodeId(videoId, out var seriesId, out _, out var rawVideoId))
            throw new HongguoHighException("剧集标识无效");
        if (!streamLink1 && !streamLink2)
        {
            streamLink1 = true;
            streamLink2 = true;
        }

        if (TryResolveIdentity(deviceId, installId, out var did, out var iid))
        {
            try
            {
                var playback = await GetEmulatorPlaybackAsync(rawVideoId, quality, did, iid, cancellationToken);
                EmulatorChainUses++;
                Remember(deviceId, installId, did, iid, persist);
                return playback;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HongguoHighException("模拟器链路失败：" + ex.Message, 0, ex);
            }
        }

        try
        {
            return await GetAuditVideoModelAsync(seriesId, rawVideoId, quality, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (streamLink1 || streamLink2)
        {
            // 单集接口失败时再试原来的两条播放链路。
        }

        Exception? first = null;
        if (streamLink1)
        {
            try
            {
                return await _playback.GetAsync(rawVideoId, quality, cancellationToken, deviceId, installId, persist);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (streamLink2)
            {
                first = ex;
            }
        }

        try
        {
            return await _signed.GetAsync(rawVideoId, quality, cancellationToken, deviceId, installId, persist);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (first is not null)
        {
            throw new HongguoHighException($"链路 1 失败：{first.Message}；链路 2 失败：{ex.Message}", 0, ex);
        }
    }

    internal async Task<HongguoAppReleasePage> GetNewReleasePageAsync(
        string genre,
        int offset,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var safeOffset = Math.Max(0, offset);
        var root = await SendAsync(
            HttpMethod.Post,
            LandpagePath,
            LandpageBody(genre, safeOffset, sessionId),
            null,
            cancellationToken,
            attempts: 8);
        var items = ReadLandpageItems(root);
        var paging = ReadLandpagePaging(root, safeOffset, items.Count);
        return new HongguoAppReleasePage(items, paging.HasMore, paging.NextOffset, paging.SessionId);
    }

    internal const string LandpagePath = "/reading/distribution/category/landpage/v";
    internal const int NewReleasePageSize = 20;

    internal static byte[] LandpageBody(string genre, int offset, string? sessionId = null)
    {
        if (genre is not ("comic_series" or "ai_series"))
            throw new ArgumentOutOfRangeException(nameof(genre));
        var safeOffset = Math.Max(0, offset);
        var session = new string((sessionId ?? "").Where(char.IsLetterOrDigit).ToArray());
        var json = "{\"client_req_type\":3,\"filter_ids\":\"\",\"limit\":20,"
                   + "\"need_selector_panel\":false,\"offset\":" + safeOffset
                   + ",\"req_scene\":\"comic_series\",\"req_type\":\"only_content\",\"select_items\":{"
                   + "\"category_dim_epoch\":[],\"category_dim_role\":[],\"category_dim_theme\":[],"
                   + "\"gender\":[],\"genre\":[\"" + genre + "\"],\"online_time\":[\"days_7\"],"
                   + "\"sort\":[\"online_time\"]},\"session_id\":\"" + session + "\"}";
        return Encoding.UTF8.GetBytes(json);
    }

    internal static IReadOnlyList<DramaSearchItem> ReadLandpageItems(JsonNode root)
    {
        var mapped = HongguoHighCalendarMapper.MapPayload(root);
        if (mapped.Count > 0)
            return mapped;
        return HongguoHighCalendarMapper.ExtractLandpageItems(root)
            .Select(HongguoHighCalendarMapper.TryMapItem)
            .OfType<DramaSearchItem>()
            .ToArray();
    }

    internal static bool LandpageHasMore(JsonNode? root, int itemCount) =>
        ReadLandpagePaging(root, 0, itemCount).HasMore;

    internal readonly record struct LandpageCursor(bool HasMore, int NextOffset, string SessionId);

    internal static LandpageCursor ReadLandpagePaging(JsonNode? root, int offset, int itemCount)
    {
        var data = root?["data"] as JsonObject;
        var hasMore = itemCount > 0 && (data is not null && data.ContainsKey("has_more")
            ? ReadBool(data["has_more"])
            : itemCount >= NewReleasePageSize);
        var next = Positive(data, "next_offset");
        if (next <= offset)
            next = offset + Math.Max(itemCount, 0);
        var session = Text(data, "session_id") ?? "";
        return new LandpageCursor(hasMore, next, session);
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value)
            return false;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetValue<int>(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetValue<string>(), out var flag) && flag
                || string.Equals(value.GetValue<string>(), "1", StringComparison.Ordinal),
            _ => false
        };
    }

    private async Task<HongguoHighVideoPlayback> GetAuditVideoModelAsync(
        string seriesId,
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        var root = await SendAsync(
            HttpMethod.Post,
            "/novel/player/video_model/v1/",
            VideoModelBody(seriesId, videoId),
            null,
            cancellationToken);
        var model = HongguoGuestPlayback.ModelOf(root["data"]?["video_model"]);
        if (model is null)
            throw new HongguoHighException("播放地址为空");
        return HongguoGuestPlayback.Pick(model, quality);
    }

    private static byte[] VideoModelBody(string seriesId, string videoId)
    {
        var body = new JsonObject
        {
            ["biz_param"] = new JsonObject
            {
                ["detail_page_version"] = 0,
                ["device_level"] = 2,
                ["need_all_video_definition"] = true,
                ["need_mp4_align"] = false,
                ["use_os_player"] = false,
                ["video_platform"] = 1024,
                ["video_id"] = videoId,
            },
            ["series_id"] = seriesId,
            ["video_id"] = videoId,
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString(HongguoGuestPlayback.CompactJson));
    }

    internal async Task<HongguoGuestPlayback.TrackProbe> ProbePlaybackAsync(
        string videoId,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        if (TryResolveIdentity(deviceId, installId, out var did, out var iid))
        {
            try
            {
                var model = await FetchEmulatorModelAsync(videoId, did, iid, cancellationToken);
                EmulatorChainUses++;
                Remember(deviceId, installId, did, iid, persist);
                return HongguoGuestPlayback.DescribeTracks(model);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HongguoHighException("模拟器播放探测失败", 0, ex);
            }
        }

        return await _playback.ProbeAsync(videoId, cancellationToken, deviceId, installId, persist);
    }

    private async Task<List<HongguoNewApiService.HongguoEpisodeInfo>> ReadEpisodesAttempts(
        Func<Task<JsonNode>> send,
        string seriesId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var episodes = ReadEpisodes(await send(), seriesId);
                if (episodes.Count > 0)
                    return episodes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 空包或签名软失败时换票据再试。
            }

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return [];
    }

    private async Task<HongguoHighVideoPlayback> GetEmulatorPlaybackAsync(
        string videoId,
        string quality,
        string deviceId,
        string installId,
        CancellationToken cancellationToken)
    {
        var model = await FetchEmulatorModelAsync(videoId, deviceId, installId, cancellationToken);
        return HongguoGuestPlayback.Pick(model, quality);
    }

    private async Task<JsonObject> FetchEmulatorModelAsync(
        string videoId,
        string deviceId,
        string installId,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var root = await SendEmulatorAsync(
                    HttpMethod.Post,
                    PlaybackHost,
                    PlaybackPath,
                    EmulatorPlaybackBody(videoId),
                    null,
                    deviceId,
                    installId,
                    cancellationToken,
                    attempts: 1);
                return EmulatorModel(root, videoId) ?? throw new HongguoHighException("播放地址为空");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 7 || !IsTransientPlaybackFailure(ex))
                    break;
                await Task.Delay(EmulatorPlaybackRetryDelay(attempt), cancellationToken);
            }
        }

        throw last ?? new HongguoHighException("播放地址为空");
    }

    internal static bool IsTransientPlaybackFailure(Exception exception)
    {
        var message = exception.Message ?? "";
        return message.Contains("empty body", StringComparison.OrdinalIgnoreCase)
               || message.Contains("播放地址为空", StringComparison.Ordinal);
    }

    internal static TimeSpan EmulatorPlaybackRetryDelay(int attempt)
    {
        var shift = Math.Clamp(attempt, 0, 4);
        return TimeSpan.FromMilliseconds(400 * (1 << shift));
    }

    internal static byte[] EmulatorPlaybackBody(string videoId)
    {
        var body = new JsonObject
        {
            ["biz_param"] = new JsonObject
            {
                ["detail_page_version"] = 0,
                ["device_level"] = 2,
                ["disable_video_relate_book"] = false,
                ["need_all_video_definition"] = true,
                ["video_platform"] = 1024,
            },
            ["dr_scene"] = "preload",
            ["mixed_video_id_map"] = new JsonObject
            {
                ["1004"] = new JsonArray(JsonValue.Create(videoId)),
            },
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString(HongguoGuestPlayback.CompactJson));
    }

    internal static string BuildEmulatorUrl(
        string host,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? extra,
        string deviceId,
        string installId,
        long rticket)
    {
        var parts = new List<string>();
        foreach (var (key, value) in EmulatorQuery)
            parts.Add(key + "=" + Uri.EscapeDataString(value));
        if (extra is not null)
        {
            foreach (var pair in extra)
                parts.Add(pair.Key + "=" + Uri.EscapeDataString(pair.Value));
        }

        parts.Add("device_id=" + Uri.EscapeDataString(deviceId));
        parts.Add("iid=" + Uri.EscapeDataString(installId));
        parts.Add("_rticket=" + rticket);
        return host + path + "?" + string.Join("&", parts);
    }

    private static bool TryResolveIdentity(string? deviceId, string? installId, out string did, out string iid)
    {
        if (HongguoAppEmulatorIdentity.TryRead(out did, out iid))
            return true;
        if (HongguoGuestPlayback.IsSavedIdentity(deviceId, installId))
        {
            did = deviceId!.Trim();
            iid = installId!.Trim();
            return true;
        }

        did = "";
        iid = "";
        return false;
    }

    private static void Remember(
        string? givenDevice,
        string? givenInstall,
        string deviceId,
        string installId,
        Action<string, string>? persist)
    {
        if (persist is null)
            return;
        if (string.Equals(givenDevice?.Trim(), deviceId, StringComparison.Ordinal) &&
            string.Equals(givenInstall?.Trim(), installId, StringComparison.Ordinal))
            return;
        persist(deviceId, installId);
    }

    private static JsonObject? EmulatorModel(JsonNode root, string videoId)
    {
        if (root["data"] is not JsonObject data)
            return null;
        if (data[videoId] is JsonObject entry)
        {
            var direct = HongguoGuestPlayback.ModelOf(entry["video_model"]);
            if (direct is not null)
                return direct;
        }

        foreach (var pair in data)
        {
            if (pair.Value is not JsonObject any)
                continue;
            var model = HongguoGuestPlayback.ModelOf(any["video_model"]);
            if (model is not null)
                return model;
        }

        return HongguoGuestPlayback.ModelOf(data["video_model"]);
    }

    private async Task<JsonNode> SendEmulatorAsync(
        HttpMethod method,
        string host,
        string path,
        byte[]? body,
        IReadOnlyList<KeyValuePair<string, string>>? extra,
        string deviceId,
        string installId,
        CancellationToken cancellationToken,
        int attempts = 3)
    {
        Exception? last = null;
        var rticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxAttempts = Math.Clamp(attempts, 1, 8);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var khronos = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = BuildEmulatorUrl(host, path, extra, deviceId, installId, rticket);
            for (var guard = 0; HongguoPureSign.BranchOf(url, body, khronos) == 1 && guard < 32; guard++)
            {
                rticket++;
                url = BuildEmulatorUrl(host, path, extra, deviceId, installId, rticket);
            }

            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("user-agent", EmulatorUserAgent);
            request.Headers.TryAddWithoutValidation("passport-sdk-version", "5051452");
            request.Headers.TryAddWithoutValidation("sdk-version", "2");
            if (body is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                request.Headers.TryAddWithoutValidation("x-ss-stub", Convert.ToHexString(MD5.HashData(body)));
            }

            foreach (var header in HongguoPureSign.Sign(url, body, khronos))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
                    throw new InvalidOperationException("empty body");
                var node = JsonNode.Parse(bytes) ?? throw new InvalidOperationException("empty body");
                if (node is JsonObject obj && CodeOf(obj) is not (0 or null))
                    throw new InvalidOperationException("rejected");
                return node;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                rticket++;
                if (attempt < maxAttempts - 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("红果请求失败");
    }

    private async Task<JsonNode> SendAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        IReadOnlyList<KeyValuePair<string, string>>? extra,
        CancellationToken cancellationToken,
        int attempts = 3)
    {
        var deviceId = HongguoPureSign.LoadDeviceId(HongguoPureSign.DefaultDevicePath());
        Exception? last = null;
        var rticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxAttempts = Math.Clamp(attempts, 1, 8);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var khronos = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var built = HongguoPureSign.BuildUrl(path, extra, deviceId, rticket);
            var url = built.Url;
            rticket = built.Rticket;
            for (var guard = 0; HongguoPureSign.BranchOf(url, body, khronos) == 1 && guard < 32; guard++)
            {
                rticket++;
                built = HongguoPureSign.BuildUrl(path, extra, deviceId, rticket);
                url = built.Url;
                rticket = built.Rticket;
            }

            using var request = new HttpRequestMessage(method, url);
            foreach (var header in HongguoPureSign.SessionHeaders())
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                request.Headers.TryAddWithoutValidation("x-ss-stub", Convert.ToHexString(MD5.HashData(body)));
            }
            else
            {
                request.Headers.TryAddWithoutValidation("content-type", "application/json; charset=utf-8");
            }

            foreach (var header in HongguoPureSign.Sign(url, body, khronos))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
                    throw new InvalidOperationException("empty body");
                var node = JsonNode.Parse(bytes) ?? throw new InvalidOperationException("empty body");
                if (node is JsonObject obj && CodeOf(obj) is not (0 or null))
                    throw new InvalidOperationException("rejected");
                return node;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                rticket++;
                if (attempt < maxAttempts - 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("红果请求失败");
    }

    private static IEnumerable<DramaSearchItem> MapCell(JsonObject cell)
    {
        var mapped = HongguoHighCalendarMapper.ExtractSearchTabItems(cell)
            .Select(HongguoHighCalendarMapper.TryMapItem)
            .OfType<DramaSearchItem>()
            .ToArray();
        if (mapped.Length > 0)
        {
            foreach (var item in mapped)
                yield return item;
            yield break;
        }

        var bookId = Text(cell, "book_id") ?? Text(cell, "search_result_id");
        if (string.IsNullOrWhiteSpace(bookId))
            yield break;
        var video = FirstVideo(cell);
        var detail = cell["video_detail"] as JsonObject ?? video?["video_detail"] as JsonObject;
        var title = Text(cell["search_high_light"]?["title"] as JsonObject, "text")
                    ?? Text(detail, "series_title")
                    ?? Text(video, "title")
                    ?? bookId;
        title = StripTags(title);
        yield return new DramaSearchItem(
            HongguoHighCrypto.EnsureBookPrefix(bookId),
            title.Length == 0 ? bookId : title,
            "",
            Positive(detail, "episode_cnt") is var fromDetail and > 0
                ? fromDetail
                : Positive(video, "episode_cnt"),
            Text(detail, "series_intro") ?? "",
            Text(video, "cover") ?? Text(detail, "series_cover") ?? "");
    }

    private static IEnumerable<JsonObject> SearchCells(JsonNode root)
    {
        if (root["search_tabs"] is JsonArray tabs && tabs.FirstOrDefault() is JsonObject tab && tab["data"] is JsonArray data)
        {
            foreach (var cell in data.OfType<JsonObject>())
                yield return cell;
        }
    }

    private static List<HongguoNewApiService.HongguoEpisodeInfo> ReadEpisodes(JsonNode root, string seriesId)
    {
        var videoData = root["data"]?[seriesId]?["video_data"] as JsonObject;
        var list = videoData?["video_list"];
        var rows = new List<JsonObject>();
        if (list is JsonArray array)
            rows.AddRange(array.OfType<JsonObject>());
        else if (list is JsonObject obj)
            rows.AddRange(obj.Select(pair => pair.Value).OfType<JsonObject>());
        if (rows.Count == 0)
            return [];

        var episodes = new List<HongguoNewApiService.HongguoEpisodeInfo>();
        var fallback = 0;
        foreach (var row in rows.OrderBy(IndexOf))
        {
            var videoId = Text(row, "vid");
            if (string.IsNullOrWhiteSpace(videoId))
                continue;
            fallback++;
            var number = IndexOf(row);
            if (number <= 0)
                number = fallback;
            var title = Text(row, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = $"第{number}集";
            episodes.Add(new HongguoNewApiService.HongguoEpisodeInfo(
                number,
                title,
                HongguoHighCrypto.EncodeEpisodeId(seriesId, number, videoId),
                Text(row, "episode_cover") ?? Text(row, "cover") ?? ""));
        }
        return episodes;
    }

    private static int IndexOf(JsonObject row)
    {
        if (row["vid_index"] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static JsonObject? FirstVideo(JsonObject cell)
    {
        var video = cell["video_data"];
        if (video is JsonArray array)
            return array.OfType<JsonObject>().FirstOrDefault();
        return video as JsonObject;
    }

    private static bool IsBookIdQuery(string keyword, string? searchCategory)
    {
        var digits = keyword.All(char.IsDigit);
        if (string.Equals(searchCategory?.Trim(), DramaSearchCategory.BookId, StringComparison.Ordinal))
            return digits && keyword.Length > 0;
        return digits && keyword.Length is >= 10 and <= 20;
    }

    private static string RawSeriesId(string bookId)
    {
        var text = (bookId ?? "").Trim();
        var colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[(colon + 1)..];
        return HongguoHighCrypto.StripBookPrefix(text);
    }

    private static int? CodeOf(JsonObject obj)
    {
        var node = obj["code"] ?? obj["Code"];
        if (node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : -1;
    }

    private static int Positive(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return Math.Max(0, number);
        return int.TryParse(value.ToString(), out var parsed) ? Math.Max(0, parsed) : 0;
    }

    private static string? Text(JsonObject? obj, string name)
    {
        if (obj is null || obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? null : text;
        var raw = value.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string StripTags(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", "");
}
