using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public interface IKuaishouKdjGateway
{
    Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken);

    Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken);
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
        using var document = JsonDocument.Parse(File.ReadAllText(authStatePath));
        var container = new CookieContainer();
        var count = 0;
        if (!document.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
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
                var cookie = new System.Net.Cookie(name, value, path);
                if (item.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out var unix) && unix > 0)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
                container.Add(new Uri("https://kdj.kuaishou.com/"), cookie);
                count++;
            }
            catch (System.Net.CookieException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        if (count == 0)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        return container;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
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
    private readonly HttpClient _http;

    private KuaishouApiSession(HttpClient http) => _http = http;

    public static KuaishouApiSession Open(string authStatePath)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = KuaishouAuthCookies.Load(authStatePath),
            UseCookies = true,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Origin + "/home/content/content-management");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return new KuaishouApiSession(http);
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
        using var request = new HttpRequestMessage(method, url) { Content = content };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        if (status >= 500)
            throw new HttpRequestException($"快手接口返回 HTTP {status}。");
        if (status is < 200 or >= 300)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? $"快手接口返回 HTTP {status}。" : $"快手接口返回 HTTP {status}。{Trim(text)}");
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
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

        return text;
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
}
