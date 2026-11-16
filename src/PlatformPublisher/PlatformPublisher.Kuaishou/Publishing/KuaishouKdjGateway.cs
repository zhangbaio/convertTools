using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public interface IKuaishouKdjGateway
{
    Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken);

    Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken);

    Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public static class KuaishouUploadTransport
{
    public const string Playwright = "playwright";
    public const string Api = "api";

    public static bool IsApi(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text)
            || string.Equals(text, Api, StringComparison.OrdinalIgnoreCase);
    }
}

public static class KuaishouAuthCookies
{
    public static CookieContainer Load(string authStatePath)
    {
        if (string.IsNullOrWhiteSpace(authStatePath) || !File.Exists(authStatePath))
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        var container = new CookieContainer();
        if (AddFromFile(container, authStatePath) == 0)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        return container;
    }

    public static void MergeInto(CookieContainer container, string authStatePath)
    {
        if (string.IsNullOrWhiteSpace(authStatePath) || !File.Exists(authStatePath))
            return;
        AddFromFile(container, authStatePath);
    }

    public static void Save(string authStatePath, CookieContainer container)
    {
        if (string.IsNullOrWhiteSpace(authStatePath) || !File.Exists(authStatePath))
            return;
        var root = JsonNode.Parse(File.ReadAllText(authStatePath)) as JsonObject
            ?? throw new JsonException("快手登录态无法读取。");
        var cookies = root["cookies"] as JsonArray ?? new JsonArray();
        root["cookies"] = cookies;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in new[] { "https://kdj.kuaishou.com/", "https://id.kuaishou.com/" })
        {
            foreach (System.Net.Cookie cookie in container.GetCookies(new Uri(uri)))
            {
                if (!seen.Add(cookie.Name + "|" + DomainKey(cookie.Domain)))
                    continue;
                var match = Find(cookies, cookie.Name, cookie.Domain);
                if (match is null)
                    cookies.Add(ToNode(cookie));
                else
                    match["value"] = cookie.Value;
            }
        }

