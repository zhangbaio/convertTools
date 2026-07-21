using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>红果新接口登录探测（从 Desktop HongguoNewApiService 抽取，供多宿主复用）。</summary>
public static class HongguoNewLoginClient
{
    private const string BaseUrlTemplate = "https://au.s1o.cc/api/user/1000/win/{0}";
    private const string RestApiBase = "http://101.35.49.94/api";
    // AES（default 1.4.x）；>=1.5.0 走 REST JWT（与 weixin-channel-tool hongguo_auth_service 对齐）
    private const string AppKey = "de0852fd2493377d766f27d8a9f686af";
    private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("UMgfJjHhMizNdJGl");
    private const string DefaultVersion = HongguoClientVersion.Default;

    public static async Task<HongguoLoginProbeResult> ProbeLoginAsync(
        HttpClient httpClient,
        string account,
        string password,
        string udid,
        string? clientVersion,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(account, password, udid, clientVersion);
        return await EnsureTokenAsync(httpClient, credentials, timeoutSeconds, cancellationToken);
    }

    private static HongguoCredentials ResolveCredentials(
        string account,
        string password,
        string udid,
        string? clientVersion)
    {
        var normalizedAccount = (account ?? string.Empty).Trim();
        var normalizedPassword = password ?? string.Empty;
        var version = NormalizeVersion(clientVersion);
        var normalizedUdid = HongguoDeviceId.Normalize(udid);
        if (IsRestV15(version))
        {
            // GUID 仅支持 1.4.x；1.5.0 REST 不再把 GUID 自动换成 32hex
            // if (HongguoDeviceId.LooksLikeGuid(normalizedUdid) || string.IsNullOrWhiteSpace(normalizedUdid))
            // {
            //     var registryId = HongguoDeviceId.TryReadFromRegistry(preferAes: false);
            //     if (!string.IsNullOrWhiteSpace(registryId) && HongguoDeviceId.LooksLikeHex32(registryId))
            //     {
            //         normalizedUdid = registryId;
            //     }
            // }
            if (HongguoDeviceId.LooksLikeGuid(normalizedUdid))
            {
                throw new HongguoLoginException(
                    "客户端版本 >=1.5.0 仅支持 HongGuopy 的 32 位设备号，GUID 仅用于 1.4.x；请改版本为 1.4.1 或填写 32hex DeviceId");
            }

            if (string.IsNullOrWhiteSpace(normalizedUdid))
            {
                normalizedUdid = HongguoDeviceId.TryReadFromRegistry(preferAes: false) ?? "";
            }
        }
        else if (string.IsNullOrWhiteSpace(normalizedUdid))
        {
            normalizedUdid = HongguoDeviceId.TryReadFromRegistry(preferAes: true) ?? "";
        }
        // else if (HongguoDeviceId.LooksLikeHex32(normalizedUdid))
        // {
        //     var registryId = HongguoDeviceId.TryReadFromRegistry(preferAes: true);
        //     if (!string.IsNullOrWhiteSpace(registryId) && HongguoDeviceId.LooksLikeGuid(registryId))
        //     {
        //         normalizedUdid = registryId;
        //     }
        // }

        var missing = new List<string>();
        if (normalizedAccount.Length == 0) missing.Add("账号");
        if (normalizedPassword.Length == 0) missing.Add("密码");
        if (normalizedUdid.Length == 0) missing.Add("UDID/DeviceId");
        if (missing.Count > 0)
        {
            throw new HongguoLoginException($"红果新接口未配置：{string.Join("、", missing)}");
        }

        return new HongguoCredentials(normalizedAccount, normalizedPassword, normalizedUdid, version);
    }

    private static async Task<HongguoLoginProbeResult> EnsureTokenAsync(
        HttpClient httpClient,
        HongguoCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (IsRestV15(credentials.ClientVersion))
        {
            return await RestLoginAsync(httpClient, credentials, timeoutSeconds, cancellationToken);
        }

        var url = BuildBaseUrl(credentials.ClientVersion) + "/m4";  // 1.3.9: /logon -> /m4
        var response = await PostEncryptedFormAsync(
            httpClient,
            url,
            credentials,
            timeoutSeconds,
            new Dictionary<string, string?>
            {
                ["account"] = credentials.Account,
                ["password"] = credentials.Password,
                ["udid"] = credentials.Udid
            },
            token: null,
            cancellationToken);

        var outerCode = GetIntValue(response, "code") ?? 0;
        if (outerCode != 0)
        {
            throw new HongguoLoginException(ReadMessage(response, $"Login failed (code={outerCode})"), outerCode);
        }

        if (response.TryGetValue("data", out var dataValue) && dataValue is Dictionary<string, object?> data)
        {
            var state = GetStringValue(data, "state");
            if (string.Equals(state, "n", StringComparison.OrdinalIgnoreCase))
            {
                throw new HongguoLoginException(
                    "账号未绑定当前设备唯一标识，请在红果客户端或服务端重新绑定后再试",
                    76);
            }

            var token = GetStringValue(data, "token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return new HongguoLoginProbeResult(
                    token.Trim(),
                    FirstNonEmpty(ReadDeepString(data, "email", "mail", "account", "username", "userName"), credentials.Account),
                    NormalizeDisplayDate(FirstNonEmpty(
                        ReadDeepString(data, "vipExpDate", "vip_exp_date", "vip_expire_date", "vipExpireDate"),
                        ReadDeepString(data, "vipExpiresAt", "vip_expire_at", "vip_expire_time", "vipEndTime"),
                        ReadDeepString(data, "expireTime", "expiredAt", "expiresAt", "endTime"))));
            }
        }

