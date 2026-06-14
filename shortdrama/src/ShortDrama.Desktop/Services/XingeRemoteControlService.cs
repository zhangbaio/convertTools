using ShortDrama.Desktop.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Desktop.Services;

public sealed class XingeRemoteControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;

    public XingeRemoteControlService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<XingeCredentialRefreshResult> FetchClientCredentialsAsync(
        GlobalConfigSnapshot globalConfig,
        CancellationToken cancellationToken)
    {
        if (!globalConfig.XingeEnabled)
        {
            throw new InvalidOperationException("请先启用 Xinge 远程控制。");
        }

        var serverUrl = CleanBaseUrl(globalConfig.XingeServerUrl);
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new InvalidOperationException("请先填写 Xinge 服务地址。");
        }

        var username = globalConfig.XingeUsername.Trim();
        var password = globalConfig.XingePassword;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("请先填写 Xinge 用户名和密码。");
        }

        var loginResult = await LoginAndCreateClientAsync(
            serverUrl,
            username,
            password,
            ResolveClientName(globalConfig.XingeClientName),
            cancellationToken);

        var updatedConfig = globalConfig with
        {
            XingeServerUrl = serverUrl,
            XingeClientId = loginResult.ClientId,
            XingeClientToken = loginResult.ClientToken,
            XingeUserRole = loginResult.UserRole
        };

        return new XingeCredentialRefreshResult(updatedConfig, loginResult);
    }

    public async Task<XingeQueueSyncResult> SyncQueueSelectionAsync(
        GlobalConfigSnapshot globalConfig,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("items", out var itemsValue) ||
            itemsValue is not IEnumerable<object> items ||
            !items.Cast<object>().Any())
        {
            throw new InvalidOperationException("同步到 Xinge 的 payload 缺少勾选任务明细。");
        }

        var resolved = await EnsureClientSettingsAsync(globalConfig, cancellationToken);
        var serverUrl = resolved.ClientSettings.ServerUrl.TrimEnd('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/client-api/queue-selections/sync");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Id", resolved.ClientSettings.ClientId);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Token", resolved.ClientSettings.ClientToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError(response.StatusCode, response.ReasonPhrase, raw, "Xinge 同步失败");
        }

        var parsed = ParseJsonObject(raw, "Xinge 返回的不是合法 JSON。");
        if (TryGetBoolean(parsed, "ok") == false)
        {
            throw new InvalidOperationException(GetString(parsed, "message") ?? "同步到 Xinge 失败。");
        }

        var data = parsed["data"] as JsonObject ?? parsed;
        return new XingeQueueSyncResult(
            resolved.UpdatedGlobalConfig,
            GetInt(data, "item_count"),
            GetInt(data, "snapshot_id"),
            GetString(data, "updated_at") ?? string.Empty,
            GetString(parsed, "message") ?? "同步到 Xinge 成功。");
    }

    public static string ComputeProjectKey(string projectPath)
    {
        var normalized = projectPath.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<(XingeClientSettings ClientSettings, GlobalConfigSnapshot UpdatedGlobalConfig)> EnsureClientSettingsAsync(
        GlobalConfigSnapshot globalConfig,
        CancellationToken cancellationToken)
    {
        if (!globalConfig.XingeEnabled)
        {
            throw new InvalidOperationException("请先在配置中启用 Xinge 远程控制。");
        }

        var serverUrl = CleanBaseUrl(globalConfig.XingeServerUrl);
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new InvalidOperationException("请先配置 Xinge 服务地址。");
        }

        var clientId = globalConfig.XingeClientId.Trim();
        var clientToken = globalConfig.XingeClientToken.Trim();
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientToken))
        {
            return (BuildClientSettings(globalConfig with { XingeServerUrl = serverUrl }), globalConfig with { XingeServerUrl = serverUrl });
        }

        var refreshed = await FetchClientCredentialsAsync(globalConfig with { XingeServerUrl = serverUrl }, cancellationToken);
        return (BuildClientSettings(refreshed.UpdatedGlobalConfig), refreshed.UpdatedGlobalConfig);
    }

    private static XingeClientSettings BuildClientSettings(GlobalConfigSnapshot globalConfig)
    {
        return new XingeClientSettings(
            CleanBaseUrl(globalConfig.XingeServerUrl),
            globalConfig.XingeClientId.Trim(),
            globalConfig.XingeClientToken.Trim(),
            ResolveClientName(globalConfig.XingeClientName),
            int.TryParse(globalConfig.XingePollIntervalSeconds, out var pollIntervalSeconds) && pollIntervalSeconds > 0
                ? pollIntervalSeconds
                : 3,
            globalConfig.XingeUploadLoginQr,
            globalConfig.XingeWsEnabled);
    }

    private static async Task<XingeLoginResult> LoginAndCreateClientAsync(
        string serverUrl,
        string username,
        string password,
        string clientName,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{serverUrl}/api/login",
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            },
            headers: null,
            cancellationToken,
            failureLabel: "Xinge 登录失败");

        var currentUser = await SendJsonAsync(
            client,
            HttpMethod.Get,
            $"{serverUrl}/api/me",
            payload: null,
            headers: null,
            cancellationToken,
            failureLabel: "Xinge 登录后读取当前用户信息失败");

        var currentUserPayload = currentUser["user"] as JsonObject ?? currentUser;

        var response = await SendJsonAsync(
            client,
            HttpMethod.Get,
            $"{serverUrl}/api/remote/clients",
            payload: null,
            headers: null,
            cancellationToken,
            failureLabel: "Xinge 登录后读取远程设备列表失败");
        if (response["items"] is not JsonArray)
        {
            throw new InvalidOperationException("Xinge 登录后读取远程设备列表失败。");
        }

        var createResponse = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{serverUrl}/api/remote/clients",
            new Dictionary<string, string>
            {
                ["client_name"] = clientName
            },
            headers: null,
            cancellationToken,
            failureLabel: "Xinge 创建设备返回格式不正确");

        var item = createResponse["item"] as JsonObject;
        var clientId = GetString(item, "client_id") ?? string.Empty;
        var clientToken = GetString(createResponse, "client_token") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientToken))
        {
            throw new InvalidOperationException("Xinge 登录后未获取到有效的 client_id 或 client_token。");
        }

        return new XingeLoginResult(
            clientId,
            clientToken,
            (GetString(currentUserPayload, "role") ?? string.Empty).Trim().ToLowerInvariant());
    }

    private static async Task<JsonObject> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object? payload,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken,
        string failureLabel)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError(response.StatusCode, response.ReasonPhrase, raw, failureLabel);
        }

        return ParseJsonObject(raw, $"{failureLabel}：返回的不是合法 JSON。");
    }

    private static JsonObject ParseJsonObject(string raw, string invalidMessage)
    {
        try
        {
            return JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidOperationException("返回格式不是 JSON 对象。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException(invalidMessage, ex);
        }
    }

    private static Exception CreateHttpError(HttpStatusCode statusCode, string? reasonPhrase, string raw, string failureLabel)
    {
        var detail = ExtractRemoteErrorMessage(raw);
        var message = $"{failureLabel}：HTTP {(int)statusCode} {reasonPhrase}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message = $"{message}，{detail}";
        }

        return new InvalidOperationException(message.TrimEnd('，'));
    }

    private static string ExtractRemoteErrorMessage(string raw)
    {
        var text = raw.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj)
            {
                return text;
            }

            return GetString(obj, "message")
                ?? GetString(obj, "error")
                ?? GetString(obj, "detail")
                ?? text;
        }
        catch
        {
            return text;
        }
    }

    private static string CleanBaseUrl(string serverUrl)
    {
        return (serverUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    private static string ResolveClientName(string? clientName)
    {
        return string.IsNullOrWhiteSpace(clientName)
            ? "短剧助手"
            : clientName.Trim();
    }

    private static string? GetString(JsonObject? obj, string propertyName)
    {
        return obj?[propertyName]?.GetValue<string?>()?.Trim();
    }

    private static bool? TryGetBoolean(JsonObject? obj, string propertyName)
    {
        if (obj?[propertyName] is null)
        {
            return null;
        }

        try
        {
            return obj[propertyName]!.GetValue<bool>();
        }
        catch
        {
            if (bool.TryParse(obj[propertyName]!.ToJsonString().Trim('"'), out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }

    private static int GetInt(JsonObject? obj, string propertyName)
    {
        if (obj?[propertyName] is null)
        {
            return 0;
        }

        try
        {
            return obj[propertyName]!.GetValue<int>();
        }
        catch
        {
            return int.TryParse(obj[propertyName]!.ToJsonString().Trim('"'), out var parsed) ? parsed : 0;
        }
    }
}

public sealed record XingeClientSettings(
    string ServerUrl,
    string ClientId,
    string ClientToken,
    string ClientName,
    int PollIntervalSeconds,
    bool UploadLoginQr,
    bool WebsocketEnabled);

public sealed record XingeLoginResult(
    string ClientId,
    string ClientToken,
    string UserRole);

public sealed record XingeCredentialRefreshResult(
    GlobalConfigSnapshot UpdatedGlobalConfig,
    XingeLoginResult LoginResult);

public sealed record XingeQueueSyncResult(
    GlobalConfigSnapshot UpdatedGlobalConfig,
    int ItemCount,
    int SnapshotId,
    string UpdatedAt,
    string Message);