        var temp = authStatePath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, authStatePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static int AddFromFile(CookieContainer container, string authStatePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(authStatePath));
        var count = 0;
        if (!document.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            return 0;
        foreach (var item in cookies.EnumerateArray())
        {
            var domain = Text(item, "domain");
            if (!domain.Contains("kuaishou.com", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Text(item, "name");
            var value = Text(item, "value");
            if (name.Length == 0 || value.Length == 0)
                continue;
            var path = Text(item, "path");
            if (path.Length == 0)
                path = "/";
            try
            {
                var cookie = new System.Net.Cookie(name, value, path, domain);
                if (item.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out var unix) && unix > 0)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
                if (item.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True)
                    cookie.Secure = true;
                if (item.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.ValueKind == JsonValueKind.True)
                    cookie.HttpOnly = true;
                container.Add(cookie);
                count++;
            }
            catch (CookieException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return count;
    }

    private static JsonObject? Find(JsonArray cookies, string name, string domain)
    {
        var key = DomainKey(domain);
        foreach (var node in cookies)
        {
            if (node is not JsonObject item)
                continue;
            if (!string.Equals(NodeText(item["name"]), name, StringComparison.Ordinal))
                continue;
            if (DomainKey(NodeText(item["domain"])) == key)
                return item;
        }

        return null;
    }

    private static JsonObject ToNode(System.Net.Cookie cookie)
    {
        var expires = cookie.Expires == DateTime.MinValue
            ? -1
            : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds();
        return new JsonObject
        {
            ["name"] = cookie.Name,
            ["value"] = cookie.Value,
            ["domain"] = cookie.Domain,
            ["path"] = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
            ["expires"] = expires,
            ["httpOnly"] = cookie.HttpOnly,
            ["secure"] = cookie.Secure,
            ["sameSite"] = "Lax",
        };
    }

    private static string DomainKey(string domain) => domain.Trim().TrimStart('.').ToLowerInvariant();

    private static string NodeText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

public static class KuaishouPassTokenLogin
{
    public const string Message = "快手登录态无效，请先打开经营者管理平台。";
    private const string PassTokenPath = "/pass/kuaishou/login/passToken";

    public static bool IsRejected(string text) => TryRead(text, out _, out _);

    public static bool TryGetPassTokenUrl(string text, out string loginUrl)
    {
        loginUrl = string.Empty;
        return TryRead(text, out loginUrl, out var accepted) && accepted;
    }

    public static bool IsPassTokenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && uri.Host.Equals("id.kuaishou.com", StringComparison.OrdinalIgnoreCase)
            && path.Equals(PassTokenPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRead(string text, out string loginUrl, out bool passToken)
    {
        loginUrl = string.Empty;
        passToken = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            using var body = JsonDocument.Parse(text);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result))
                return false;
            var rejected = (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var code) && code == 109)
                || (result.ValueKind == JsonValueKind.String && result.GetString() == "109");
            if (!rejected)
                return false;
            if (root.TryGetProperty("loginUrl", out var url) && url.ValueKind == JsonValueKind.String)
            {
                var candidate = url.GetString()?.Trim() ?? string.Empty;
                if (IsPassTokenUrl(candidate))
                {
                    loginUrl = candidate;
                    passToken = true;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class KuaishouPageGateway : IKuaishouKdjGateway
{
    private readonly IPage _page;

    public KuaishouPageGateway(IPage page) => _page = page;

    public async Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _page.EvaluateAsync<string>(
            """
            async ({ url, body }) => {
              const response = await fetch(url, {
                method: 'POST', credentials: 'include',
                headers: { 'Content-Type': 'application/json' },
                body,
              });
              const text = await response.text();
              return JSON.stringify({ status: response.status, text: text.slice(0, 200000) });
            }
            """,
            new { url, body });
        return KuaishouSeriesDraftClient.Unwrap(raw, "快手接口");
    }

    public async Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"缺少{label}。", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > byteLimit)
            throw new InvalidOperationException($"{label}超过 {byteLimit / 1024 / 1024} MB。");
        var payload = JsonSerializer.Serialize(new
        {
            name = Path.GetFileName(path),
            type = Mime(path),
            base64 = Convert.ToBase64String(bytes),
        });
        var raw = await _page.EvaluateAsync<string>(
            """
            async ({ url, payload }) => {
              const file = JSON.parse(payload);
              const binary = atob(file.base64);
              const bytes = new Uint8Array(binary.length);
              for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
              const form = new FormData();
              form.append('file', new Blob([bytes], { type: file.type }), file.name);
              const response = await fetch(url, { method: 'POST', credentials: 'include', body: form });
              const text = await response.text();
              return JSON.stringify({ status: response.status, text: text.slice(0, 4000) });
            }
            """,
            new { url, payload });
        return KuaishouSeriesDraftClient.Unwrap(raw, label);
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "image/jpeg",
    };
}

public sealed class KuaishouApiSession : IDisposable, IKuaishouKdjGateway
{
    public const string Origin = "https://kdj.kuaishou.com";
    private static readonly ConcurrentDictionary<string, RefreshGate> RefreshGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly string _authStatePath;

    private KuaishouApiSession(HttpClient http, CookieContainer cookies, string authStatePath)
    {
        _http = http;
        _cookies = cookies;
        _authStatePath = authStatePath;
    }

    public static KuaishouApiSession Open(string authStatePath)
    {
        var cookies = KuaishouAuthCookies.Load(authStatePath);
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
        };
        return new KuaishouApiSession(CreateClient(handler), cookies, authStatePath);
    }

    public static KuaishouApiSession Open(string authStatePath, HttpMessageHandler handler) =>
        new(CreateClient(handler), KuaishouAuthCookies.Load(authStatePath), authStatePath);

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Origin + "/home/content/content-management");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return http;
    }

