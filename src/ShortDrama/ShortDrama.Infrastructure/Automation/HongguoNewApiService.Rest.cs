using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>HG Downloader REST（>=1.5.0，明文 JSON + JWT）；AES 默认见 HongguoClientVersion。</summary>
public sealed partial class HongguoNewApiService
{
    private async Task<IReadOnlyList<Dictionary<string, object?>>> RestSearchItemsAsync(
        HongguoCredentials credentials,
        string keyword,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var inner = await RestCallWithRetryAsync(
            HttpMethod.Post,
            "/ThirdParty/unified-search",
            credentials,
            new Dictionary<string, object?>
            {
                ["query"] = keyword,
                ["offset"] = 0,
                ["searchId"] = "",
                ["passback"] = "",
                ["pointsRequired"] = 1
            },
            timeoutSeconds,
            unwrapRaw: true,
            cancellationToken);

        return ExtractInnerItemList(inner, "搜索");
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RestLatestItemsAsync(
        HongguoCredentials credentials,
        string mode,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!RestDailyActionMap.TryGetValue(mode, out var action))
        {
            throw new HongguoNewApiException($"Unsupported Hongguo daily mode: {mode}");
        }

        var inner = await RestCallWithRetryAsync(
            HttpMethod.Post,
            "/ThirdParty/latest",
            credentials,
            new Dictionary<string, object?>
            {
                ["page"] = 1,
                ["action"] = action,
                ["pointsRequired"] = 1
            },
            timeoutSeconds,
            unwrapRaw: true,
            cancellationToken);

        if (inner is Dictionary<string, object?> dict)
        {
            var warming = dict.TryGetValue("warming", out var warmingValue) && warmingValue is true;
            var ready = !dict.TryGetValue("ready", out var readyValue) || readyValue is not false;
            if (warming && !ready)
            {
                throw new HongguoNewApiException(
                    FirstNonEmpty(GetStringValue(dict, "message"), "数据预热中，请稍后重试"),
                    payload: dict);
            }
        }

        return ExtractInnerItemList(inner, "latest");
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RestVideoListItemsAsync(
        HongguoCredentials credentials,
        string bookId,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var inner = await RestCallWithRetryAsync(
            HttpMethod.Post,
            "/ThirdParty/videolist",
            credentials,
            new Dictionary<string, object?>
            {
                ["bookId"] = bookId,
                ["pointsRequired"] = 1
            },
            timeoutSeconds,
            unwrapRaw: true,
            cancellationToken);

        return ExtractInnerItemList(inner, "videolist");
    }

    private async Task<HongguoVideoPlayback> RestVideoParseAsync(
        HongguoCredentials credentials,
        string videoId,
        string quality,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var level = NormalizeVideoLevel(quality);
        var inner = await RestCallWithRetryAsync(
            HttpMethod.Post,
            "/ThirdParty/videoparse",
            credentials,
            new Dictionary<string, object?>
            {
                ["videoId"] = videoId,
                ["level"] = level,
                ["pointsRequired"] = 1
            },
            timeoutSeconds,
            unwrapRaw: true,
            cancellationToken);

        if (inner is not Dictionary<string, object?> dict)
        {
            throw new HongguoNewApiException("videoparse 响应格式异常", payload: inner);
        }

        EnsureInnerSuccess(dict, "videoparse");
        var url = FirstNonEmpty(
            GetStringValue(dict, "url"),
            dict.TryGetValue("data", out var dataValue) && dataValue is Dictionary<string, object?> data
                ? GetStringValue(data, "url")
                : null);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new HongguoNewApiException("接口响应缺少视频直链。", payload: dict);
        }

        long size = 0;
        if (dict.TryGetValue("data", out var detailValue) &&
            detailValue is Dictionary<string, object?> detail &&
            detail.TryGetValue("info", out var infoValue) &&
            infoValue is Dictionary<string, object?> info)
        {
            size = ParseSizeToBytes(info.TryGetValue("size", out var sizeValue) ? sizeValue : null);
        }

        return new HongguoVideoPlayback(url, size);
    }

