using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoHighApiService
{
    private const int CalendarPageSize = 20;
    private const int CalendarMaxPages = 15;
    private const int CalendarPageConcurrency = 5;
    private const int CalendarEnrichConcurrency = 12;
    private const int LandpageMaxAttempts = 3;
    private const int PlaybackParseMaxAttempts = 3;
    private static readonly HashSet<int> LandpageRetryCodes = [408, 425, 429, 500, 502, 503, 504];
    private static readonly TimeSpan CalendarCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CalendarDetailCacheLifetime = TimeSpan.FromHours(12);
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
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly HongguoHighSession _session = new();
    private readonly ConcurrentDictionary<string, CalendarCacheEntry> _calendarCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CalendarDetailCacheEntry> _calendarDetailCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BatchParsePlan> _batchParsePlans = new(StringComparer.Ordinal);
    private HongguoHighDevice? _device;

    internal Func<DramaSourceSettings, string, JsonObject, int, CancellationToken, Task<JsonNode?>>? AuthedRequestForTests { get; set; }
    internal Func<JsonNode?, byte[], int, CancellationToken, Task<JsonNode?>>? ExecuteSignedRequestForTests { get; set; }
    internal Func<TimeSpan, CancellationToken, Task>? DelayForTests { get; set; }
    internal Func<DramaSourceSettings, CancellationToken, Task<JsonObject>>? LoginForTests { get; set; }

    private sealed record CalendarCacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<DramaSearchItem> Items);
    private sealed record CalendarDetailCacheEntry(DateTimeOffset CreatedAt, JsonObject BookInfo);
    private sealed record BatchEpisode(string VideoId, int EpisodeNumber);
    private sealed class BatchParsePlan
    {
        public Dictionary<string, Lazy<Task<IReadOnlyDictionary<string, HongguoNewApiService.HongguoVideoPlayback>>>> GroupsByVideoId { get; } = new(StringComparer.Ordinal);
    }

    public HongguoHighApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    internal IDisposable RegisterBatchParsePlan(
        DramaSourceSettings settings,
        IReadOnlyList<string> encodedVideoIds,
        string quality,
        int batchSize)
    {
        var episodes = encodedVideoIds
            .Select(id => HongguoHighCrypto.TryDecodeEpisodeId(id, out var bookId, out var number, out var videoId)
                ? (BookId: bookId, Episode: new BatchEpisode(videoId, number))
                : default)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId) && item.Episode is not null)
            .ToArray();
        if (episodes.Length == 0)
            return EmptyDisposable.Instance;

        var rawBookId = episodes[0].BookId;
        var planKey = BuildBatchPlanKey(settings, rawBookId, quality);
        var plan = new BatchParsePlan();
        var size = Math.Clamp(batchSize, 1, 10);
        foreach (var group in episodes
                     .Where(item => string.Equals(item.BookId, rawBookId, StringComparison.Ordinal))
                     .Select(item => item.Episode!)
                     .Chunk(size))
        {
            var captured = group.ToArray();
            var resolver = new Lazy<Task<IReadOnlyDictionary<string, HongguoNewApiService.HongguoVideoPlayback>>>(
                () => ResolveBatchGroupAsync(settings, rawBookId, captured, quality, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
            foreach (var episode in captured)
                plan.GroupsByVideoId[episode.VideoId] = resolver;
        }

        _batchParsePlans[planKey] = plan;
        return new BatchPlanLease(this, planKey, plan);
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
        try
        {
            var latest = await SearchLatestClientApiAsync(settings, keyword, page, cancellationToken);
            if (latest.Count > 0)
                return latest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep the former novelfm endpoint as a compatibility fallback when signing or the
            // current client endpoint is temporarily unavailable.
        }

        var legacy = await SearchLegacyNovelFmAsync(keyword, page, cancellationToken);
        return await CorrectSearchEpisodeTotalsFromDirectoryAsync(legacy, cancellationToken);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchLatestClientApiAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
            return [];

        var offset = Math.Max(0, (Math.Max(1, page) - 1) * 20);
        var now = DateTimeOffset.UtcNow;
        var normalSessionId = Guid.NewGuid().ToString();
        var coldStartSessionId = Guid.NewGuid().ToString();
        var spec = new JsonObject
        {
            ["host"] = "api5-normal-sinfonlinea.fqnovel.com",
            ["path"] = "/reading/bookapi/search/tab/v",
            ["method"] = "GET",
            ["purpose"] = "search",
            ["requestId"] = $"redguo.search:{page}:{Guid.NewGuid().ToString().ToUpperInvariant()}",
            ["device_profile_version"] = "hbr-account-tt-encrypt-v1",
            ["deviceProfileVersion"] = "hbr-account-tt-encrypt-v1",
            ["params"] = new JsonObject
            {
                ["aid"] = 8662,
                ["app_name"] = "novelread",
                ["version_code"] = 72332,
                ["version_name"] = "7.2.3.32",
                ["device_platform"] = "android",
                ["device_type"] = "LE2100",
                ["device_brand"] = "OnePlus",
                ["os"] = "android",
                ["os_version"] = "12",
                ["os_api"] = 31,
                ["manifest_version_code"] = 72332,
                ["update_version_code"] = 72332,
                ["channel"] = "oppo_8662_64",
                ["language"] = "zh",
                ["resolution"] = "1080*2400",
                ["dpi"] = 480,
                ["ac"] = "wifi",
                ["ssmix"] = "a",
                ["host_abi"] = "arm64-v8a",
                ["dragon_device_type"] = "phone",
                ["pv_player"] = 72332,
                ["compliance_status"] = 0,
                ["need_personal_recommend"] = 1,
                ["player_so_load"] = 1,
                ["is_android_pad_screen"] = 0,
                ["rom_version"] = "ColorOS_12.1_LE2100_12_C.63",
                ["query"] = trimmed,
                ["tab_type"] = 11,
                ["offset"] = offset,
                ["count"] = 20,
                ["search_source"] = 1,
                ["gender"] = 2,
                ["search_id"] = $"clks####11@{now.ToUnixTimeSeconds()}",
                ["normal_session_cnt_in_day"] = 10,
                ["cold_start_session_cnt_in_day"] = 4,
                ["normal_session_id"] = normalSessionId,
                ["cold_start_session_id"] = coldStartSessionId,
                ["session_id"] = $"{now.ToUnixTimeMilliseconds()}{Guid.NewGuid():N}"[..31].ToUpperInvariant(),
            },
            ["manual"] = page == 1,
            ["charge"] = page == 1,
        };
        var descriptor = AuthedRequestForTests is null
            ? await AuthedRequestAsync(settings, "/redguo/sign", spec, 60, cancellationToken)
            : await AuthedRequestForTests(settings, "/redguo/sign", spec, 60, cancellationToken);
        var payload = ExecuteSignedRequestForTests is null
            ? await ExecuteSignedFanqieRequestAsync(descriptor, [], 60, cancellationToken)
            : await ExecuteSignedRequestForTests(descriptor, [], 60, cancellationToken);

        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in HongguoHighCalendarMapper.ExtractSearchTabItems(payload))
        {
            var item = HongguoHighCalendarMapper.TryMapItem(raw);
            if (item is not null && seen.Add(item.BookId))
                results.Add(item);
        }
        return results;
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchLegacyNovelFmAsync(
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
                EpisodeTotal: HongguoHighCalendarMapper.ReadEpisodeTotal(book),
                Intro: GetString(book, "abstract") ?? "",
                PosterUrl: FirstHttpUrl(
                    GetString(book, "thumb_url"),
                    GetString(book, "cover"),
                    GetString(book, "series_cover"),
                    GetString(book, "cover_url"),
                    GetString(book, "poster"),
                    GetString(book, "audio_thumb_uri"),
                    GetString(book, "thumb_uri")) ?? "",
                Author: GetString(book, "author") ?? GetString(book, "anchor") ?? "",
                PublishTime: "",
                FavoriteCount: GetInt(book, "favorite_count") ?? 0));
        }

        return results;
    }

    private async Task<IReadOnlyList<DramaSearchItem>> CorrectSearchEpisodeTotalsFromDirectoryAsync(
        IReadOnlyList<DramaSearchItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return items;

        using var gate = new SemaphoreSlim(6, 6);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var rawBookId = HongguoHighCrypto.StripBookPrefix(item.BookId);
                if (string.IsNullOrWhiteSpace(rawBookId))
                    return item;
                var directory = await FetchFanqieDirectoryDataAsync(rawBookId, cancellationToken);
                var actualCount = directory["item_list"] is JsonArray episodes ? episodes.Count : 0;
                return actualCount > 0 ? item with { EpisodeTotal = actualCount } : item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return item;
            }
            finally
            {
                gate.Release();
            }
        }));
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

        var data = await FetchFanqieDirectoryDataAsync(rawBookId, cancellationToken);
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

        var timeout = ParsePlaybackTimeout(settings.HongguoDownloadTimeoutSeconds);
        var planKey = BuildBatchPlanKey(settings, bookId, quality);
        if (_batchParsePlans.TryGetValue(planKey, out var plan) &&
            plan.GroupsByVideoId.TryGetValue(rawVideoId, out var groupResolver))
        {
            try
            {
                var groupResults = await groupResolver.Value.WaitAsync(cancellationToken);
                if (groupResults.TryGetValue(rawVideoId, out var plannedPlayback))
                    return plannedPlayback;
            }
            catch (HongguoHighException ex) when (ex.Code == 408)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryablePlaybackParseException(ex, cancellationToken))
            {
                // A partial/temporary batch failure falls back to the established single-episode path below.
            }
        }

        JsonObject? lastItem = null;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= PlaybackParseMaxAttempts; attempt++)
        {
            var requestData = new JsonObject
            {
                ["bookId"] = bookId,
                ["book_id"] = bookId,
                ["episodes"] = new JsonArray { HongguoHighCrypto.BatchParseEpisodePayload(rawVideoId, episodeNumber) },
                ["quality"] = HongguoHighCrypto.NormalizeQuality(quality),
                ["resolution"] = HongguoHighCrypto.NormalizeQuality(quality)
            };
            try
            {
                var inner = AuthedRequestForTests is null
                    ? await AuthedRequestAsync(settings, "/video/batch-parse", requestData, timeout, cancellationToken)
                    : await AuthedRequestForTests(settings, "/video/batch-parse", requestData, timeout, cancellationToken);

                lastError = null;
                lastItem = SelectPlaybackItem(inner, rawVideoId);
                var url = ReadPlaybackUrl(lastItem);
                if (IsHttpUrl(url))
                {
                    var size = GetLong(lastItem, "size")
                               ?? GetLong(lastItem, "size_bytes")
                               ?? GetLong(lastItem, "sizeBytes")
                               ?? 0;
                    return new HongguoNewApiService.HongguoVideoPlayback(url!, size);
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreatePlaybackTimeoutException(timeout, ex);
            }
            catch (Exception ex) when (IsRetryablePlaybackParseException(ex, cancellationToken))
            {
                lastError = ex;
                lastItem = null;
            }

            if (attempt < PlaybackParseMaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(attempt);
                if (DelayForTests is null)
                {
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    await DelayForTests(delay, cancellationToken);
                }
            }
        }

        var detail = lastError?.Message ?? GetString(lastItem, "message") ?? GetString(lastItem, "msg");
        var code = lastError is HongguoHighException highError ? highError.Code : 0;
        throw new HongguoHighException(string.IsNullOrWhiteSpace(detail)
            ? $"高码率解析连续 {PlaybackParseMaxAttempts} 次未返回播放地址"
            : $"高码率解析连续 {PlaybackParseMaxAttempts} 次未返回播放地址：{detail}",
            code,
            lastError);
    }

    private async Task<IReadOnlyDictionary<string, HongguoNewApiService.HongguoVideoPlayback>> ResolveBatchGroupAsync(
        DramaSourceSettings settings,
        string bookId,
        IReadOnlyList<BatchEpisode> episodes,
        string quality,
        CancellationToken cancellationToken)
    {
        var timeout = ParsePlaybackTimeout(settings.HongguoDownloadTimeoutSeconds);
        var missing = episodes.ToDictionary(item => item.VideoId, StringComparer.Ordinal);
        var resolved = new Dictionary<string, HongguoNewApiService.HongguoVideoPlayback>(StringComparer.Ordinal);

        for (var attempt = 1; attempt <= PlaybackParseMaxAttempts && missing.Count > 0; attempt++)
        {
            try
            {
                var requestData = new JsonObject
                {
                    ["bookId"] = bookId,
                    ["book_id"] = bookId,
                    ["episodes"] = new JsonArray(missing.Values
                        .Select(item => (JsonNode)HongguoHighCrypto.BatchParseEpisodePayload(item.VideoId, item.EpisodeNumber))
                        .ToArray()),
                    ["quality"] = HongguoHighCrypto.NormalizeQuality(quality),
                    ["resolution"] = HongguoHighCrypto.NormalizeQuality(quality)
                };
                var inner = AuthedRequestForTests is null
                    ? await AuthedRequestAsync(settings, "/video/batch-parse", requestData, timeout, cancellationToken)
                    : await AuthedRequestForTests(settings, "/video/batch-parse", requestData, timeout, cancellationToken);

                foreach (var item in EnumeratePlaybackItems(inner))
                {
                    var videoId = ReadPlaybackVideoId(item);
                    var url = ReadPlaybackUrl(item);
                    if (string.IsNullOrWhiteSpace(videoId) || !missing.ContainsKey(videoId) || !IsHttpUrl(url))
                        continue;
                    var size = GetLong(item, "size") ?? GetLong(item, "size_bytes") ?? GetLong(item, "sizeBytes") ?? 0;
                    resolved[videoId] = new HongguoNewApiService.HongguoVideoPlayback(url!, size);
                    missing.Remove(videoId);
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreatePlaybackTimeoutException(timeout, ex);
            }
            catch (Exception ex) when (IsRetryablePlaybackParseException(ex, cancellationToken))
            {
                // Retry the still-missing subset as one batch.
            }

            if (missing.Count > 0 && attempt < PlaybackParseMaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(attempt);
                if (DelayForTests is null)
                    await Task.Delay(delay, cancellationToken);
                else
                    await DelayForTests(delay, cancellationToken);
            }
        }

        return resolved;
    }

    private static string BuildBatchPlanKey(DramaSourceSettings settings, string bookId, string quality) =>
        string.Join("\n",
            settings.HghighAccount?.Trim().ToLowerInvariant() ?? "",
            settings.HghighDeviceId?.Trim().ToLowerInvariant() ?? "",
            HongguoHighCrypto.StripBookPrefix(bookId),
            HongguoHighCrypto.NormalizeQuality(quality));

    private static HongguoHighException CreatePlaybackTimeoutException(int timeoutSeconds, Exception innerException) =>
        new($"高码率播放地址解析超过 {timeoutSeconds} 秒，已停止等待", 408, innerException);

    private static IEnumerable<JsonObject> EnumeratePlaybackItems(JsonNode? response)
    {
        if (response is JsonArray array)
            return array.OfType<JsonObject>();
        if (response is not JsonObject obj)
            return [];
        foreach (var key in new[] { "data", "items", "results", "episodes" })
        {
            if (obj[key] is JsonArray nested)
                return nested.OfType<JsonObject>();
            if (obj[key] is JsonObject nestedObject)
                return [nestedObject];
        }
        return [obj];
    }

    private static string? ReadPlaybackVideoId(JsonObject item) =>
        GetString(item, "episode_id") ?? GetString(item, "episodeId") ??
        GetString(item, "video_id") ?? GetString(item, "videoId");

    private sealed class BatchPlanLease(
        HongguoHighApiService owner,
        string key,
        BatchParsePlan plan) : IDisposable
    {
        public void Dispose()
        {
            if (owner._batchParsePlans.TryGetValue(key, out var current) && ReferenceEquals(current, plan))
                owner._batchParsePlans.TryRemove(key, out _);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private static bool IsRetryablePlaybackParseException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is HttpRequestException)
            return true;
        if (exception is TaskCanceledException)
            return !cancellationToken.IsCancellationRequested;
        if (exception is not HongguoHighException high || ShouldRelogin(high))
            return false;

        if (LandpageRetryCodes.Contains(high.Code))
            return true;

        var message = high.Message ?? "";
        return (message.Contains("未返回", StringComparison.Ordinal) &&
                (message.Contains("下载地址", StringComparison.Ordinal) ||
                 message.Contains("播放地址", StringComparison.Ordinal))) ||
               message.Contains("解析器繁忙", StringComparison.Ordinal) ||
               message.Contains("稍后重试", StringComparison.Ordinal) ||
               message.Contains("请求超时", StringComparison.Ordinal) ||
               message.Contains("限流", StringComparison.Ordinal) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject? SelectPlaybackItem(JsonNode? response, string rawVideoId)
    {
        var candidates = new List<JsonObject>();
        switch (response)
        {
            case JsonArray array:
                candidates.AddRange(array.OfType<JsonObject>());
                break;
            case JsonObject obj:
                foreach (var key in new[] { "data", "items", "results", "episodes" })
                {
                    if (obj[key] is JsonArray nested)
                    {
                        candidates.AddRange(nested.OfType<JsonObject>());
                    }
                    else if (obj[key] is JsonObject nestedObject)
                    {
                        candidates.Add(nestedObject);
                    }
                }

                if (candidates.Count == 0)
                {
                    candidates.Add(obj);
                }

                break;
        }

        return candidates.FirstOrDefault(item =>
                   string.Equals(GetString(item, "episode_id"), rawVideoId, StringComparison.Ordinal) ||
                   string.Equals(GetString(item, "episodeId"), rawVideoId, StringComparison.Ordinal) ||
                   string.Equals(GetString(item, "video_id"), rawVideoId, StringComparison.Ordinal) ||
                   string.Equals(GetString(item, "videoId"), rawVideoId, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault();
    }

    private static string? ReadPlaybackUrl(JsonObject? item) =>
        GetString(item, "url")
        ?? GetString(item, "video_url")
        ?? GetString(item, "download_url")
        ?? GetString(item, "play_url")
        ?? GetString(item, "playUrl")
        ?? GetString(item, "videoUrl")
        ?? GetString(item, "downloadUrl")
        ?? GetString(item, "DownloadUrl");

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<DramaSearchItem>> GetManjuNewAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken) =>
        await GetManjuNewAsync(settings, days, enrich: true, progress: null, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetManjuNewAsync(
        DramaSourceSettings settings,
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        return await GetCalendarNewAsync(
            "manju",
            settings,
            days,
            enrich,
            progress,
            async (page, ct) =>
            {
                var inner = await AuthedRequestAsync(
                    settings,
                    "/redguo/fanqie-new",
                    new JsonObject
                    {
                        ["append"] = false,
                        ["limit"] = CalendarPageSize,
                        ["page"] = page,
                        ["category"] = "comic",
                        ["channel"] = "fanqieBackup",
                        ["offset"] = (page - 1) * CalendarPageSize
                    },
                    timeout,
                    ct);
                return HongguoHighCalendarMapper.MapPayload(inner);
            },
            timeout,
            cancellationToken,
            partialResults,
            detailResults);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetAiNewAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken) =>
        await GetAiNewAsync(settings, days, enrich: true, progress: null, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetAiNewAsync(
        DramaSourceSettings settings,
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        return await GetCalendarNewAsync(
            "aiju",
            settings,
            days,
            enrich,
            progress,
            async (page, ct) =>
            {
                var inner = await FetchAiLandpagePageAsync(settings, page, timeout, ct);
                var mapped = HongguoHighCalendarMapper.MapPayload(inner);
                return mapped.Count > 0
                    ? mapped
                    : HongguoHighCalendarMapper.ExtractLandpageItems(inner)
                        .Select(HongguoHighCalendarMapper.TryMapItem)
                        .Where(item => item is not null)
                        .Select(item => item!)
                        .ToArray();
            },
            timeout,
            cancellationToken,
            partialResults,
            detailResults);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> EnrichNewReleaseItemsAsync(
        DramaSourceSettings settings,
        IReadOnlyList<DramaSearchItem> items,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var timeout = ParseTimeout(settings.HongguoDownloadTimeoutSeconds);
        return await EnrichCalendarItemsAsync(
            items,
            timeout,
            progress,
            cancellationToken,
            detailResults);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> GetCalendarNewAsync(
        string kind,
        DramaSourceSettings settings,
        int days,
        bool enrich,
        IProgress<string>? progress,
        Func<int, CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> pageLoader,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var windowDays = Math.Clamp(days, 1, 30);
        var phase = enrich ? "enriched" : "list";
        var key = CalendarCacheKey(kind, windowDays, phase, settings);
        if (TryReadCalendarCache(key, out var cached))
        {
            progress?.Report($"已使用 5 分钟内{(enrich ? "完整结果" : "上新列表")}缓存 · 共 {cached.Count} 部");
            if (!enrich)
                partialResults?.Report(cached);
            return cached;
        }

        var listKey = CalendarCacheKey(kind, windowDays, "list", settings);
        IReadOnlyList<DramaSearchItem> items;
        if (enrich && TryReadCalendarCache(listKey, out var listCached))
        {
            items = listCached;
            progress?.Report($"已复用上新列表 · 共 {items.Count} 部");
        }
        else
        {
            items = await FetchCalendarListAsync(
                pageLoader,
                windowDays,
                progress,
                cancellationToken,
                partialResults);
            _calendarCache[listKey] = new CalendarCacheEntry(DateTimeOffset.UtcNow, items);
        }

        if (enrich && items.Count > 0)
        {
            progress?.Report($"上新列表已获取 · 共 {items.Count} 部，正在补充详情...");
            items = await EnrichCalendarItemsAsync(
                items,
                timeoutSeconds,
                progress,
                cancellationToken,
                detailResults);
            items = HongguoHighCalendarMapper.FilterByRecentDays(items, windowDays);
        }

        _calendarCache[key] = new CalendarCacheEntry(DateTimeOffset.UtcNow, items);
        return items;
    }

    internal Task<IReadOnlyList<DramaSearchItem>> GetCalendarNewForTestsAsync(
        string kind,
        DramaSourceSettings settings,
        int days,
        bool enrich,
        IProgress<string>? progress,
        Func<int, CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> pageLoader,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null) =>
        GetCalendarNewAsync(
            kind, settings, days, enrich, progress, pageLoader, 30, cancellationToken, partialResults, detailResults);

    private static async Task<IReadOnlyList<DramaSearchItem>> FetchCalendarListAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> pageLoader,
        int days,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null)
    {
        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool MergePage(int page, IReadOnlyList<DramaSearchItem> pageItems)
        {
            foreach (var item in pageItems)
            {
                if (seen.Add(item.BookId))
                {
                    results.Add(item);
                }
            }

            progress?.Report($"正在拉取上新列表 · 第 {page} 页 · 已发现 {results.Count} 部");
            return pageItems.Count == 0 || pageItems.Count < CalendarPageSize || PageIsBeforeWindow(pageItems, days);
        }

        void ReportPartial() =>
            partialResults?.Report(HongguoHighCalendarMapper.FilterByRecentDays(results, days));

        progress?.Report("正在拉取上新列表 · 第 1 页");
        var stopped = MergePage(1, await pageLoader(1, cancellationToken));
        ReportPartial();
        for (var start = 2; !stopped && start <= CalendarMaxPages; start += CalendarPageConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumbers = Enumerable.Range(start, Math.Min(CalendarPageConcurrency, CalendarMaxPages - start + 1)).ToArray();
            progress?.Report($"正在并发拉取上新列表 · 第 {pageNumbers.First()}-{pageNumbers.Last()} 页");
            var pending = pageNumbers
                .Select(async page => (Page: page, Items: await pageLoader(page, cancellationToken)))
                .ToList();
            var completed = new SortedDictionary<int, IReadOnlyList<DramaSearchItem>>();
            var nextPage = pageNumbers.First();
            while (pending.Count > 0)
            {
                var task = await Task.WhenAny(pending);
                pending.Remove(task);
                var page = await task;
                completed[page.Page] = page.Items;
                while (!stopped && completed.Remove(nextPage, out var pageItems))
                {
                    stopped = MergePage(nextPage, pageItems);
                    ReportPartial();
                    nextPage++;
                }
            }
        }

        return HongguoHighCalendarMapper.FilterByRecentDays(results, days);
    }

    internal static Task<IReadOnlyList<DramaSearchItem>> FetchCalendarListForTestsAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> pageLoader,
        int days,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null) =>
        FetchCalendarListAsync(pageLoader, days, progress, cancellationToken, partialResults);

    private async Task<IReadOnlyList<DramaSearchItem>> EnrichCalendarItemsAsync(
        IReadOnlyList<DramaSearchItem> items,
        int timeoutSeconds,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var gate = new SemaphoreSlim(CalendarEnrichConcurrency);
        var completed = 0;
        var reportGate = new object();
        var pendingReports = new List<DramaSearchItem>();
        var enriched = await Task.WhenAll(items.Select(async item =>
        {
            DramaSearchItem result;
            if (!NeedsCalendarEnrichment(item))
            {
                result = item;
            }
            else
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var info = await FetchFanqieBookInfoAsync(item.BookId, timeoutSeconds, cancellationToken);
                    result = info is null ? item : HongguoHighCalendarMapper.ApplyBookInfo(item, info);
                }
                finally
                {
                    gate.Release();
                }
            }

            var done = Interlocked.Increment(ref completed);
            IReadOnlyList<DramaSearchItem>? reportBatch = null;
            lock (reportGate)
            {
                pendingReports.Add(result);
                if (pendingReports.Count >= 10 || done == items.Count)
                {
                    reportBatch = pendingReports.ToArray();
                    pendingReports.Clear();
                }
            }
            if (done == items.Count || done % 20 == 0)
                progress?.Report($"正在补充剧目详情 · {done}/{items.Count}");
            if (reportBatch is not null)
                detailResults?.Report(reportBatch);
            return result;
        }));
        IReadOnlyList<DramaSearchItem>? finalBatch = null;
        lock (reportGate)
        {
            if (pendingReports.Count > 0)
            {
                finalBatch = pendingReports.ToArray();
                pendingReports.Clear();
            }
        }
        if (finalBatch is not null)
            detailResults?.Report(finalBatch);
        return enriched;
    }

    internal static bool NeedsCalendarEnrichment(DramaSearchItem item) =>
        string.IsNullOrWhiteSpace(item.Author) ||
        item.EpisodeTotal <= 0 ||
        string.IsNullOrWhiteSpace(item.PublishTime) ||
        string.IsNullOrWhiteSpace(item.PosterUrl);

    private static bool PageIsBeforeWindow(IReadOnlyList<DramaSearchItem> items, int days)
    {
        var cutoff = DateTimeOffset.Now.Date.AddDays(-Math.Clamp(days, 1, 30) + 1);
        var parsed = items
            .Select(item => DateTimeOffset.TryParse(item.PublishTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
                ? value
                : (DateTimeOffset?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return parsed.Length > 0 && parsed.Max().Date < cutoff;
    }

    private static string CalendarCacheKey(string kind, int days, string phase, DramaSourceSettings settings) =>
        string.Join('|', kind, days, phase, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (settings.HghighAccount ?? "").Trim());

    private bool TryReadCalendarCache(string key, out IReadOnlyList<DramaSearchItem> items)
    {
        if (_calendarCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt <= CalendarCacheLifetime)
        {
            items = cached.Items;
            return true;
        }

        _calendarCache.TryRemove(key, out _);
        items = [];
        return false;
    }

    private async Task<JsonObject?> FetchFanqieBookInfoAsync(
        string bookId,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var rawBookId = HongguoHighCrypto.StripBookPrefix(bookId);
        if (string.IsNullOrWhiteSpace(rawBookId))
        {
            return null;
        }

        if (_calendarDetailCache.TryGetValue(rawBookId, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt <= CalendarDetailCacheLifetime)
        {
            return cached.BookInfo.DeepClone().AsObject();
        }
        _calendarDetailCache.TryRemove(rawBookId, out _);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 120)));
            var data = await FetchFanqieDirectoryDataAsync(rawBookId, timeoutCts.Token);
            var info = data["book_info"] as JsonObject ?? data;
            var directoryPoster = HongguoHighCalendarMapper.ExtractMediaUrl(data);
            if (!string.IsNullOrWhiteSpace(directoryPoster) &&
                string.IsNullOrWhiteSpace(HongguoHighCalendarMapper.ExtractMediaUrl(info)))
            {
                info["cover_url"] = directoryPoster;
            }
            _calendarDetailCache[rawBookId] = new CalendarDetailCacheEntry(
                DateTimeOffset.UtcNow,
                info.DeepClone().AsObject());
            return info;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonObject> FetchFanqieDirectoryDataAsync(string rawBookId, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(FanqieDirectoryUrl)
        {
            Query = $"book_id={Uri.EscapeDataString(rawBookId)}&aid={HongguoHighCrypto.FanqieDirectoryAid}"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", FanqieWeChatUa);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await ReadJsonAsync(response, cancellationToken);
        EnsureBusinessOk(payload, "剧集目录失败");
        return payload["data"] as JsonObject ?? payload;
    }

    internal async Task<JsonNode?> FetchAiLandpagePageAsync(
        DramaSourceSettings settings,
        int page,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var body = BuildAiLandpageBody(page);
        var packed = HongguoHighCrypto.GzipStoreJson(body);
        var digest = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(packed));
        Exception? lastError = null;
        for (var attempt = 1; attempt <= LandpageMaxAttempts; attempt++)
        {
            var spec = new JsonObject
            {
                ["host"] = "api5-normal-sinfonlinea.fqnovel.com",
                ["path"] = "/reading/distribution/category/landpage/v:version/",
                ["method"] = "POST",
                ["purpose"] = "discovery",
                ["requestId"] = $"redguo.discovery:ai:{page}:{Guid.NewGuid().ToString().ToUpperInvariant()}",
                ["device_profile_version"] = "hbr-account-tt-encrypt-v1",
                ["deviceProfileVersion"] = "hbr-account-tt-encrypt-v1",
                ["content_encoding"] = "gzip",
                ["contentEncoding"] = "gzip",
                ["params"] = new JsonObject
                {
                    ["bdhm_bid"] = "novelread_lynx",
                    ["bdhm_pid"] = "filter-page"
                },
                ["json"] = body.DeepClone(),
                ["body_md5"] = digest,
                ["bodyMd5"] = digest,
                ["manual"] = page == 1,
                ["charge"] = page == 1
            };
            try
            {
                // 签名头含时间戳；每次重试都重新请求签名，不能复用旧 descriptor。
                var descriptor = AuthedRequestForTests is null
                    ? await AuthedRequestAsync(settings, "/redguo/sign", spec, timeoutSeconds, cancellationToken)
                    : await AuthedRequestForTests(settings, "/redguo/sign", spec, timeoutSeconds, cancellationToken);
                return ExecuteSignedRequestForTests is null
                    ? await ExecuteSignedFanqieRequestAsync(descriptor, packed, timeoutSeconds, cancellationToken)
                    : await ExecuteSignedRequestForTests(descriptor, packed, timeoutSeconds, cancellationToken);
            }
            catch (HongguoHighException ex) when (LandpageRetryCodes.Contains(ex.Code) && attempt < LandpageMaxAttempts)
            {
                lastError = ex;
            }
            catch (HttpRequestException ex) when (attempt < LandpageMaxAttempts)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < LandpageMaxAttempts)
            {
                lastError = ex;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(6, 1.5 * Math.Pow(2, attempt - 1)));
            if (DelayForTests is null)
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                await DelayForTests(delay, cancellationToken);
            }
        }

        throw lastError ?? new HongguoHighException("番茄 landpage 请求失败");
    }

    private async Task<JsonNode?> ExecuteSignedFanqieRequestAsync(
        JsonNode? descriptor,
        byte[] gzipBody,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (descriptor is not JsonObject obj)
        {
            return descriptor;
        }

        if (obj["data"] is JsonObject nested &&
            (nested["headers"] is not null || nested["host"] is not null || nested["path"] is not null || nested["url"] is not null))
        {
            obj = nested;
        }

        var executable = obj["headers"] is not null || obj["host"] is not null || obj["path"] is not null || obj["url"] is not null;
        if (!executable)
        {
            return obj;
        }

        var host = GetString(obj, "host") ?? "";
        var path = GetString(obj, "path") ?? "";
        var url = GetString(obj, "url") ?? GetString(obj, "href") ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path))
            {
                throw new HongguoHighException("红果签名未返回可执行的番茄请求");
            }

            url = "https://" + host.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        var method = (GetString(obj, "method") ?? "POST").ToUpperInvariant();
        var hasRequestBody = method is not ("GET" or "HEAD");
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (obj["headers"] is JsonObject headers)
        {
            foreach (var header in headers)
            {
                var name = header.Key;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = NodeToText(header.Value);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    if (!hasRequestBody)
                        continue;
                    request.Content ??= new ByteArrayContent(gzipBody);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (obj["params"] is JsonObject queryParams && request.RequestUri is not null)
        {
            var pairs = queryParams.Select(pair =>
            {
                var text = NodeToText(pair.Value);
                return $"{EscapeFanqieQueryValue(pair.Key)}={EscapeFanqieQueryValue(text)}";
            });
            var builder = new UriBuilder(request.RequestUri) { Query = string.Join("&", pairs) };
            request.RequestUri = builder.Uri;
        }

        if (hasRequestBody)
        {
            request.Content ??= new ByteArrayContent(gzipBody);
            request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 120)));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        var payload = await ReadJsonAsync(response, timeoutCts.Token);
        if (response.StatusCode >= System.Net.HttpStatusCode.BadRequest && payload["code"] is null)
        {
            throw new HongguoHighException($"番茄 landpage HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }

        EnsureBusinessOk(payload, "番茄 landpage 失败");
        return payload["data"] ?? payload;
    }

    internal static string EscapeFanqieQueryValue(string value) =>
        Uri.EscapeDataString(value ?? "")
            .Replace("%2A", "*", StringComparison.OrdinalIgnoreCase);

    private static JsonObject BuildAiLandpageBody(int page)
    {
        var offset = Math.Max(0, (Math.Max(1, page) - 1) * 20);
        return new JsonObject
        {
            ["client_req_type"] = 3,
            ["filter_ids"] = "",
            ["limit"] = 20,
            ["need_selector_panel"] = false,
            ["offset"] = offset,
            ["req_scene"] = "comic_series",
            ["req_type"] = "only_content",
            ["select_items"] = new JsonObject
            {
                ["category_dim_epoch"] = new JsonArray(),
                ["category_dim_role"] = new JsonArray(),
                ["category_dim_theme"] = new JsonArray(),
                ["gender"] = new JsonArray(),
                ["genre"] = new JsonArray { "ai_series" },
                ["online_time"] = new JsonArray { "days_7" },
                ["sort"] = new JsonArray { "online_time" }
            },
            ["session_id"] = ""
        };
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

        ApplyLoginData(settings, device, data);
        return data;
    }

    private void ApplyLoginData(
        DramaSourceSettings settings,
        HongguoHighDevice device,
        JsonObject data)
    {
        var account = (settings.HghighAccount ?? "").Trim();

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
    }

    private async Task<JsonNode?> AuthedRequestAsync(
        DramaSourceSettings settings,
        string path,
        JsonObject data,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(settings, timeoutSeconds, cancellationToken);
        var staleToken = ReadCurrentAccessToken();
        try
        {
            return await RequestAsync(LoadDevice(), _session, "POST", path, data, timeoutSeconds, cancellationToken);
        }
        catch (HongguoHighException ex) when (ShouldRelogin(ex))
        {
            await RefreshTokenAsync(settings, staleToken, cancellationToken);
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
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_session.AccessToken) &&
                    _session.Account == account &&
                    _session.BoundDeviceId == device.DeviceId)
                {
                    return;
                }
            }

            await LoginOnceAsync(settings, cancellationToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task RefreshTokenAsync(
        DramaSourceSettings settings,
        string staleToken,
        CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_session.AccessToken) &&
                    !string.Equals(_session.AccessToken, staleToken, StringComparison.Ordinal))
                {
                    return;
                }

                _session.Clear();
            }

            await LoginOnceAsync(settings, cancellationToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task<JsonObject> LoginOnceAsync(
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (LoginForTests is null)
        {
            return await LoginAsync(settings, cancellationToken);
        }

        var data = await LoginForTests(settings, cancellationToken);
        ApplyLoginData(settings, LoadDevice(), data);
        return data;
    }

    private string ReadCurrentAccessToken()
    {
        lock (_gate)
        {
            return _session.AccessToken;
        }
    }

    internal Task EnsureTokenForTestsAsync(
        DramaSourceSettings settings,
        CancellationToken cancellationToken) =>
        EnsureTokenAsync(settings, ParseTimeout(settings.HongguoDownloadTimeoutSeconds), cancellationToken);

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
        string bearer;
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

            // The encrypted envelope and Authorization header must come from the
            // same session generation. A concurrent relogin may replace _session.
            bearer = HongguoHighCrypto.TrimBearer(session.AccessToken);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), JoinApi(normalizedPath))
        {
            Content = JsonContent(envelope)
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.TryAddWithoutValidation("X-App-Id", HongguoHighCrypto.AppId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", device.DeviceId);
        request.Headers.TryAddWithoutValidation("X-Client-Version", HongguoHighCrypto.ClientVersion);
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

    internal static bool ShouldRelogin(HongguoHighException ex)
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
               message.Contains("会话凭证不一致", StringComparison.Ordinal) ||
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

    private static int ParsePlaybackTimeout(string? value) =>
        Math.Min(ParseTimeout(value), 15);

    private static byte[] RandomNumberGeneratorBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string? FirstHttpUrl(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
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
