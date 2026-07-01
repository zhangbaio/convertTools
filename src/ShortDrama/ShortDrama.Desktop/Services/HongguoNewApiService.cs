using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class HongguoNewApiService
{
    private const string BaseUrlTemplate = "https://au.s1o.cc/api/user/1000/win/{0}";
    private const string FallbackDailyUrl = "http://129.211.169.30:996/new.php";
    private const string AppKey = "c8b9d4a1f3e265c89a0b1d3f4e5a6c7b";
    private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("asKVK4K5tEPg4inz");
    private const string DefaultVersion = "1.3.4";
    private const string MinVersion = DefaultVersion;
    private static readonly string[] AxiosWrapperKeys = ["status", "statusText", "headers", "config", "request"];
    private static readonly HashSet<int> LoginRetryCodes = [46, 141, 401];
    private static readonly string[] LoginRetryHints =
    [
        "token不存在",
        "token已失效",
        "登录已失效",
        "登录过期",
        "未登录",
        "请重新登录",
        "退出重新登录"
    ];
    private static readonly string[] DailyModes = ["djnew", "mjnew", "aiju"];
    private static readonly string[] PosterKeys =
    [
        "poster", "poster_url", "cover", "cover_url", "thumbnail", "thumbnail_url", "thumb", "thumb_url",
        "image", "image_url", "img", "img_url", "pic", "pic_url", "book_pic", "book_cover",
        "video_cover", "vertical_cover", "vertical_cover_url", "horizontal_cover", "horizontal_cover_url"
    ];
    private static readonly string[] PosterKeywords = ["poster", "cover", "thumb", "thumbnail", "image", "img", "pic"];
    private static readonly Dictionary<string, string> CloudFunctionNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mjnew"] = "getManjuData",
        ["djnew"] = "getDuanjuData",
        ["aiju"] = "getAijuData",
        ["video"] = "getVideoData",
        ["book"] = "getBookData",
        ["search"] = "getSearchData"
    };
    private static readonly Dictionary<string, string> HistoryTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["djnew"] = "duanju",
        ["mjnew"] = "manju",
        ["aiju"] = "aiju"
    };

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, HongguoTokenCacheEntry> _tokenCache = new(StringComparer.Ordinal);
    private readonly object _tokenGate = new();

    public HongguoNewApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ProbeLoginAsync(GlobalConfigSnapshot settings, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(ResolveCredentials(settings), 30, cancellationToken);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        GlobalConfigSnapshot settings,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var trimmedKeyword = (keyword ?? string.Empty).Trim();
        if (trimmedKeyword.Length == 0)
        {
            return [];
        }

        var items = await CloudFunctionItemsAsync(
            "search",
            ResolveCredentials(settings),
            30,
            trimmedKeyword,
            cancellationToken);
        return items.Select(MapSearchItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayNewAsync(
        GlobalConfigSnapshot settings,
        string mode,
        CancellationToken cancellationToken)
    {
        ValidateDailyMode(mode);
        var credentials = ResolveCredentials(settings);

        try
        {
            var items = await CloudFunctionItemsAsync(
                mode,
                credentials,
                30,
                null,
                cancellationToken);
            return SortByPublishTime(items)
                .Select(MapSearchItem)
                .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
                .ToArray();
        }
        catch (HongguoNewApiException ex) when (ex.Code >= 400)
        {
            var fallbackItems = await FetchFallbackDailyNewAsync(credentials.ClientVersion, cancellationToken);
            return SortByPublishTime(fallbackItems)
                .Select(MapSearchItem)
                .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetDailyByDatesAsync(
        GlobalConfigSnapshot settings,
        string mode,
        IReadOnlyList<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        ValidateDailyMode(mode);
        var credentials = ResolveCredentials(settings);
        var rawItems = await CloudFunctionItemsAsync(
            mode,
            credentials,
            30,
            null,
            cancellationToken);

        var dateSet = dates
            .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        return SortByPublishTime(rawItems.Where(item => MatchesPublishDate(item, dateSet)))
            .Select(MapSearchItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .DistinctBy(item => item.BookId)
            .ToArray();
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetHistoryByDatesAsync(
        GlobalConfigSnapshot settings,
        string mode,
        IReadOnlyList<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        ValidateDailyMode(mode);
        var credentials = ResolveCredentials(settings);
        var allItems = new List<Dictionary<string, object?>>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var date in dates)
        {
            var paramJson = JsonSerializer.Serialize(new
            {
                type = HistoryTypeMap.GetValueOrDefault(mode, mode),
                time = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            });

            var items = await CloudFunctionItemsAsync(
                "getTmDailyData",
                credentials,
                30,
                paramJson,
                cancellationToken);

            foreach (var item in items)
            {
                var mapped = MapSearchItem(item);
                if (string.IsNullOrWhiteSpace(mapped.BookId) || !seenIds.Add(mapped.BookId))
                {
                    continue;
                }

                allItems.Add(item);
            }
        }

        return SortByPublishTime(allItems)
            .Select(MapSearchItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    public async Task<IReadOnlyList<HongguoEpisodeInfo>> GetEpisodesAsync(
        GlobalConfigSnapshot settings,
        string bookId,
        CancellationToken cancellationToken)
    {
        var trimmedBookId = (bookId ?? string.Empty).Trim();
        if (trimmedBookId.Length == 0)
        {
            return [];
        }

        var credentials = ResolveCredentials(settings);
        var paramJson = JsonSerializer.Serialize(new
        {
            book_id = trimmedBookId
        });
        var items = await CloudFunctionItemsAsync(
            "book",
            credentials,
            30,
            paramJson,
            cancellationToken);

        var episodes = new List<HongguoEpisodeInfo>();
        var index = 1;
        foreach (var item in items)
        {
            var videoId = FirstNonEmpty(
                GetStringValue(item, "video_id"),
                GetStringValue(item, "videoId"),
                GetStringValue(item, "vid"));
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var episodeNumber = GetIntValue(item, "index") ?? ExtractEpisodeNumber(GetStringValue(item, "title"), index);
            episodes.Add(new HongguoEpisodeInfo(
                EpisodeNumber: episodeNumber <= 0 ? index : episodeNumber,
                Title: FirstNonEmpty(GetStringValue(item, "title"), $"第{index:00}集"),
                VideoId: videoId,
                PosterUrl: ExtractPosterUrl(item)));
            index++;
        }

        return episodes
            .OrderBy(item => item.EpisodeNumber)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<HongguoVideoPlayback> GetVideoPlaybackAsync(
        GlobalConfigSnapshot settings,
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        var trimmedVideoId = (videoId ?? string.Empty).Trim();
        if (trimmedVideoId.Length == 0)
        {
            throw new HongguoNewApiException("video_id 不能为空。");
        }

        var credentials = ResolveCredentials(settings);
        return await GetVideoPlaybackWithRetryAsync(credentials, trimmedVideoId, quality, cancellationToken);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> FetchFallbackDailyNewAsync(
        string clientVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FallbackDailyUrl);
        ApplyVersionedHeaders(request, clientVersion, token: null);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HongguoNewApiException($"996/new.php fallback failed: {(int)response.StatusCode} {response.ReasonPhrase}", (int)response.StatusCode, body);
        }

        var root = ParseJsonObject(body, "996/new.php fallback");
        var code = GetIntValue(root, "code") ?? 0;
        if (code != 200)
        {
            throw new HongguoNewApiException($"996/new.php returned code={code}", code, root);
        }

        if (!root.TryGetValue("data", out var dataValue) || dataValue is not List<object?> list)
        {
            throw new HongguoNewApiException("996/new.php data is not an array.", payload: root);
        }

        return list.OfType<Dictionary<string, object?>>().ToArray();
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> CloudFunctionItemsAsync(
        string functionName,
        HongguoCredentials credentials,
        int timeoutSeconds,
        string? param,
        CancellationToken cancellationToken)
    {
        var outer = await CloudFunctionCallWithRetryAsync(
            functionName,
            credentials,
            timeoutSeconds,
            param,
            cancellationToken);
        var businessObject = UnwrapCloudResponse(outer);

        if (!businessObject.TryGetValue("data", out var dataValue) || dataValue is not List<object?> list)
        {
            throw new HongguoNewApiException("Cloud response inner data is not an array.", payload: outer);
        }

        return list.OfType<Dictionary<string, object?>>().ToArray();
    }

    private async Task<Dictionary<string, object?>> CloudFunctionCallWithRetryAsync(
        string functionName,
        HongguoCredentials credentials,
        int timeoutSeconds,
        string? param,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CloudFunctionCallOnceAsync(functionName, credentials, timeoutSeconds, param, cancellationToken);
        }
        catch (HongguoNewApiException ex) when (ShouldRetryLogin(ex))
        {
            InvalidateToken(credentials);
            return await CloudFunctionCallOnceAsync(functionName, credentials, timeoutSeconds, param, cancellationToken);
        }
    }

    private async Task<HongguoVideoPlayback> GetVideoPlaybackWithRetryAsync(
        HongguoCredentials credentials,
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetVideoPlaybackOnceAsync(credentials, videoId, quality, cancellationToken);
        }
        catch (HongguoNewApiException ex) when (ShouldRetryLogin(ex))
        {
            InvalidateToken(credentials);
            return await GetVideoPlaybackOnceAsync(credentials, videoId, quality, cancellationToken);
        }
    }

    private async Task<Dictionary<string, object?>> CloudFunctionCallOnceAsync(
        string functionName,
        HongguoCredentials credentials,
        int timeoutSeconds,
        string? param,
        CancellationToken cancellationToken)
    {
        var token = await EnsureTokenAsync(credentials, timeoutSeconds, cancellationToken);
        return await CloudFunctionCallAsync(
            functionName,
            credentials,
            token,
            timeoutSeconds,
            param,
            cancellationToken);
    }

    private async Task<HongguoVideoPlayback> GetVideoPlaybackOnceAsync(
        HongguoCredentials credentials,
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        var token = await EnsureTokenAsync(credentials, 30, cancellationToken);
        await InfoPingAsync(credentials, token, 30, cancellationToken);

        var paramJson = BuildVideoPlaybackParam(videoId, quality, token, credentials.Udid);
        var outer = await CloudFunctionCallAsync(
            "video",
            credentials,
            token,
            30,
            paramJson,
            cancellationToken);
        var businessObject = UnwrapCloudResponse(outer);
        if (!businessObject.TryGetValue("data", out var dataValue) || dataValue is not Dictionary<string, object?> detail)
        {
            throw new HongguoNewApiException("Cloud response video detail is invalid.", payload: outer);
        }

        var url = FirstNonEmpty(
            GetStringValue(detail, "url"),
            GetStringValue(detail, "play_url"),
            GetStringValue(detail, "playUrl"),
            GetStringValue(detail, "video_url"),
            GetStringValue(detail, "videoUrl"),
            GetStringValue(detail, "main_url"),
            GetStringValue(detail, "mainUrl"));
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new HongguoNewApiException("接口响应缺少视频直链。", payload: outer);
        }

        var size = detail.TryGetValue("info", out var infoValue) && infoValue is Dictionary<string, object?> info
            ? GetLongValue(info, "size") ?? 0L
            : 0L;
        return new HongguoVideoPlayback(url, size);
    }

    private async Task<Dictionary<string, object?>> CloudFunctionCallAsync(
        string functionName,
        HongguoCredentials credentials,
        string token,
        int timeoutSeconds,
        string? param,
        CancellationToken cancellationToken)
    {
        var url = BuildBaseUrl(credentials.ClientVersion) + "/cloudFunction";
        var mappedName = CloudFunctionNameMap.GetValueOrDefault(functionName, functionName);
        var fields = new Dictionary<string, string?>
        {
            ["name"] = mappedName,
            ["token"] = token
        };
        if (!string.IsNullOrWhiteSpace(param))
        {
            fields["param"] = param;
        }

        var outer = await PostEncryptedFormAsync(url, credentials, timeoutSeconds, fields, token, cancellationToken);
        var outerCode = GetIntValue(outer, "code") ?? 0;
        if (outerCode != 0)
        {
            throw new HongguoNewApiException(ReadMessage(outer, $"Cloud outer code={outerCode}"), outerCode, outer);
        }

        return outer;
    }

    private Dictionary<string, object?> UnwrapCloudResponse(Dictionary<string, object?> outer)
    {
        object? data = outer.TryGetValue("data", out var value) ? value : null;
        for (var index = 0; index < 3; index++)
        {
            var unwrapped = UnwrapOnce(data, outer);
            if (ReferenceEquals(unwrapped, data))
            {
                break;
            }

            data = unwrapped;
        }

        if (data is not Dictionary<string, object?> businessObject)
        {
            throw new HongguoNewApiException("Cloud response business object is invalid.", payload: outer);
        }

        var innerCode = GetIntValue(businessObject, "code") ?? 0;
        if (innerCode != 200)
        {
            throw new HongguoNewApiException(ReadMessage(businessObject, $"Cloud inner code={innerCode}"), innerCode, outer);
        }

        return businessObject;
    }

    private object? UnwrapOnce(object? data, object payload)
    {
        if (data is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return data;
            }

            try
            {
                return ParseJsonAny(trimmed, "cloud inner payload");
            }
            catch (JsonException ex)
            {
                if (trimmed[0] is not ('{' or '[' or '"'))
                {
                    throw new HongguoNewApiException($"Cloud function call failed: {trimmed[..Math.Min(300, trimmed.Length)]}", payload: payload, innerException: ex);
                }

                throw new HongguoNewApiException($"Cloud inner JSON parse failed: {ex.Message}", payload: payload, innerException: ex);
            }
        }

        if (data is Dictionary<string, object?> dictionary &&
            AxiosWrapperKeys.Any(dictionary.ContainsKey))
        {
            var httpStatus = GetIntValue(dictionary, "status") ?? 0;
            if (httpStatus >= 400)
            {
                var statusText = GetStringValue(dictionary, "statusText");
                throw new HongguoNewApiException(
                    $"Cloud upstream unavailable (HTTP {httpStatus}){(string.IsNullOrWhiteSpace(statusText) ? string.Empty : $": {statusText}")}",
                    httpStatus,
                    payload);
            }

            return dictionary.TryGetValue("data", out var inner) ? inner : null;
        }

        return data;
    }

    private async Task<string> EnsureTokenAsync(
        HongguoCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{credentials.Account.Trim()}\n{credentials.Udid.Trim().ToUpperInvariant()}\n{credentials.ClientVersion.Trim()}";
        lock (_tokenGate)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.Token;
            }
        }

        var url = BuildBaseUrl(credentials.ClientVersion) + "/logon";
        var response = await PostEncryptedFormAsync(
            url,
            credentials,
            timeoutSeconds,
            new Dictionary<string, string?>
            {
                ["account"] = credentials.Account,
                ["password"] = credentials.Password,
                ["udid"] = credentials.Udid.ToUpperInvariant()
            },
            token: null,
            cancellationToken);

        var outerCode = GetIntValue(response, "code") ?? 0;
        if (outerCode != 0)
        {
            throw new HongguoNewApiException(ReadMessage(response, $"Login failed (code={outerCode})"), outerCode, response);
        }

        if (response.TryGetValue("data", out var dataValue) && dataValue is Dictionary<string, object?> data)
        {
            var state = GetStringValue(data, "state");
            if (string.Equals(state, "n", StringComparison.OrdinalIgnoreCase))
            {
                throw new HongguoNewApiException("账号未绑定当前 DeviceUDID，请在红果客户端/服务端重新绑定后再试", 76, response);
            }

            var token = GetStringValue(data, "token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                lock (_tokenGate)
                {
                    _tokenCache[cacheKey] = new HongguoTokenCacheEntry(token, DateTimeOffset.UtcNow.AddHours(1));
                }

                return token;
            }
        }

        throw new HongguoNewApiException("Login response does not contain token.", payload: response);
    }

    private async Task InfoPingAsync(
        HongguoCredentials credentials,
        string token,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var url = BuildBaseUrl(credentials.ClientVersion) + "/info";
        await PostEncryptedFormAsync(
            url,
            credentials,
            timeoutSeconds,
            new Dictionary<string, string?>(),
            token,
            cancellationToken);
    }

    private void InvalidateToken(HongguoCredentials credentials)
    {
        var cacheKey = $"{credentials.Account.Trim()}\n{credentials.Udid.Trim().ToUpperInvariant()}\n{credentials.ClientVersion.Trim()}";
        lock (_tokenGate)
        {
            _tokenCache.Remove(cacheKey);
        }
    }

    private async Task<Dictionary<string, object?>> PostEncryptedFormAsync(
        string url,
        HongguoCredentials credentials,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string?> fields,
        string? token,
        CancellationToken cancellationToken)
    {
        var bodyFields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                bodyFields[pair.Key] = pair.Value!;
            }
        }

        bodyFields["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(token))
        {
            bodyFields.TryAdd("token", token!);
        }

        var plain = BuildSignBaseString(bodyFields);
        var encryptedData = EncryptPlain(plain);
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(plain + AppKey))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyVersionedHeaders(request, credentials.ClientVersion, token);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["data"] = encryptedData,
            ["sign"] = sign
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HongguoNewApiException(
                $"HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}",
                (int)response.StatusCode,
                body);
        }

        return DecryptOuterResponse(body);
    }

    private static string BuildBaseUrl(string clientVersion)
    {
        var version = NormalizeVersion(clientVersion);
        return string.Format(CultureInfo.InvariantCulture, BaseUrlTemplate, version);
    }

    private static void ApplyVersionedHeaders(HttpRequestMessage request, string clientVersion, string? token)
    {
        var version = NormalizeVersion(clientVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", $"HGXZQ-Client/{version} (Windows)");
        request.Headers.TryAddWithoutValidation("X-Client-Version", version);
        request.Headers.TryAddWithoutValidation("X-Client-Name", "HGXZQ");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    private static string NormalizeVersion(string clientVersion)
    {
        var version = string.IsNullOrWhiteSpace(clientVersion) ? DefaultVersion : clientVersion.Trim();
        return CompareVersions(version, MinVersion) < 0 ? MinVersion : version;
    }

    private static string BuildVideoPlaybackParam(string videoId, string quality, string token, string udid)
    {
        var normalizedLevel = NormalizeVideoLevel(quality);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var message = $"level={normalizedLevel}&time={timestamp}&token={token}&udid={udid}&video_id={videoId}";
        var signKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppKey), Encoding.UTF8.GetBytes(token));
        var sign = Convert.ToHexString(HMACSHA256.HashData(signKey, Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

        return JsonSerializer.Serialize(new
        {
            video_id = videoId,
            level = normalizedLevel,
            udid,
            token,
            time = timestamp,
            sign
        });
    }

    private static string NormalizeVideoLevel(string quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "1080p+" or "4k" or "2160" or "2160p" => "2160p",
            "1080p" or "1080" => "1080p",
            "720p" or "720" => "720p",
            "480p" or "480" => "480p",
            "360p" or "360" => "360p",
            _ => "1080p"
        };
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(part => int.TryParse(part, out var parsed) ? parsed : 0).ToArray();
        var rightParts = right.Split('.').Select(part => int.TryParse(part, out var parsed) ? parsed : 0).ToArray();
        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : 0;
            var rightPart = index < rightParts.Length ? rightParts[index] : 0;
            if (leftPart != rightPart)
            {
                return leftPart.CompareTo(rightPart);
            }
        }

        return 0;
    }

    private static string BuildSignBaseString(IReadOnlyDictionary<string, string> fields)
    {
        return string.Join(
            "&",
            fields
                .Where(pair => !string.Equals(pair.Key, "sign", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string EncryptPlain(string plain)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var input = Encoding.UTF8.GetBytes(plain);
        var encrypted = encryptor.TransformFinalBlock(input, 0, input.Length);
        var payload = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, payload, aes.IV.Length, encrypted.Length);
        return Convert.ToBase64String(payload);
    }

    private static Dictionary<string, object?> DecryptOuterResponse(string body)
    {
        var outer = ParseJsonObject(body, "Hongguo outer response");
        if (outer.TryGetValue("data", out var dataValue) &&
            dataValue is string encryptedData &&
            !string.IsNullOrWhiteSpace(encryptedData))
        {
            var decrypted = DecryptData(encryptedData);
            try
            {
                outer["data"] = ParseJsonAny(decrypted, "Hongguo decrypted data");
            }
            catch (JsonException)
            {
                outer["data"] = decrypted;
            }
        }

        return outer;
    }

    private static string DecryptData(string encryptedData)
    {
        var bytes = Convert.FromBase64String(encryptedData);
        if (bytes.Length < 32)
        {
            throw new HongguoNewApiException("Encrypted Hongguo payload is too short.");
        }

        var iv = bytes[..16];
        var cipher = bytes[16..];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static object? ParseJsonAny(string json, string context)
    {
        using var document = JsonDocument.Parse(json);
        return ConvertJsonElement(document.RootElement);
    }

    private static Dictionary<string, object?> ParseJsonObject(string json, string context)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new HongguoNewApiException($"{context} is not a JSON object.");
        }

        return (Dictionary<string, object?>)ConvertJsonElement(document.RootElement)!;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertJsonElement(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDecimal(out var decimalValue)
                    ? decimalValue
                    : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static HongguoCredentials ResolveCredentials(GlobalConfigSnapshot settings)
    {
        var account = (settings.HgnewAccount ?? string.Empty).Trim();
        var password = (settings.HgnewPassword ?? string.Empty).Trim();
        var udid = (settings.HgnewUdid ?? string.Empty).Trim().ToUpperInvariant();
        var clientVersion = NormalizeVersion(settings.HgnewClientVersion);

        var missing = new List<string>();
        if (account.Length == 0) missing.Add("账号");
        if (password.Length == 0) missing.Add("密码");
        if (udid.Length == 0) missing.Add("UDID");
        if (missing.Count > 0)
        {
            throw new HongguoNewApiException($"红果新接口未配置：{string.Join("、", missing)}");
        }

        return new HongguoCredentials(account, password, udid, clientVersion);
    }

    private static void ValidateDailyMode(string mode)
    {
        if (!DailyModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new HongguoNewApiException($"Unsupported Hongguo daily mode: {mode}");
        }
    }

    private static bool ShouldRetryLogin(HongguoNewApiException exception)
    {
        if (LoginRetryCodes.Contains(exception.Code))
        {
            return true;
        }

        var message = (exception.Message ?? string.Empty).Trim().ToLowerInvariant();
        return LoginRetryHints.Any(hint => message.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<Dictionary<string, object?>> SortByPublishTime(IEnumerable<Dictionary<string, object?>> items)
    {
        return items
            .Select(item => new
            {
                Item = item,
                PublishTimestamp = ResolvePublishTimestamp(item)
            })
            .OrderByDescending(pair => pair.PublishTimestamp.HasValue)
            .ThenByDescending(pair => pair.PublishTimestamp ?? long.MinValue)
            .ToList()
            .ConvertAll(pair => pair.Item);
    }

    private static bool MatchesPublishDate(Dictionary<string, object?> item, IReadOnlySet<string> isoDates)
    {
        var timestamp = ResolvePublishTimestamp(item);
        if (!timestamp.HasValue)
        {
            return false;
        }

        try
        {
            var dateText = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return isoDates.Contains(dateText);
        }
        catch
        {
            return false;
        }
    }

    private static long? ResolvePublishTimestamp(Dictionary<string, object?> item)
    {
        var raw = GetStringValue(item, "publish_time");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (long.TryParse(raw, out var numeric))
        {
            return numeric > 1_000_000_000_000 ? numeric / 1000 : numeric;
        }

        foreach (var format in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm" })
        {
            if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return new DateTimeOffset(parsed).ToUnixTimeSeconds();
            }
        }

        return null;
    }

    private static DramaSearchItem MapSearchItem(Dictionary<string, object?> item)
    {
        var bookId = FirstNonEmpty(
            GetStringValue(item, "book_id"),
            GetStringValue(item, "id"));
        var title = FirstNonEmpty(
            GetStringValue(item, "title"),
            GetStringValue(item, "book_name"),
            GetStringValue(item, "name"),
            bookId);
        var category = FirstNonEmpty(
            GetStringValue(item, "type"),
            GetStringValue(item, "tags"));
        var intro = FirstNonEmpty(
            GetStringValue(item, "intro"),
            GetStringValue(item, "description"),
            GetStringValue(item, "desc"));
        var author = FirstNonEmpty(
            GetStringValue(item, "author"),
            GetStringValue(item, "producer"),
            GetStringValue(item, "company"));
        var publishTime = FirstNonEmpty(
            GetStringValue(item, "publish_time"),
            GetStringValue(item, "create_time"),
            GetStringValue(item, "created_at"));

        return new DramaSearchItem(
            BookId: bookId,
            Title: title,
            Category: category,
            EpisodeTotal: ResolveEpisodeTotal(item, category),
            Intro: intro,
            PosterUrl: ExtractPosterUrl(item),
            Author: author,
            PublishTime: publishTime,
            FavoriteCount: GetIntValue(item, "favorite_count")
                ?? GetIntValue(item, "collect_count")
                ?? GetIntValue(item, "favorite")
                ?? 0);
    }

    private static int ResolveEpisodeTotal(Dictionary<string, object?> item, string category)
    {
        foreach (var key in new[] { "episode", "episode_total", "episode_cnt", "ji" })
        {
            var value = GetIntValue(item, key);
            if (value is > 0)
            {
                return value.Value;
            }
        }

        var match = System.Text.RegularExpressions.Regex.Match(category ?? string.Empty, @"(\d+)\s*集");
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 0;
    }

    private static int ExtractEpisodeNumber(string? title, int fallback)
    {
        var digits = new string((title ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string ExtractPosterUrl(object? value)
    {
        if (value is Dictionary<string, object?> objectMap)
        {
            foreach (var key in PosterKeys)
            {
                var candidate = GetStringValue(objectMap, key);
                if (LooksLikeHttpUrl(candidate))
                {
                    return candidate!;
                }
            }

            foreach (var (key, nested) in objectMap)
            {
                if (PosterKeywords.Any(keyword => key.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    var nestedUrl = ExtractPosterUrl(nested);
                    if (nestedUrl.Length > 0)
                    {
                        return nestedUrl;
                    }
                }
            }

            foreach (var nested in objectMap.Values)
            {
                var nestedUrl = ExtractPosterUrl(nested);
                if (nestedUrl.Length > 0)
                {
                    return nestedUrl;
                }
            }
        }
        else if (value is List<object?> list)
        {
            foreach (var nested in list)
            {
                var nestedUrl = ExtractPosterUrl(nested);
                if (nestedUrl.Length > 0)
                {
                    return nestedUrl;
                }
            }
        }
        else if (value is string text && LooksLikeHttpUrl(text))
        {
            return text.Trim();
        }

        return string.Empty;
    }

    private static bool LooksLikeHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetStringValue(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text.Trim(),
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()?.Trim(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()?.Trim()
        };
    }

    private static int? GetIntValue(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            decimal decimalValue when decimalValue is >= int.MinValue and <= int.MaxValue => (int)decimalValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed) => parsed,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static long? GetLongValue(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            decimal decimalValue when decimalValue is >= long.MinValue and <= long.MaxValue => (long)decimalValue,
            string text when long.TryParse(text, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var parsed) => parsed,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static string ReadMessage(IReadOnlyDictionary<string, object?> values, string fallback)
    {
        return FirstNonEmpty(
            GetStringValue(values, "msg"),
            GetStringValue(values, "message"),
            fallback);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private sealed record HongguoCredentials(string Account, string Password, string Udid, string ClientVersion);

    private sealed record HongguoTokenCacheEntry(string Token, DateTimeOffset ExpiresAtUtc);

    public sealed record HongguoEpisodeInfo(int EpisodeNumber, string Title, string VideoId, string PosterUrl);

    public sealed record HongguoVideoPlayback(string Url, long Size);

    private sealed class HongguoNewApiException : Exception
    {
        public HongguoNewApiException(string message, int code = 0, object? payload = null, Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
            Payload = payload;
        }

        public int Code { get; }

        public object? Payload { get; }
    }
}
