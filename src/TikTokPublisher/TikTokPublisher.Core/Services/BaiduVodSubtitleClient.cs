using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

internal sealed class BaiduVodSubtitleClient
{
    private const string Host = "vod.bj.baidubce.com";
    private const int SignatureExpirationSeconds = 1800;
    private readonly ClientSettings _settings;
    private readonly HttpClient _http;

    public BaiduVodSubtitleClient(ClientSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromHours(2) };
    }

    public async Task<string> UploadMediaAsync(string filePath, Action<double>? progress, CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) extension = "mp4";
        var apply = await SendAsync(HttpMethod.Post, "/v2/medias/upload", null, new JsonObject
        {
            ["name"] = Path.GetFileNameWithoutExtension(filePath),
            ["container"] = extension,
            ["isMultipartUpload"] = false,
        }, ct).ConfigureAwait(false);
        var sessionKey = RequiredString(apply, "sessionKey");
        var uploadUrl = apply["urls"]?[0]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(uploadUrl)) throw ApiError(apply, "百度 VOD 申请上传未返回上传地址");

        progress?.Invoke(0);
        await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl))
        {
            request.Content = new StreamContent(stream, 1024 * 1024);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(extension));
            request.Content.Headers.ContentLength = stream.Length;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"上传视频到百度 VOD 失败：{(int)response.StatusCode} {responseText}");
        }
        progress?.Invoke(1);

        var completed = await SendAsync(HttpMethod.Post, "/v2/medias/complete_upload", null, new JsonObject
        {
            ["sessionKey"] = sessionKey,
            ["isMultipartUpload"] = false,
        }, ct).ConfigureAwait(false);
        return RequiredString(completed, "mediaId");
    }

    public async Task<string> StartSubtitleRemovalAsync(string mediaId, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, "/v2/desubtitle", null, new JsonObject
        {
            ["mediaId"] = mediaId,
            ["modelType"] = _settings.BaiduSubtitleModelType,
            ["subtitleType"] = _settings.BaiduSubtitleType,
        }, ct).ConfigureAwait(false);
        return RequiredString(response, "taskId");
    }

    public async Task<BaiduSubtitleResult> GetSubtitleRemovalAsync(string taskId, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, "/v2/desubtitle/list",
            new Dictionary<string, string> { ["taskId"] = taskId }, null, ct).ConfigureAwait(false);
        var item = response["data"]?[0] as JsonObject;
        if (item is null) throw ApiError(response, $"百度字幕擦除任务 {taskId} 不存在");
        return new BaiduSubtitleResult(
            item["status"]?.GetValue<string>() ?? "",
            item["targetUrl"]?.GetValue<string>() ?? "",
            item["errorMessage"]?.GetValue<string>() ?? item["message"]?.GetValue<string>() ?? "");
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string>? query, JsonObject? body, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.BaiduSubtitleEndpoint)
            ? $"https://{Host}"
            : _settings.BaiduSubtitleEndpoint.TrimEnd('/');
        var canonicalQuery = CanonicalQuery(query);
        var url = endpoint + path + (canonicalQuery.Length == 0 ? "" : "?" + canonicalQuery);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var endpointHost = new Uri(endpoint).Host;
        var authorization = GenerateAuthorization(method.Method, path, query, endpointHost,
            _settings.BaiduSubtitleAccessKeyId, _settings.BaiduSubtitleSecretAccessKey, timestamp);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-bce-date", timestamp);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-bce-request-id", Guid.NewGuid().ToString());
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject json;
        try { json = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? new JsonObject(); }
        catch { json = new JsonObject { ["raw"] = text }; }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"百度 VOD 请求失败：{(int)response.StatusCode} {ApiMessage(json)}");
        return json;
    }

    internal static string GenerateAuthorizationForTest(string method, string path,
        IReadOnlyDictionary<string, string>? query, string accessKey, string secretKey, string timestamp) =>
        GenerateAuthorization(method, path, query, Host, accessKey, secretKey, timestamp);

    private static string GenerateAuthorization(string method, string path,
        IReadOnlyDictionary<string, string>? query, string host, string accessKey, string secretKey, string timestamp)
    {
        var prefix = $"bce-auth-v1/{accessKey}/{timestamp}/{SignatureExpirationSeconds}";
        var canonicalRequest = $"{method.ToUpperInvariant()}\n{EncodePath(path)}\n{CanonicalQuery(query)}\nhost:{Encode(host)}";
        var signingKey = HmacHex(Encoding.UTF8.GetBytes(secretKey), prefix);
        var signature = HmacHex(Encoding.UTF8.GetBytes(signingKey), canonicalRequest);
        return $"{prefix}/host/{signature}";
    }

    private static string CanonicalQuery(IReadOnlyDictionary<string, string>? query) => query is null
        ? ""
        : string.Join("&", query.Where(pair => !string.Equals(pair.Key, "authorization", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{Encode(pair.Key)}={Encode(pair.Value)}")
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string EncodePath(string path) => string.Join('/',
        (path.StartsWith('/') ? path : "/" + path).Split('/').Select(Encode));

    private static string Encode(string value) => Uri.EscapeDataString(value)
        .Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    private static string HmacHex(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string RequiredString(JsonObject json, string name)
    {
        var value = json[name]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(value)) throw ApiError(json, $"百度 VOD 响应缺少 {name}");
        return value;
    }

    private static Exception ApiError(JsonObject json, string fallback) =>
        new InvalidOperationException($"{fallback}：{ApiMessage(json)}");

    private static string ApiMessage(JsonObject json) =>
        json["message"]?.GetValue<string>() ?? json["code"]?.GetValue<string>() ?? json.ToJsonString();

    private static string ContentTypeFor(string extension) => extension switch
    {
        "mp4" => "video/mp4",
        "mov" => "video/quicktime",
        "mkv" => "video/x-matroska",
        "webm" => "video/webm",
        _ => "application/octet-stream",
    };
}

internal sealed record BaiduSubtitleResult(string Status, string TargetUrl, string Error);
