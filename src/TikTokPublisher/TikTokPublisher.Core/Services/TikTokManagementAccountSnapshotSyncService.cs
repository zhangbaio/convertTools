using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokClientAccountSnapshotItem(
    [property: JsonPropertyName("client_account_id")] string ClientAccountId,
    [property: JsonPropertyName("tiktok_username")] string TikTokUsername);

public sealed record TikTokAccountSnapshotSyncResult(
    bool Ok,
    bool ShouldRetry,
    string Message);

/// <summary>把当前客户端的 TikTok 账号用户名快照同步到管理系统。</summary>
public sealed class TikTokManagementAccountSnapshotSyncService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Func<LicenseState> _stateProvider;

    public TikTokManagementAccountSnapshotSyncService()
        : this(SharedHttp, static () => ClientSettingsStore.Load(), LicenseStore.Load)
    {
    }

    internal TikTokManagementAccountSnapshotSyncService(
        HttpClient http,
        Func<ClientSettings> settingsProvider,
        Func<LicenseState> stateProvider)
    {
        _http = http;
        _settingsProvider = settingsProvider;
        _stateProvider = stateProvider;
    }

    public async Task<TikTokAccountSnapshotSyncResult> SyncAsync(
        IReadOnlyList<TikTokClientAccountSnapshotItem> accounts,
        CancellationToken ct = default)
    {
        if (accounts.Count > 200)
            return new(false, false, "客户端 TikTok 账号超过 200 个，无法同步");

        try
        {
            var settings = _settingsProvider();
            var state = _stateProvider();
            var baseUrl = ResolveBaseUrl(settings, state);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new(false, false, "管理系统地址未配置，客户端 TikTok 账号暂未同步");

            var accountName = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey);
            var machineId = (state.MachineId ?? "").Trim();
            var token = (state.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(token))
                return new(false, false, "软件未登录，客户端 TikTok 账号暂未同步");

            var payload = JsonSerializer.Serialize(
                new SnapshotRequest(accounts),
                JsonOptions);
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/client-api/tt/accounts/snapshot");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-TT-Account", accountName);
            request.Headers.TryAddWithoutValidation("X-TT-Machine-Id", machineId);
            request.Headers.TryAddWithoutValidation("X-TT-Token", token);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = SafeJson(body);
            var responseOk = ReadOk(parsed);
            if (response.IsSuccessStatusCode && responseOk is true)
                return new(true, false, "客户端 TikTok 账号同步完成");

            var status = (int)response.StatusCode;
            var message = ReadMessage(parsed);
            if (string.IsNullOrWhiteSpace(message))
                message = response.IsSuccessStatusCode && responseOk is null
                    ? "管理系统响应格式无效"
                    : $"管理系统返回 HTTP {status}";
            var shouldRetry = response.IsSuccessStatusCode && responseOk is null
                || response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                || status >= 500;
            return new(false, shouldRetry, $"客户端 TikTok 账号同步失败：{message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, true, "客户端 TikTok 账号同步超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(false, true, $"客户端 TikTok 账号同步失败：{ex.Message}");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return new(false, false, $"管理系统地址无效：{ex.Message}");
        }
    }

    internal string ResolveCurrentScopeKey()
    {
        var settings = _settingsProvider();
        var state = _stateProvider();
        return string.Join(
            '\u001f',
            ResolveBaseUrl(settings, state),
            FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey),
            (state.MachineId ?? "").Trim());
    }

    internal static IReadOnlyList<TikTokClientAccountSnapshotItem> BuildSnapshot(
        IEnumerable<TikTokAccountProfile> profiles)
    {
        var byId = new Dictionary<string, TikTokClientAccountSnapshotItem>(StringComparer.Ordinal);
        foreach (var profile in profiles ?? [])
        {
            var accountId = (profile.Id ?? "").Trim();
            var username = profile.ResolveTikTokAccountName().Trim();
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(username))
                continue;
            byId[accountId] = new TikTokClientAccountSnapshotItem(accountId, username);
        }

        return byId.Values
            .OrderBy(item => item.ClientAccountId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string BuildFingerprint(
        string scopeKey,
        IReadOnlyList<TikTokClientAccountSnapshotItem> accounts)
    {
        var builder = new StringBuilder(scopeKey ?? "");
        builder.Append('\u001e').Append(accounts.Count);
        foreach (var account in accounts)
        {
            builder.Append('\u001d')
                .Append(account.ClientAccountId)
                .Append('\u001f')
                .Append(account.TikTokUsername);
        }
        return builder.ToString();
    }

    private static string ResolveBaseUrl(ClientSettings settings, LicenseState state)
    {
        var baseUrl = CleanBaseUrl(state.ServerUrl);
        return string.IsNullOrWhiteSpace(baseUrl)
            ? CleanBaseUrl(settings.AuthServerUrl)
            : baseUrl;
    }

    private static string CleanBaseUrl(string? value) => (value ?? "").Trim().TrimEnd('/');

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => (value ?? "").Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static JsonNode? SafeJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch { return null; }
    }

    private static bool? ReadOk(JsonNode? parsed)
    {
        if (parsed?["ok"] is not JsonValue value) return null;
        return value.TryGetValue<bool>(out var result) ? result : null;
    }

    private static string ReadMessage(JsonNode? parsed)
    {
        foreach (var key in new[] { "message", "error" })
        {
            if (parsed?[key] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        return "";
    }

    private sealed record SnapshotRequest(
        [property: JsonPropertyName("accounts")]
        IReadOnlyList<TikTokClientAccountSnapshotItem> Accounts);
}
