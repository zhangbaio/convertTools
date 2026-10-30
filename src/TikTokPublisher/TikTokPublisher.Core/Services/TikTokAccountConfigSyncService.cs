using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public interface ITikTokAccountConfigSyncClient
{
    Task<TikTokAccountConfigPullResult> PullAsync(CancellationToken cancellationToken);

    Task<TikTokAccountConfigPushResult> PushAsync(
        TikTokAccountConfigPushRequest request,
        CancellationToken cancellationToken);

    Task<TikTokAccountConfigDeleteResult> DeleteAsync(
        string cloudAccountId,
        CancellationToken cancellationToken);

    Task<byte[]?> DownloadAsync(
        string cloudAccountId,
        string resource,
        CancellationToken cancellationToken);
}

public sealed class TikTokAccountConfigPushRequest
{
    public string CloudAccountId { get; init; } = "";
    public int BaseRevision { get; init; }
    public string TikTokUsername { get; init; } = "";
    public JsonObject Config { get; init; } = new();
    public JsonObject Secrets { get; init; } = new();
    public byte[]? LoginState { get; set; }
    public byte[]? Seal { get; set; }
    public string SealFilename { get; set; } = "";
}

public sealed record TikTokAccountConfigPullResult(
    bool Ok,
    bool ShouldRetry,
    string Message,
    IReadOnlyList<RemoteTikTokAccountConfig> Accounts);

public sealed record TikTokAccountConfigPushResult(
    bool Ok,
    bool Conflict,
    bool ShouldRetry,
    string Code,
    string Message,
    string ClientAccountId,
    int Revision,
    bool Adopted,
    RemoteTikTokAccountConfig? Account);

public sealed record TikTokAccountConfigDeleteResult(bool Ok, bool ShouldRetry, string Message);

public sealed class RemoteTikTokAccountConfig
{
    public string ClientAccountId { get; init; } = "";
    public string TikTokUsername { get; init; } = "";
    public int Revision { get; init; }
    public bool Deleted { get; init; }
    public string UpdatedAt { get; init; } = "";
    public string UpdatedByMachine { get; init; } = "";
    public JsonObject Config { get; init; } = new();
    public JsonObject Secrets { get; init; } = new();
    public string LoginStateSha256 { get; init; } = "";
    public string LoginStateUpdatedAt { get; init; } = "";
    public string SealSha256 { get; init; } = "";
    public string SealFilename { get; init; } = "";
    public string SealUpdatedAt { get; init; } = "";
}

