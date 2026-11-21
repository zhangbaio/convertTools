using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed partial class WeixinSeriesApiClient
{
    private async Task UploadObjectAsync(WeixinSeriesApiFile file, JsonElement upload, CancellationToken ct)
    {
        var region = upload.Text("storageRegionV5");
        var bucket = upload.Text("storageBucket");
        var appId = upload.Text("storageAppId");
        var path = upload.Get("video").Text("storagePath");
        if (!Regex.IsMatch(region, @"\A[a-z0-9-]+\z") || !Regex.IsMatch(bucket, @"\A[a-z0-9-]+\z") ||
            !Regex.IsMatch(appId, @"\A[0-9]+\z") || !path.StartsWith('/') ||
            path[1..].Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("云存储返回了无效的上传目标。");

        if (!bucket.EndsWith("-" + appId, StringComparison.Ordinal))
            bucket += "-" + appId;
        var host = $"{bucket}.cos.{region}.myqcloud.com";
        var credentials = upload.Get("tempCertificate");
        var id = RequiredText(credentials, "secretId", "缺少云存储临时凭据。");
        var key = RequiredText(credentials, "secretKey", "缺少云存储临时凭据。");
        var token = RequiredText(credentials, "token", "缺少云存储临时凭据。");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!long.TryParse(credentials.Text("expiredTime"), NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) || expiry <= now)
            throw new InvalidOperationException("云存储临时凭据已过期。");

        using var stream = OpenFile(file);
        var escapedPath = string.Join("/", path.Split('/').Select(Escape));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://{host}{escapedPath}")
        {
            Content = new StreamContent(stream, 1024 * 1024),
        };
        request.Content.Headers.ContentLength = file.Length;
        var period = FormattableString.Invariant($"{now - 30};{expiry}");
        request.Headers.TryAddWithoutValidation("Authorization", SignCos(host, path, id, key, period));
        request.Headers.TryAddWithoutValidation("x-cos-security-token", token);
        using var response = await SendAsync(cloud, request, "写入云存储视频", VideoTransferTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(response, "写入云存储视频");
        if (!response.Headers.Contains("ETag"))
            throw new InvalidOperationException("云存储未返回视频写入确认。");
    }

    internal static string SignCos(string host, string path, string id, string key, string period)
    {
        static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
        static string Hmac(string secret, string message) =>
            Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));

        var signingKey = Hmac(key, period);
        var requestHash = Hex(SHA1.HashData(Encoding.UTF8.GetBytes($"put\n{path}\n\nhost={host}\n")));
        var signature = Hmac(signingKey, $"sha1\n{period}\n{requestHash}\n");
        return $"q-sign-algorithm=sha1&q-ak={id}&q-sign-time={period}&q-key-time={period}&q-header-list=host&q-url-param-list=&q-signature={signature}";
    }

    private async Task<JsonElement> AuditAsync(string action, object payload, string operation, CancellationToken ct)
    {
        using var request = WeixinPost("playletAudit/" + action, payload);
        var result = await ReadJsonAsync(weixin, request, operation, ct).ConfigureAwait(false);
        var root = result.Get("baseResp");
        if (root.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new InvalidOperationException($"{operation}返回格式异常。");
        var node = result.Get("retInNode");
        var code = root.Get("errCode");
        foreach (var status in new[] { node, code })
        {
            if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var error) && error != 0)
            {
                var detail = action == "checkDramaName" && error == 10090023 ? "剧目名称已存在" : ErrorMessage(result);
                throw Reject($"{operation}失败（{error}）{(detail.Length == 0 ? "" : $"：{detail}")}");
            }
        }
        if (!IsStatus(node) || !IsStatus(code) || (!IsZero(node) && !(action == "checkDramaName" && IsZero(code))))
            throw new InvalidOperationException($"{operation}缺少可确认的成功状态。");
        return result;
    }

    private static bool IsStatus(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
    private static bool IsZero(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var code) && code == 0;

    private static void EnsureAuditMessage(JsonElement result, string operation)
    {
        var detail = ErrorMessage(result);
        if (detail.Length > 0 && !detail.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
            !detail.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw Reject($"{operation}失败：{detail}");
    }

    private static InvalidOperationException Reject(string message) =>
        message.Contains("额度已达上限", StringComparison.Ordinal) || message.Contains("今日额度已用完", StringComparison.Ordinal)
            ? new WeixinSeriesApiQuotaExceededException(message)
            : new WeixinSeriesApiRejectedException(message);

    private static string ErrorMessage(JsonElement result)
    {
        var message = result.Get("baseResp").Text("errMsg");
        if (message.Length == 0)
            message = result.Text("errMsg");
        if (message.Length == 0)
            message = result.Text("msg");
        return message[..Math.Min(message.Length, 160)].Replace('\r', ' ').Replace('\n', ' ');
    }

    private static bool HasInvalidMedia(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(item => HasInvalidMedia(item.Value)),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => value.AsText().Length > 0,
        JsonValueKind.Number => !IsZero(value),
        _ => true,
    };

    private async Task<JsonElement> RequireCodeAsync(HttpClient client, HttpRequestMessage request, string operation,
        string codeField, CancellationToken ct)
    {
        var result = await ReadJsonAsync(client, request, operation, ct).ConfigureAwait(false);
        var code = codeField == "base_resp.ret" ? result.Get("base_resp").Get("ret") : result.Get(codeField);
        if (!IsZero(code))
        {
            var status = code.ValueKind == JsonValueKind.Number ? code.AsText() : "缺少成功状态";
            throw new InvalidOperationException($"{operation}失败（{status}）。");
        }
        return result;
    }

    private async Task<JsonElement> ReadJsonAsync(HttpClient client, HttpRequestMessage request, string operation,
        CancellationToken ct)
    {
        using var response = await SendAsync(client, request, operation, RequestTimeout, ct, HttpCompletionOption.ResponseContentRead)
            .ConfigureAwait(false);
        EnsureSuccess(response, operation);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document.RootElement.Clone();
        }
        catch (JsonException) { }
        throw new InvalidOperationException($"{operation}返回格式异常。");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string operation,
        TimeSpan timeout, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        request.Headers.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(userAgent) || userAgent.Contains('\r') || userAgent.Contains('\n') ? DefaultAgent : userAgent);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await client.SendAsync(request, completion, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{operation}超时。");
        }
        catch (HttpRequestException)
        {
            throw new IOException($"{operation}时网络连接失败。");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation}失败（HTTP {(int)response.StatusCode}）。");
    }

    private static HttpRequestMessage WeixinRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("Origin", Channels);
        request.Headers.Referrer = new Uri(Channels + "/");
        return request;
    }

    private static HttpRequestMessage WeixinPost(string path, object payload)
    {
        var json = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return WeixinRequest(HttpMethod.Post, $"{Mp}/webpocnew/cgi/{path}?lang=zh_CN", json);
    }

    private static string RequiredText(JsonElement data, string field, string message) =>
        data.Text(field) is { Length: > 0 } value ? value : throw new InvalidOperationException(message);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
