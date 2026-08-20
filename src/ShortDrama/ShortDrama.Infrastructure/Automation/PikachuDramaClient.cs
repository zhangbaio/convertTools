using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>皮卡丘链路搜索与连通性测试。</summary>
public static class PikachuDramaClient
{
    private const string FanqieSearchUrl =
        "https://api5-sinfonlinea.novelfm.com/novelfm/bookmall/search/page/v1/?device_platform=android&aid=3040&manifest_version_code=628&update_version_code=62832";
    private const string DetailPath = "/api/drama/hongguo/detail";
    private const string VideoPath = "/api/drama/hongguo/decryptVideo";
    private const string TestBookId = "7599558182226119705";
    private const string PikachuPassId = "start-prod-api";
    private const string PikachuPassToken = "MkYQyRrrD2iG5WuDEV7DjYcq2jq7";
    private const string DefaultServerUrl = "https://startvlog.cn/start-prod-api";
    private const string DefaultClientVersion = "1.4.4";
    private const string FanqieUserAgent =
        "com.xs.fm/576 (Linux; U; Android 9; zh_CN; BVL-AN16; Build/PQ3B.190801.11191547;tt-ok/3.12.13.4-tiktok)";
    private const string PikachuPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC/EwSZCZTwnYhixLefB9Gvfa+X
        o4uMnG35UiNdPd20/CpgMjw0a9Zy79WjvMH4oCRCOL81HMy5/o6Iuks5Nj4t0reN
        KMHkDcrZdIgMW+DFaioJWEi4zfORC0amtHuDEMYaxfVQ1PxOfgnApbD+/3qzd4hr
        4AzoGhyxwpyUXtX6wQIDAQAB
        -----END PUBLIC KEY-----
        """;

    public sealed record ConnectivityResult(
        bool SearchOk,
        bool DetailOk,
        string SearchMessage,
        string DetailMessage);

    public static async Task<int> ProbeSearchCountAsync(
        HttpClient httpClient,
        string fanqieCookie,
        string dramaType,
        CancellationToken cancellationToken)
    {
        var items = await SearchAsync(httpClient, fanqieCookie, dramaType, "测试", 1, cancellationToken);
        return items.Count;
    }

    public static async Task<ConnectivityResult> TestConnectivityAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? fanqieCookie,
        string dramaType = "short",
        string? deviceId = null,
        string? clientVersion = null,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        var cookie = (fanqieCookie ?? string.Empty).Trim();
        var normalizedServer = NormalizeServerUrl(serverUrl);

        string searchMessage;
        var searchOk = false;
        var normalizedDramaType = string.Equals(dramaType?.Trim(), "manga", StringComparison.OrdinalIgnoreCase)
            ? "manga"
            : "short";
        try
        {
            var count = await SearchAsync(httpClient, cookie, normalizedDramaType, "财神", 1, timeoutCts.Token);
            searchOk = true;
            searchMessage = $"{(normalizedDramaType == "manga" ? "红果漫剧" : "红果短剧")}搜索正常，返回 {count} 条结果";
        }
        catch (Exception ex)
        {
            searchMessage = $"{(normalizedDramaType == "manga" ? "红果漫剧" : "红果短剧")}搜索失败：{ex.Message}";
        }

        string detailMessage;
        var detailOk = false;
        try
        {
            var detailProbe = await ProbeDetailAsync(
                httpClient,
                normalizedServer,
                TestBookId,
                timeoutCts.Token);
            detailOk = true;
            detailMessage = $"皮卡丘服务器正常，测试剧返回 {detailProbe.EpisodeCount} 集";

            var resolvedDeviceId = (deviceId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(detailProbe.FirstVideoId) && !string.IsNullOrWhiteSpace(resolvedDeviceId))
            {
                try
                {
                    await ProbeVideoAsync(
                        httpClient,
                        normalizedServer,
                        detailProbe.FirstVideoId,
                        resolvedDeviceId,
                        string.IsNullOrWhiteSpace(clientVersion) ? DefaultClientVersion : clientVersion.Trim(),
                        timeoutCts.Token);
                    detailMessage += "；video 直链正常";
                }
                catch (Exception ex)
                {
                    detailOk = false;
                    detailMessage += $"；video 失败：{ex.Message}";
                }
            }
            else if (!string.IsNullOrWhiteSpace(detailProbe.FirstVideoId))
            {
                detailMessage += "；未配置 DeviceId，未测试 video";
            }
        }
        catch (Exception ex)
        {
            detailMessage = $"皮卡丘服务器失败：{ex.Message}";
        }

        return new ConnectivityResult(searchOk, detailOk, searchMessage, detailMessage);
    }

    private static async Task<DetailProbeResult> ProbeDetailAsync(
        HttpClient httpClient,
        string serverUrl,
        string bookId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bookId"] = Encrypt(bookId)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}{DetailPath}")
        {
            Content = content
        };
        ApplyPikachuHeaders(request);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!string.Equals(GetString(document.RootElement, "code"), "200", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"皮卡丘 detail 失败: {GetString(document.RootElement, "msg") ?? "unknown"}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var episodeList) ||
            episodeList.ValueKind != JsonValueKind.Array)
        {
            return new DetailProbeResult(0, null);
        }

        string? firstVideoId = null;
        foreach (var episode in episodeList.EnumerateArray())
        {
            firstVideoId = GetString(episode, "videoId") ?? GetString(episode, "video_id");
            if (!string.IsNullOrWhiteSpace(firstVideoId))
            {
                break;
            }
        }

        return new DetailProbeResult(episodeList.GetArrayLength(), firstVideoId);
    }

    private static async Task ProbeVideoAsync(
        HttpClient httpClient,
        string serverUrl,
        string videoId,
        string deviceId,
        string clientVersion,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["videoId"] = Encrypt(videoId),
            ["quality"] = Encrypt("1080"),
            ["deviceId"] = Encrypt(deviceId),
            ["version"] = Encrypt(clientVersion)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}{VideoPath}")
        {
            Content = content
        };
        ApplyPikachuHeaders(request);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!string.Equals(GetString(document.RootElement, "code"), "200", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"皮卡丘 video 失败: {GetString(document.RootElement, "msg") ?? "unknown"}");
        }

        var url = document.RootElement.TryGetProperty("data", out var data)
            ? GetString(data, "url")
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("皮卡丘 video 未返回可用播放链接。");
        }
    }

    private static async Task<IReadOnlyList<int>> SearchAsync(
        HttpClient httpClient,
        string fanqieCookie,
        string dramaType,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        using var document = await RequestFanqieSearchAsync(
            httpClient,
            fanqieCookie,
            dramaType,
            keyword,
            page,
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("search_data", out var searchData) ||
            searchData.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var count = 0;
        foreach (var item in searchData.EnumerateArray())
        {
            if (item.TryGetProperty("books", out var books) && books.ValueKind == JsonValueKind.Array)
            {
                count += books.GetArrayLength();
            }

            if (item.TryGetProperty("cell_slices", out var cells) && cells.ValueKind == JsonValueKind.Array)
            {
                count += cells.GetArrayLength();
            }
        }

        return Enumerable.Range(0, count).ToArray();
    }

    internal static async Task<JsonDocument> RequestFanqieSearchAsync(
        HttpClient httpClient,
        string? fanqieCookie,
        string dramaType,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var mangaSearch = string.Equals(dramaType?.Trim(), "manga", StringComparison.OrdinalIgnoreCase);
        var normalizedCookie = HongguoMemoryReaderService.NormalizeFanqieCookie(fanqieCookie);
        if (!mangaSearch && string.IsNullOrWhiteSpace(normalizedCookie))
        {
            throw new InvalidOperationException(
                "皮卡丘搜索 Cookie 无效，请在系统设置中点击“从红果读取”后保存设置。");
        }

        var searchCtx = JsonSerializer.Serialize(new
        {
            type = 1,
            tab_type = 39,
            default_tab_type = 10,
            bottom_type = 1,
            search_tab_id = mangaSearch ? 13 : 10
        });

        var pageIndex = Math.Max(0, page - 1);
        var form = mangaSearch
            ? new Dictionary<string, string>
            {
                ["limit"] = "10",
                ["offset"] = (pageIndex * 10).ToString(CultureInfo.InvariantCulture),
                ["query"] = (keyword ?? string.Empty).Trim(),
                ["search_ctx_info"] = "",
                ["search_entrance"] = searchCtx,
                ["search_id"] = "",
                ["sub_tab_type"] = "31",
                ["tab_type"] = "13"
            }
            : new Dictionary<string, string>
            {
                ["limit"] = "20",
                ["offset"] = (pageIndex * 20).ToString(CultureInfo.InvariantCulture),
                ["query"] = (keyword ?? string.Empty).Trim(),
                ["search_ctx_info"] = searchCtx
            };
        using var content = new FormUrlEncodedContent(form);
        if (mangaSearch)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "utf-8"
            };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, FanqieSearchUrl)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.Host = "api5-sinfonlinea.novelfm.com";
        request.Headers.TryAddWithoutValidation("user-agent", FanqieUserAgent);
        if (!mangaSearch)
        {
            request.Headers.TryAddWithoutValidation("cookie", normalizedCookie);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"皮卡丘搜索请求失败: HTTP {(int)response.StatusCode} {response.ReasonPhrase}。" +
                (mangaSearch ? string.Empty : "请在系统设置中重新读取红果 Cookie。"));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("皮卡丘搜索返回了无法解析的数据。", ex);
        }

        if (GetInt(document.RootElement, "code") != 0)
        {
            var code = GetString(document.RootElement, "code") ?? "unknown";
            var message = GetString(document.RootElement, "message") ??
                          GetString(document.RootElement, "msg") ??
                          "unknown";
            document.Dispose();
            throw new InvalidOperationException(
                $"皮卡丘搜索失败: code={code}, {message}" +
                (mangaSearch ? "。" : "（Cookie 可能已过期，请重新读取）。"));
        }

        return document;
    }

    private static string NormalizeServerUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultServerUrl : normalized;
    }

    private static void ApplyPikachuHeaders(HttpRequestMessage request)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var raw = $"{today}_{PikachuPassId}_{PikachuPassToken}";
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        request.Headers.TryAddWithoutValidation("auth-pass-id", PikachuPassId);
        request.Headers.TryAddWithoutValidation("auth-signature", signature);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    private static string Encrypt(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PikachuPublicKey);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.Pkcs1));
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

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record DetailProbeResult(int EpisodeCount, string? FirstVideoId);
}