    public Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, url, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);

    public async Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"缺少{label}。", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > byteLimit)
            throw new InvalidOperationException($"{label}超过 {byteLimit / 1024 / 1024} MB。");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(Mime(path));
        form.Add(file, "file", Path.GetFileName(path));
        return await SendAsync(HttpMethod.Post, url, form, cancellationToken);
    }

    public Task<string> PostBytesAsync(string url, byte[] body, CancellationToken cancellationToken, string? contentRange = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (!string.IsNullOrWhiteSpace(contentRange))
            content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        return SendAsync(HttpMethod.Post, url, content, cancellationToken);
    }

    public Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, url, null, cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = await BufferAsync(method, url, content, cancellationToken).ConfigureAwait(false);
        var text = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (!KuaishouPassTokenLogin.IsRejected(text))
        {
            EnsureBusinessSuccess(text);
            return text;
        }

        if (!KuaishouPassTokenLogin.TryGetPassTokenUrl(text, out var loginUrl))
            throw new InvalidOperationException(KuaishouPassTokenLogin.Message);
        await RefreshAsync(loginUrl, cancellationToken).ConfigureAwait(false);
        text = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (KuaishouPassTokenLogin.IsRejected(text))
            throw new InvalidOperationException(KuaishouPassTokenLogin.Message);
        EnsureBusinessSuccess(text);
        return text;
    }

    private async Task RefreshAsync(string loginUrl, CancellationToken cancellationToken)
    {
        var gate = RefreshGates.GetOrAdd(Path.GetFullPath(_authStatePath), _ => new RefreshGate());
        var seen = Volatile.Read(ref gate.Generation);
        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref gate.Generation) != seen)
            {
                KuaishouAuthCookies.MergeInto(_cookies, _authStatePath);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, loginUrl);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
                throw new InvalidOperationException(KuaishouPassTokenLogin.Message);
            try
            {
                KuaishouAuthCookies.Save(_authStatePath, _cookies);
                Interlocked.Increment(ref gate.Generation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }

    private async Task<string> DispatchAsync(BufferedRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(request.Method, request.Url);
        if (request.Body is not null)
        {
            var content = new ByteArrayContent(request.Body);
            if (request.ContentType is not null)
                content.Headers.ContentType = request.ContentType;
            if (!string.IsNullOrWhiteSpace(request.ContentRange))
                content.Headers.TryAddWithoutValidation("Content-Range", request.ContentRange);
            message.Content = content;
        }

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        if (status >= 500)
            throw new HttpRequestException($"快手接口返回 HTTP {status}。");
        if (status is < 200 or >= 300)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? $"快手接口返回 HTTP {status}。" : $"快手接口返回 HTTP {status}。{Trim(text)}");
        return text;
    }

    private static async Task<BufferedRequest> BufferAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
            return new BufferedRequest(method, url, null, null, null);
        var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentType = content.Headers.ContentType is null ? null : MediaTypeHeaderValue.Parse(content.Headers.ContentType.ToString());
        var contentRange = content.Headers.TryGetValues("Content-Range", out var ranges) ? ranges.FirstOrDefault() : null;
        return new BufferedRequest(method, url, body, contentType, contentRange);
    }

    private static void EnsureBusinessSuccess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            using var body = JsonDocument.Parse(text);
            var root = body.RootElement;
            if (root.ValueKind == JsonValueKind.Object && ShouldCheckKdjResult(root))
                KuaishouSeriesDraft.EnsureSuccess(root);
        }
        catch (JsonException)
        {
        }
    }

    private static bool ShouldCheckKdjResult(JsonElement root)
    {
        if (root.TryGetProperty("result", out _) ||
            root.TryGetProperty("miniSeriesId", out _) ||
            root.TryGetProperty("imgKey", out _))
            return true;
        var fragment = root.TryGetProperty("fragment_index", out _) || root.TryGetProperty("fragmentIndex", out _);
        return !fragment && KuaishouSeriesDraft.ReadMessage(root).Length > 0;
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "image/jpeg",
    };

    private static string Trim(string text) => text.Length <= 180 ? text : text[..180];

    private readonly record struct BufferedRequest(
        HttpMethod Method,
        string Url,
        byte[]? Body,
        MediaTypeHeaderValue? ContentType,
        string? ContentRange);

    private sealed class RefreshGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Generation;
    }
}
