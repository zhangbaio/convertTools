using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Notifications;

public sealed class FeishuNotificationService : IFeishuNotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, FeishuTokenCacheEntry> _tokenCache = new(StringComparer.Ordinal);

    public FeishuNotificationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task SendTextAsync(
        FeishuNotificationSettings settings,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        return SendTextWithOptionalImageAsync(settings, title, message, null, cancellationToken);
    }

    public async Task SendTextWithOptionalImageAsync(
        FeishuNotificationSettings settings,
        string title,
        string message,
        string? imagePath,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        ValidateSettings(settings);
        var token = await GetTenantAccessTokenAsync(settings, cancellationToken);
        await SendTextMessageAsync(token, settings, CombineTitleAndMessage(title, message), cancellationToken);

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        var imageKey = await UploadImageAsync(token, settings, imagePath, cancellationToken);
        await SendImageMessageAsync(token, settings, imageKey, cancellationToken);
    }

    private async Task<string> GetTenantAccessTokenAsync(
        FeishuNotificationSettings settings,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{settings.AppId}\n{settings.AppSecret}";
        if (_tokenCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{NormalizeApiBase(settings.ApiBase)}/open-apis/auth/v3/tenant_access_token/internal");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                app_id = settings.AppId,
                app_secret = settings.AppSecret
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"飞书租户令牌请求失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;
        if (code != 0)
        {
            throw new InvalidOperationException($"飞书租户令牌返回错误: {body}");
        }

        var token = root.TryGetProperty("tenant_access_token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"飞书租户令牌返回为空: {body}");
        }

        var expireSeconds = root.TryGetProperty("expire", out var expireElement) && expireElement.ValueKind == JsonValueKind.Number
            ? expireElement.GetInt32()
            : 7200;
        _tokenCache[cacheKey] = new FeishuTokenCacheEntry(
            token,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expireSeconds)));
        return token;
    }

    private async Task SendTextMessageAsync(
        string token,
        FeishuNotificationSettings settings,
        string text,
        CancellationToken cancellationToken)
    {
        using var request = BuildMessageRequest(token, settings, "text", new
        {
            text
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "飞书文本消息发送失败", cancellationToken);
    }

    private async Task<string> UploadImageAsync(
        string token,
        FeishuNotificationSettings settings,
        string imagePath,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("message"), "image_type");

        await using var stream = File.OpenRead(imagePath);
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveMimeType(imagePath));
        form.Add(streamContent, "image", Path.GetFileName(imagePath));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{NormalizeApiBase(settings.ApiBase)}/open-apis/im/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"飞书图片上传失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;
        if (code != 0)
        {
            throw new InvalidOperationException($"飞书图片上传返回错误: {body}");
        }

        var imageKey = root.TryGetProperty("data", out var dataElement) &&
                       dataElement.ValueKind == JsonValueKind.Object &&
                       dataElement.TryGetProperty("image_key", out var imageKeyElement) &&
                       imageKeyElement.ValueKind == JsonValueKind.String
            ? imageKeyElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(imageKey))
        {
            throw new InvalidOperationException($"飞书图片上传未返回 image_key: {body}");
        }

        return imageKey;
    }

    private async Task SendImageMessageAsync(
        string token,
        FeishuNotificationSettings settings,
        string imageKey,
        CancellationToken cancellationToken)
    {
        using var request = BuildMessageRequest(token, settings, "image", new
        {
            image_key = imageKey
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "飞书图片消息发送失败", cancellationToken);
    }

    private static HttpRequestMessage BuildMessageRequest(
        string token,
        FeishuNotificationSettings settings,
        string messageType,
        object content)
    {
        var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(settings.ReceiveIdType) ? "chat_id" : settings.ReceiveIdType.Trim());
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{NormalizeApiBase(settings.ApiBase)}/open-apis/im/v1/messages?receive_id_type={query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                receive_id = settings.ReceiveId,
                msg_type = messageType,
                content = JsonSerializer.Serialize(content, JsonOptions)
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{errorPrefix}: {(int)response.StatusCode} {response.ReasonPhrase}; body: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;
        if (code != 0)
        {
            throw new InvalidOperationException($"{errorPrefix}: {body}");
        }
    }

    private static void ValidateSettings(FeishuNotificationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AppId))
        {
            throw new InvalidOperationException("飞书通知缺少 AppId。");
        }

        if (string.IsNullOrWhiteSpace(settings.AppSecret))
        {
            throw new InvalidOperationException("飞书通知缺少 AppSecret。");
        }

        if (string.IsNullOrWhiteSpace(settings.ReceiveId))
        {
            throw new InvalidOperationException("飞书通知缺少 ReceiveId。");
        }
    }

    private static string CombineTitleAndMessage(string title, string message)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        return string.IsNullOrWhiteSpace(normalizedTitle)
            ? normalizedMessage
            : string.IsNullOrWhiteSpace(normalizedMessage)
                ? normalizedTitle
                : $"{normalizedTitle}\n{normalizedMessage}";
    }

    private static string NormalizeApiBase(string apiBase)
    {
        return string.IsNullOrWhiteSpace(apiBase)
            ? "https://open.feishu.cn"
            : apiBase.TrimEnd('/');
    }

    private static string ResolveMimeType(string imagePath)
    {
        return Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    private sealed record FeishuTokenCacheEntry(string Token, DateTimeOffset ExpiresAtUtc);
}