    private async Task<(string Token, int ExpiresIn)> RestLoginAsync(
        HongguoCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var deviceId = credentials.Udid;
        if (HongguoDeviceId.LooksLikeGuid(deviceId))
        {
            var registryId = HongguoDeviceId.TryReadFromRegistry();
            if (!string.IsNullOrWhiteSpace(registryId) && HongguoDeviceId.LooksLikeHex32(registryId))
            {
                deviceId = registryId;
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["email"] = credentials.Account,
            ["password"] = credentials.Password,
            ["deviceId"] = deviceId,
            ["deviceInfo"] = BuildRestDeviceInfo()
        };

        var data = await RestRequestAsync(
            HttpMethod.Post,
            "/User/login",
            deviceId,
            credentials.ClientVersion,
            token: null,
            body,
            timeoutSeconds,
            unwrapRaw: false,
            cancellationToken);

        if (data is not Dictionary<string, object?> dict)
        {
            throw new HongguoNewApiException("登录响应格式异常：data 非对象", payload: data);
        }

        var access = FirstNonEmpty(GetStringValue(dict, "accessToken"), GetStringValue(dict, "token"));
        if (string.IsNullOrWhiteSpace(access))
        {
            throw new HongguoNewApiException("登录响应中未找到 accessToken", payload: dict);
        }

        var expiresIn = GetIntValue(dict, "expiresIn") ?? 3600;
        return (access, expiresIn);
    }

    private async Task<object?> RestCallWithRetryAsync(
        HttpMethod method,
        string path,
        HongguoCredentials credentials,
        Dictionary<string, object?>? body,
        int timeoutSeconds,
        bool unwrapRaw,
        CancellationToken cancellationToken)
    {
        var token = await EnsureTokenAsync(credentials, timeoutSeconds, cancellationToken);
        try
        {
            return await RestRequestAsync(
                method,
                path,
                credentials.Udid,
                credentials.ClientVersion,
                token,
                body,
                timeoutSeconds,
                unwrapRaw,
                cancellationToken);
        }
        catch (HongguoNewApiException ex) when (ShouldRetryLogin(ex))
        {
            InvalidateToken(credentials);
            token = await EnsureTokenAsync(credentials, timeoutSeconds, cancellationToken);
            return await RestRequestAsync(
                method,
                path,
                credentials.Udid,
                credentials.ClientVersion,
                token,
                body,
                timeoutSeconds,
                unwrapRaw,
                cancellationToken);
        }
    }

    private async Task<object?> RestRequestAsync(
        HttpMethod method,
        string path,
        string deviceId,
        string clientVersion,
        string? token,
        Dictionary<string, object?>? body,
        int timeoutSeconds,
        bool unwrapRaw,
        CancellationToken cancellationToken)
    {
        var url = RestApiBase.TrimEnd('/') + "/" + path.TrimStart('/');
        using var request = new HttpRequestMessage(method, url);
        ApplyRestHeaders(request, deviceId, clientVersion, token);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HongguoNewApiException($"红果新接口网络异常：{ex.Message}", innerException: ex);
        }

        using (response)
        {
            var text = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (text.Length == 0)
            {
                throw new HongguoNewApiException(
                    $"红果新接口响应为空（HTTP {(int)response.StatusCode}）",
                    (int)response.StatusCode);
            }

            Dictionary<string, object?> outer;
            try
            {
                outer = ParseJsonObject(text, "REST response");
            }
            catch (HongguoNewApiException)
            {
                throw new HongguoNewApiException(
                    $"红果新接口返回了非 JSON 响应：{text[..Math.Min(200, text.Length)]}",
                    (int)response.StatusCode);
            }

            if (outer.ContainsKey("errors") && !outer.ContainsKey("success"))
            {
                var detail = outer.TryGetValue("errors", out var errorsValue)
                    ? errorsValue?.ToString() ?? ""
                    : "";
                throw new HongguoNewApiException(
                    string.IsNullOrWhiteSpace(detail)
                        ? FirstNonEmpty(GetStringValue(outer, "title"), $"请求校验失败（HTTP {(int)response.StatusCode}）")
                        : detail,
                    GetIntValue(outer, "status") ?? (int)response.StatusCode,
                    outer);
            }

            if (outer.TryGetValue("success", out var successValue) && successValue is false)
            {
                var message = FirstNonEmpty(GetStringValue(outer, "message"), "请求失败");
                if (message.Contains("当前设备未绑定", StringComparison.Ordinal) ||
                    message.Contains("机器码", StringComparison.Ordinal))
                {
                    message = "账号未绑定当前设备唯一标识，请在红果客户端或服务端重新绑定后再试";
                }

                throw new HongguoNewApiException(message, (int)response.StatusCode, outer);
            }

            if (!response.IsSuccessStatusCode &&
                !(outer.TryGetValue("success", out var ok) && ok is true))
            {
                throw new HongguoNewApiException(
                    $"HTTP {(int)response.StatusCode}：{FirstNonEmpty(GetStringValue(outer, "message"), text[..Math.Min(200, text.Length)])}",
                    (int)response.StatusCode,
                    outer);
            }

            if (!outer.TryGetValue("data", out var data))
            {
                return null;
            }

            return unwrapRaw ? UnwrapRestRawData(data) : data;
        }
    }