        throw new HongguoLoginException("Login response does not contain token.");
    }

    private static async Task<Dictionary<string, object?>> PostEncryptedFormAsync(
        HttpClient httpClient,
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

        using var response = await httpClient.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HongguoLoginException(
                $"HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}",
                (int)response.StatusCode);
        }

        return DecryptOuterResponse(body);
    }

    private static string BuildBaseUrl(string clientVersion) =>
        string.Format(CultureInfo.InvariantCulture, BaseUrlTemplate, NormalizeVersion(clientVersion));

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

    private static string NormalizeVersion(string? clientVersion) =>
        HongguoClientVersion.Normalize(clientVersion);

    private static bool IsRestV15(string clientVersion) =>
        HongguoClientVersion.IsRest(clientVersion);

    private static async Task<HongguoLoginProbeResult> RestLoginAsync(
        HttpClient httpClient,
        HongguoCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var url = RestApiBase.TrimEnd('/') + "/User/login";
        var payload = new Dictionary<string, object?>
        {
            ["email"] = credentials.Account,
            ["password"] = credentials.Password,
            ["deviceId"] = credentials.Udid,
            ["deviceInfo"] = $"Windows {Environment.OSVersion.VersionString} | {Environment.MachineName}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var version = credentials.ClientVersion;
        request.Headers.TryAddWithoutValidation("User-Agent", $"HGXZQ-Client/{version} (Windows)");
        request.Headers.TryAddWithoutValidation("X-Client-Name", "HGXZQ");
        request.Headers.TryAddWithoutValidation("X-Client-Version", version);
        request.Headers.TryAddWithoutValidation("X-Device-Id", credentials.Udid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
        using var response = await httpClient.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        var outer = ParseJsonObject(body, "REST login");
        if (outer.TryGetValue("success", out var success) && success is false)
        {
            var message = FirstNonEmpty(GetStringValue(outer, "message"), "登录失败");
            if (message.Contains("当前设备未绑定", StringComparison.Ordinal))
            {
                message = "账号未绑定当前设备唯一标识，请在红果客户端或服务端重新绑定后再试";
            }

            throw new HongguoLoginException(message, (int)response.StatusCode);
        }

        if (outer.TryGetValue("data", out var dataValue) && dataValue is Dictionary<string, object?> data)
        {
            var token = FirstNonEmpty(GetStringValue(data, "accessToken"), GetStringValue(data, "token"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                return new HongguoLoginProbeResult(
                    token.Trim(),
                    FirstNonEmpty(GetStringValue(data, "email"), credentials.Account),
                    NormalizeDisplayDate(GetStringValue(data, "memberEndDate")));
            }
        }

        throw new HongguoLoginException("Login response does not contain accessToken.");
    }

    private static string BuildSignBaseString(IReadOnlyDictionary<string, string> fields) =>
        string.Join(
            "&",
            fields
                .Where(pair => !string.Equals(pair.Key, "sign", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

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
            throw new HongguoLoginException("Encrypted Hongguo payload is too short.");
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
            throw new HongguoLoginException($"{context} is not a JSON object.");
        }

        return (Dictionary<string, object?>)ConvertJsonElement(document.RootElement)!;
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
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

    private static string ReadMessage(Dictionary<string, object?> payload, string fallback)
    {
        var message = GetStringValue(payload, "msg");
        if (string.IsNullOrWhiteSpace(message))
        {
            message = GetStringValue(payload, "message");
        }

        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    private static string? GetStringValue(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            _ => value.ToString()
        };
    }

    private static string ReadDeepString(object? value, params string[] keys)
    {
        var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        return ReadDeepString(value, keySet);
    }

    private static string ReadDeepString(object? value, IReadOnlySet<string> keys)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case IReadOnlyDictionary<string, object?> dictionary:
            {
                foreach (var (key, nested) in dictionary)
                {
                    if (keys.Contains(key))
                    {
                        var direct = FormatProbeValue(nested);
                        if (!string.IsNullOrWhiteSpace(direct))
                        {
                            return direct;
                        }
                    }
                }

                foreach (var nested in dictionary.Values)
                {
                    var found = ReadDeepString(nested, keys);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }

                return string.Empty;
            }
            case IEnumerable<object?> list:
                foreach (var nested in list)
                {
                    var found = ReadDeepString(nested, keys);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string FormatProbeValue(object? value) =>
        value switch
        {
            null => string.Empty,
            string text => text.Trim(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
            _ => value.ToString()?.Trim() ?? string.Empty
        };

    private static string NormalizeDisplayDate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            try
            {
                var seconds = numeric > 1_000_000_000_000 ? numeric / 1000 : numeric;
                return DateTimeOffset.FromUnixTimeSeconds(seconds)
                    .LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return text;
            }
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static int? GetIntValue(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long longNumber => (int)longNumber,
            double doubleNumber => (int)doubleNumber,
            decimal decimalNumber => (int)decimalNumber,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record HongguoCredentials(string Account, string Password, string Udid, string ClientVersion);
}

public sealed record HongguoLoginProbeResult(string Token, string Email, string VipExpiresAt);

public sealed class HongguoLoginException : Exception
{
    public int Code { get; }

    public HongguoLoginException(string message, int code = 0) : base(message) => Code = code;
}