/// <summary>把 TikTok 账号配置、登录态和印章同步到管理系统。</summary>
public sealed class TikTokAccountConfigSyncService : ITikTokAccountConfigSyncClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly HttpClient _http;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Func<LicenseState> _stateProvider;

    public TikTokAccountConfigSyncService()
        : this(SharedHttp, static () => ClientSettingsStore.Load(), LicenseStore.Load)
    {
    }

    internal TikTokAccountConfigSyncService(
        HttpClient http,
        Func<ClientSettings> settingsProvider,
        Func<LicenseState> stateProvider)
    {
        _http = http;
        _settingsProvider = settingsProvider;
        _stateProvider = stateProvider;
    }

    public Task<TikTokAccountConfigPullResult> PullAsync(CancellationToken cancellationToken) =>
        SendMappedAsync(async (baseUrl, account, machineId, token, ct) =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/client-api/tt/account-configs", account, machineId, token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TikTokAccountConfigJson.Parse(body);
            if (response.IsSuccessStatusCode && TikTokAccountConfigJson.ReadOk(parsed) is true)
            {
                var accounts = new List<RemoteTikTokAccountConfig>();
                if (parsed?["data"]?["accounts"] is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is JsonObject accountNode)
                            accounts.Add(TikTokAccountConfigJson.ReadAccount(accountNode));
                    }
                }
                return new TikTokAccountConfigPullResult(true, false, "", accounts);
            }

            return new TikTokAccountConfigPullResult(
                false,
                ShouldRetry(response),
                FailureMessage(response, parsed, "拉取账号配置失败"),
                []);
        }, new TikTokAccountConfigPullResult(false, false, "", []), cancellationToken);

    public Task<TikTokAccountConfigPushResult> PushAsync(
        TikTokAccountConfigPushRequest request,
        CancellationToken cancellationToken) =>
        SendMappedAsync(async (baseUrl, account, machineId, token, ct) =>
        {
            var payload = new JsonObject
            {
                ["base_revision"] = request.BaseRevision,
                ["tiktok_username"] = request.TikTokUsername,
                ["config"] = request.Config.DeepClone(),
                ["secrets"] = request.Secrets.DeepClone(),
            };
            if (request.LoginState is not null)
                payload["login_state_base64"] = Convert.ToBase64String(request.LoginState);
            if (request.Seal is not null)
            {
                payload["seal_base64"] = Convert.ToBase64String(request.Seal);
                payload["seal_filename"] = request.SealFilename;
            }

            using var httpRequest = CreateRequest(
                HttpMethod.Put,
                $"{baseUrl}/client-api/tt/account-configs/{Uri.EscapeDataString(request.CloudAccountId)}",
                account,
                machineId,
                token);
            httpRequest.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TikTokAccountConfigJson.Parse(body);
            var code = TikTokAccountConfigJson.Text(parsed as JsonObject, "code");
            var data = parsed?["data"] as JsonObject;
            var remote = data is null ? null : TikTokAccountConfigJson.ReadAccount(data);
            if (response.IsSuccessStatusCode && TikTokAccountConfigJson.ReadOk(parsed) is true)
            {
                return new TikTokAccountConfigPushResult(
                    true,
                    false,
                    false,
                    "",
                    "",
                    TikTokAccountConfigJson.Text(data, "client_account_id").Length > 0
                        ? TikTokAccountConfigJson.Text(data, "client_account_id")
                        : request.CloudAccountId,
                    data is null ? request.BaseRevision : TikTokAccountConfigJson.Int(data, "revision"),
                    TikTokAccountConfigJson.Bool(data, "adopted"),
                    remote);
            }

            var conflict = (int)response.StatusCode == 409 || code is "revision_conflict" or "username_taken";
            return new TikTokAccountConfigPushResult(
                false,
                conflict,
                !conflict && ShouldRetry(response),
                code,
                FailureMessage(response, parsed, "上传账号配置失败"),
                remote?.ClientAccountId ?? request.CloudAccountId,
                remote?.Revision ?? 0,
                false,
                remote);
        }, EmptyPush, cancellationToken);

    public Task<TikTokAccountConfigDeleteResult> DeleteAsync(
        string cloudAccountId,
        CancellationToken cancellationToken) =>
        SendMappedAsync(async (baseUrl, account, machineId, token, ct) =>
        {
            using var request = CreateRequest(
                HttpMethod.Delete,
                $"{baseUrl}/client-api/tt/account-configs/{Uri.EscapeDataString(cloudAccountId)}",
                account,
                machineId,
                token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TikTokAccountConfigJson.Parse(body);
            if (response.IsSuccessStatusCode && TikTokAccountConfigJson.ReadOk(parsed) is true)
                return new TikTokAccountConfigDeleteResult(true, false, "");
            return new TikTokAccountConfigDeleteResult(
                false,
                ShouldRetry(response),
                FailureMessage(response, parsed, "删除同步账号失败"));
        }, new TikTokAccountConfigDeleteResult(false, false, ""), cancellationToken);

    public async Task<byte[]?> DownloadAsync(
        string cloudAccountId,
        string resource,
        CancellationToken cancellationToken)
    {
        var outcome = await SendMappedAsync(async (baseUrl, account, machineId, token, ct) =>
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"{baseUrl}/client-api/tt/account-configs/{Uri.EscapeDataString(cloudAccountId)}/{resource}",
                account,
                machineId,
                token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (byte[]?)null;
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }, (byte[]?)null, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async Task<T> SendAsync<T>(
        Func<string, string, string, string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider();
        var state = _stateProvider();
        var baseUrl = ResolveBaseUrl(settings, state);
        var machineId = (state.MachineId ?? "").Trim();
        var token = (state.Token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new TikTokAccountConfigSyncException(false, "管理系统地址未配置，账号暂未同步");
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(token))
            throw new TikTokAccountConfigSyncException(false, "软件未登录，账号暂未同步");

        try
        {
            return await action(
                baseUrl,
                FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey),
                machineId,
                token,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TikTokAccountConfigSyncException(true, "账号同步超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TikTokAccountConfigSyncException(true, $"账号同步失败：{ex.Message}");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            throw new TikTokAccountConfigSyncException(false, $"管理系统地址无效：{ex.Message}");
        }
    }

    private static readonly TikTokAccountConfigPushResult EmptyPush = new(
        false, false, false, "", "", "", 0, false, null);

    private async Task<T> SendMappedAsync<T>(
        Func<string, string, string, string, CancellationToken, Task<T>> action,
        T fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (TikTokAccountConfigSyncException ex)
        {
            return fallback switch
            {
                TikTokAccountConfigPullResult => (T)(object)new TikTokAccountConfigPullResult(false, ex.ShouldRetry, ex.Message, []),
                TikTokAccountConfigPushResult push => (T)(object)(push with { ShouldRetry = ex.ShouldRetry, Message = ex.Message }),
                TikTokAccountConfigDeleteResult => (T)(object)new TikTokAccountConfigDeleteResult(false, ex.ShouldRetry, ex.Message),
                _ => fallback,
            };
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string account,
        string machineId,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-TT-Account", account);
        request.Headers.TryAddWithoutValidation("X-TT-Machine-Id", machineId);
        request.Headers.TryAddWithoutValidation("X-TT-Token", token);
        return request;
    }

    private static bool ShouldRetry(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || status >= 500;
    }

    private static string FailureMessage(HttpResponseMessage response, JsonNode? parsed, string fallback)
    {
        var message = TikTokAccountConfigJson.ReadMessage(parsed);
        return string.IsNullOrWhiteSpace(message)
            ? $"{fallback}：HTTP {(int)response.StatusCode}"
            : message;
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
        values.Select(value => (value ?? "").Trim()).FirstOrDefault(value => value.Length > 0) ?? "";
}

public sealed class TikTokAccountConfigSyncException(bool shouldRetry, string message) : Exception(message)
{
    public bool ShouldRetry { get; } = shouldRetry;
}