    private static object? UnwrapRestRawData(object? data)
    {
        if (data is not Dictionary<string, object?> dict)
        {
            return data;
        }

        var raw = GetStringValue(dict, "rawData");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return data;
        }

        try
        {
            return ParseJsonObject(raw, "REST rawData");
        }
        catch (Exception ex)
        {
            // rawData 可能是数组包装在其它形态；再尝试通用 JSON
            try
            {
                using var document = JsonDocument.Parse(raw);
                return ConvertJsonElement(document.RootElement);
            }
            catch (Exception inner)
            {
                throw new HongguoNewApiException($"REST rawData 解析失败：{inner.Message}", payload: data, innerException: ex);
            }
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> ExtractInnerItemList(object? inner, string context)
    {
        if (inner is not Dictionary<string, object?> dict)
        {
            throw new HongguoNewApiException($"{context} 响应格式异常", payload: inner);
        }

        EnsureInnerSuccess(dict, context);
        if (!dict.TryGetValue("data", out var dataValue) || dataValue is not List<object?> list)
        {
            throw new HongguoNewApiException($"{context} 响应格式异常：data 不是数组", payload: dict);
        }

        return list.OfType<Dictionary<string, object?>>().ToArray();
    }

    private static void EnsureInnerSuccess(Dictionary<string, object?> dict, string context)
    {
        var code = GetIntValue(dict, "code") ?? 0;
        if (code != 0 && code != 200)
        {
            throw new HongguoNewApiException(
                FirstNonEmpty(
                    GetStringValue(dict, "msg"),
                    GetStringValue(dict, "message"),
                    $"{context} 失败 code={code}"),
                code,
                dict);
        }
    }

    private static void ApplyRestHeaders(
        HttpRequestMessage request,
        string deviceId,
        string clientVersion,
        string? token)
    {
        var version = string.IsNullOrWhiteSpace(clientVersion) ? DefaultVersion : clientVersion.Trim();
        request.Headers.TryAddWithoutValidation("User-Agent", $"HGXZQ-Client/{version} (Windows)");
        request.Headers.TryAddWithoutValidation("X-Client-Name", "HGXZQ");
        request.Headers.TryAddWithoutValidation("X-Client-Version", version);
        request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    private static string BuildRestDeviceInfo()
    {
        try
        {
            return $"Windows {Environment.OSVersion.VersionString} | {Environment.MachineName}";
        }
        catch
        {
            return "Windows | unknown";
        }
    }

    private static long ParseSizeToBytes(object? value)
    {
        switch (value)
        {
            case null:
                return 0;
            case long longValue:
                return longValue;
            case int intValue:
                return intValue;
            case double doubleValue:
                return (long)doubleValue;
            case decimal decimalValue:
                return (long)decimalValue;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant().Replace(" ", "") ?? "";
        if (text.Length == 0)
        {
            return 0;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
        {
            return plain;
        }

        var match = Regex.Match(text, @"^([0-9]*\.?[0-9]+)([KMGT]?B)$");
        if (!match.Success)
        {
            return 0;
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return 0;
        }

        var mult = match.Groups[2].Value switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L
        };
        return (long)(num * mult);
    }
}
