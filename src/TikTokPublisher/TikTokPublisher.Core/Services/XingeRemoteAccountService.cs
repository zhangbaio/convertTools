using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record XingeRemoteClientCredentials(
    string Username,
    string ClientId,
    string ClientToken,
    string CredentialFingerprint);

/// <summary>
/// Uses a XINGE account to obtain an account session and provision the remote-client credentials
/// consumed by the command polling API.
/// </summary>
public sealed class XingeRemoteAccountService(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool HasPasswordCredentials(ClientSettings settings) =>
        !string.IsNullOrWhiteSpace(ResolveServerUrl(settings)) &&
        !string.IsNullOrWhiteSpace(ResolveAccount(settings)) &&
        !string.IsNullOrEmpty(ResolvePassword(settings));

    public static bool NeedsProvisioning(ClientSettings settings)
    {
        if (!HasPasswordCredentials(settings))
            return false;

        return string.IsNullOrWhiteSpace(settings.XingeClientId) ||
               string.IsNullOrWhiteSpace(settings.XingeClientToken) ||
               !string.Equals(
                   settings.XingeCredentialFingerprint?.Trim(),
                   ComputeCredentialFingerprint(settings),
                   StringComparison.Ordinal);
    }

    public static string ComputeCredentialFingerprint(ClientSettings settings)
    {
        var source = string.Join("\n",
            NormalizeBaseUrl(ResolveServerUrl(settings)).ToLowerInvariant(),
            ResolveAccount(settings).Trim().ToLowerInvariant(),
            ResolvePassword(settings),
            MachineFingerprintHelper.GetMachineFingerprint());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public static async Task<XingeRemoteClientCredentials> ProvisionAsync(
        ClientSettings settings,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        return await new XingeRemoteAccountService(http).ProvisionWithHttpAsync(settings, ct).ConfigureAwait(false);
    }

    public async Task<XingeRemoteClientCredentials> ProvisionWithHttpAsync(
        ClientSettings settings,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeBaseUrl(ResolveServerUrl(settings));
        var account = ResolveAccount(settings).Trim();
        var password = ResolvePassword(settings);
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("请填写 XINGE 地址");
        if (account.Length == 0)
            throw new InvalidOperationException("请填写 XINGE 用户名或邮箱");
        if (password.Length == 0)
            throw new InvalidOperationException("请填写 XINGE 密码");

        var machineId = MachineFingerprintHelper.GetMachineFingerprint();
        var clientName = string.IsNullOrWhiteSpace(settings.XingeClientName)
            ? "TikTokPublisher"
            : settings.XingeClientName.Trim();
        var version = typeof(XingeRemoteAccountService).Assembly.GetName().Version?.ToString() ?? "unknown";

        var login = await PostAsync<XingeAccountAuthData>(
            baseUrl + "/client-api/account/login",
            new
            {
                account,
                password,
                machine_id = machineId,
                device_name = Environment.MachineName,
                app_name = "TikTokPublisher",
                app_version = version,
                force_login = true,
            },
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(login.Token))
            throw new InvalidOperationException("XINGE 登录响应缺少账号 Token");

        var remote = await PostAsync<XingeRemoteClientData>(
            baseUrl + "/client-api/account/remote-client",
            new
            {
                account,
                machine_id = machineId,
                token = login.Token,
                client_name = clientName,
            },
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(remote.Item?.ClientId) || string.IsNullOrWhiteSpace(remote.ClientToken))
            throw new InvalidOperationException("XINGE 未返回远程客户端凭证");

        var username = FirstNonEmpty(login.AccountUsername, login.Username, account);
        return new XingeRemoteClientCredentials(
            username,
            remote.Item.ClientId.Trim(),
            remote.ClientToken.Trim(),
            ComputeCredentialFingerprint(settings));
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        XingeEnvelope<T>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<XingeEnvelope<T>>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            // Report the HTTP response below with a bounded body preview.
        }

        if (!response.IsSuccessStatusCode || envelope is null || !envelope.Ok)
        {
            var message = FirstNonEmpty(envelope?.Message, BoundedPreview(raw), response.ReasonPhrase, "XINGE 请求失败");
            throw new InvalidOperationException(message);
        }

        return envelope.Data ?? throw new InvalidOperationException("XINGE 返回空数据");
    }

    private static string ResolveServerUrl(ClientSettings settings) =>
        FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl);

    private static string ResolveAccount(ClientSettings settings) =>
        FirstNonEmpty(settings.XingeAccount, settings.AuthAccount);

    private static string ResolvePassword(ClientSettings settings) =>
        !string.IsNullOrEmpty(settings.XingePassword) ? settings.XingePassword : settings.AuthPassword ?? "";

    private static string NormalizeBaseUrl(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out _))
            text = "http://" + text;
        return text.TrimEnd('/');
    }

    private static string BoundedPreview(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private sealed class XingeEnvelope<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class XingeAccountAuthData
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("account_username")]
        public string AccountUsername { get; set; } = "";

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    private sealed class XingeRemoteClientData
    {
        [JsonPropertyName("item")]
        public XingeRemoteClientItem? Item { get; set; }

        [JsonPropertyName("client_token")]
        public string ClientToken { get; set; } = "";
    }

    private sealed class XingeRemoteClientItem
    {
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; } = "";
    }
}
