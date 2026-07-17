using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokManagementUploadRecordSyncResult(bool Ok, string Message);
public sealed record TikTokManagementDuplicateCheckResult(
    bool Ok,
    string Message,
    IReadOnlySet<string> Duplicates);

/// <summary>同步 TikTok 上传记录到短剧管理系统；接口契约对齐 Python management_upload_record_sync_service。</summary>
public static class TikTokManagementUploadRecordSyncService
{
    private const string Platform = "tt";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static async Task<TikTokManagementUploadRecordSyncResult> SyncUploadRecordAsync(
        QueueProjectItem item,
        TikTokAccountProfile? account,
        CancellationToken ct)
    {
        var settings = ClientSettingsStore.Load();
        var state = LicenseStore.Load();
        var baseUrl = CleanBaseUrl(state.ServerUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = CleanBaseUrl(settings.AuthServerUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new(false, "管理系统地址未配置（请在 系统服务 配置服务器地址或登录账号）");

        var accountName = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey);
        var machineId = (state.MachineId ?? "").Trim();
        var token = (state.Token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(token))
            return new(false, "软件未登录或登录态不完整，请在 系统服务 登录 TT 账号后再同步");

        var record = FinalizeRecord(BuildRecordForSync(item, account), account);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["records"] = new[] { record },
        }, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/client-api/upload-records/batch");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-TT-Account", accountName);
        request.Headers.TryAddWithoutValidation("X-TT-Machine-Id", machineId);
        request.Headers.TryAddWithoutValidation("X-TT-Token", token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return InterpretResponse((int)response.StatusCode, SafeJson(body));
        }
        catch (OperationCanceledException ex)
        {
            if (ct.IsCancellationRequested)
                throw;
            return new(false, $"连接管理系统超时：{ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(false, $"连接管理系统失败：{ex.Message}");
        }
    }

    public static async Task<TikTokManagementDuplicateCheckResult> CheckDuplicateOriginalNamesAsync(
        IEnumerable<string> originalNames,
        string dedupeScope,
        TikTokAccountProfile? account,
        CancellationToken ct)
    {
        var names = originalNames
            .Select(name => (name ?? "").Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
            return new(true, "", new HashSet<string>(StringComparer.Ordinal));

        var settings = ClientSettingsStore.Load();
        var state = LicenseStore.Load();
        var baseUrl = CleanBaseUrl(state.ServerUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = CleanBaseUrl(settings.AuthServerUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new(false, "管理系统地址未配置", new HashSet<string>(StringComparer.Ordinal));

        var accountName = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey);
        var machineId = (state.MachineId ?? "").Trim();
        var token = (state.Token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(token))
            return new(false, "软件未登录或登录态不完整", new HashSet<string>(StringComparer.Ordinal));

        var payload = BuildDuplicateCheckPayload(names, dedupeScope, account);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/client-api/upload-records/check-duplicates");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-TT-Account", accountName);
        request.Headers.TryAddWithoutValidation("X-TT-Machine-Id", machineId);
        request.Headers.TryAddWithoutValidation("X-TT-Token", token);
        request.Content = JsonContent(payload);

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return InterpretDuplicateResponse((int)response.StatusCode, SafeJson(body));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(false, $"连接管理系统失败：{ex.Message}", new HashSet<string>(StringComparer.Ordinal));
        }
    }

    internal static Dictionary<string, object?> BuildRecordForSync(QueueProjectItem item, TikTokAccountProfile? account)
    {
        var originalName = (item.OriginalTitle ?? "").Trim();
        var newName = FirstNonEmpty(item.NewTitle, originalName);
        var projectDir = (item.ProjectDir ?? "").Trim();
        var uploadState = item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries, "");
        var uploadStatus = uploadState switch
        {
            QueueStepStatus.Completed => "成功",
            QueueStepStatus.Failed => "失败",
            _ => FirstNonEmpty(item.StatusText, uploadState, "-"),
        };
        var queuedAt = (item.QueuedAt ?? "").Trim();
        var now = DateTime.Now;
        var recordTime = string.IsNullOrWhiteSpace(queuedAt)
            ? now.ToString("yyyy-MM-dd HH:mm:ss")
            : queuedAt;
        var date = queuedAt.Length >= 10 ? queuedAt[..10] : now.ToString("yyyy-MM-dd");

        var profileName = FirstNonEmpty(item.AccountProfileName, account?.DisplayName, account?.Name);
        var tiktokUsername = FirstNonEmpty(
            account?.TiktokLoginEmail,
            account?.TiktokLastLoginEmail,
            account?.TiktokAccountNickname);
        var uploaderDisplay = FirstNonEmpty(profileName, tiktokUsername);

        var record = new Dictionary<string, object?>
        {
            ["platform"] = Platform,
            ["record_time"] = recordTime,
            ["date"] = date,
            ["upload_status"] = uploadStatus,
            ["step_label"] = "上传剧集",
            ["project_name"] = string.IsNullOrWhiteSpace(projectDir) ? newName : Path.GetFileName(projectDir),
            ["project_path"] = projectDir,
            ["original_name"] = originalName,
            ["new_name"] = newName,
            ["remark"] = (item.Remark ?? "").Trim(),
            ["episodes"] = item.EpisodeCount.ToString(),
            ["series_id"] = ResolveSeriesId(projectDir),
            ["uploader_display"] = uploaderDisplay,
            ["account_profile_name"] = profileName,
            ["tiktok_username"] = tiktokUsername,
            ["tiktok_account_username"] = tiktokUsername,
            ["tiktok_login_email"] = tiktokUsername,
            ["tiktok_account"] = FirstNonEmpty(tiktokUsername, profileName),
            ["device_name"] = Dns.GetHostName(),
        };
        if (!string.IsNullOrWhiteSpace(item.LastError))
            record["failure_reason"] = item.LastError.Trim();
        return record;
    }

    internal static Dictionary<string, object?> BuildDuplicateCheckPayload(
        IEnumerable<string> originalNames,
        string dedupeScope,
        TikTokAccountProfile? account)
    {
        var names = originalNames
            .Select(name => (name ?? "").Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scope = NormalizeDedupeScope(dedupeScope);
        var payload = new Dictionary<string, object?>
        {
            ["original_names"] = names,
            ["platform"] = Platform,
            ["dedupe_scope"] = scope,
        };

        if (scope == "tiktok_username")
        {
            var username = FirstNonEmpty(
                account?.TiktokLoginEmail,
                account?.TiktokLastLoginEmail,
                account?.TiktokAccountNickname);
            payload["tiktok_username"] = username;
            payload["tiktok_account_username"] = username;
        }

        return payload;
    }

    private static Dictionary<string, object?> FinalizeRecord(
        Dictionary<string, object?> record,
        TikTokAccountProfile? account)
    {
        var finalized = new Dictionary<string, object?>(record, StringComparer.Ordinal)
        {
            ["platform"] = Platform,
            ["raw"] = new Dictionary<string, object?>(record, StringComparer.Ordinal),
        };
        if (!string.IsNullOrWhiteSpace(account?.Id) && !finalized.ContainsKey("account_profile_id"))
            finalized["account_profile_id"] = account.Id.Trim();
        finalized["sync_key"] = SyncKey(finalized);
        return finalized;
    }

    private static string SyncKey(IReadOnlyDictionary<string, object?> payload)
    {
        var stable = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["platform"] = payload.GetValueOrDefault("platform"),
            ["project_path"] = payload.GetValueOrDefault("project_path"),
            ["original_name"] = payload.GetValueOrDefault("original_name"),
            ["new_name"] = payload.GetValueOrDefault("new_name"),
            ["series_id"] = payload.GetValueOrDefault("series_id"),
        };
        if (string.IsNullOrWhiteSpace(stable["project_path"]?.ToString()) &&
            string.IsNullOrWhiteSpace(stable["series_id"]?.ToString()))
        {
            stable["record_time"] = payload.GetValueOrDefault("record_time");
        }

        var text = JsonSerializer.Serialize(stable, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static TikTokManagementUploadRecordSyncResult InterpretResponse(int status, JsonNode? parsed)
    {
        if (parsed is JsonObject obj && TryGetBoolean(obj, "ok") is not false && status is >= 200 and < 300)
        {
            var data = obj["data"] as JsonObject;
            return new(true,
                $"成功（新增 {GetInt(data, "created")}，更新 {GetInt(data, "updated")}，失败 {GetInt(data, "failed")}）");
        }

        var message = parsed is JsonObject errorObject
            ? FirstNonEmpty(errorObject["error"]?.ToString(), errorObject["message"]?.ToString())
            : parsed?.ToString() ?? "";
        if (status == 401)
            return new(false, FirstNonEmpty(message, "TT 登录态失效，请在 系统服务 重新登录 TT 账号"));
        return new(false, FirstNonEmpty(message, $"管理系统返回错误：HTTP {status}"));
    }

    private static TikTokManagementDuplicateCheckResult InterpretDuplicateResponse(int status, JsonNode? parsed)
    {
        if (status >= 400)
        {
            var message = parsed is JsonObject errorObject
                ? FirstNonEmpty(errorObject["error"]?.ToString(), errorObject["message"]?.ToString())
                : parsed?.ToString() ?? "";
            return new(false, FirstNonEmpty(message, $"管理系统返回错误：HTTP {status}"), new HashSet<string>(StringComparer.Ordinal));
        }

        if (parsed is not JsonObject obj || TryGetBoolean(obj, "ok") is false)
        {
            var message = parsed is JsonObject errorObject
                ? FirstNonEmpty(errorObject["error"]?.ToString(), errorObject["message"]?.ToString())
                : parsed?.ToString() ?? "";
            return new(false, FirstNonEmpty(message, "管理系统查重失败"), new HashSet<string>(StringComparer.Ordinal));
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        if (obj["data"] is JsonObject data && data["duplicates"] is JsonArray duplicates)
        {
            foreach (var node in duplicates)
            {
                var value = (node?.ToString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(value)) set.Add(value);
            }
        }

        return new(true, "", set);
    }

    private static string ResolveSeriesId(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir)) return "";
        var statePath = Path.Combine(projectDir, "tiktok-upload-state.json");
        if (!File.Exists(statePath)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            if (doc.RootElement.TryGetProperty("platform_series_lookup", out var lookup) &&
                lookup.ValueKind == JsonValueKind.Object &&
                lookup.TryGetProperty("series_id", out var seriesId))
            {
                return seriesId.ToString().Trim();
            }
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static JsonNode? SafeJson(string body)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch
        {
            return JsonValue.Create(body ?? "");
        }
    }

    private static bool? TryGetBoolean(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<bool>(); }
        catch { return null; }
    }

    private static int GetInt(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return 0;
        try { return node.GetValue<int>(); }
        catch { return int.TryParse(node.ToString(), out var value) ? value : 0; }
    }

    private static string CleanBaseUrl(string? value) => (value ?? "").Trim().TrimEnd('/');

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static string NormalizeDedupeScope(string? value)
    {
        var normalized = (value ?? "tiktok_username").Trim().ToLowerInvariant();
        return normalized switch
        {
            "software" or "login_user" or "owner" or "owner_user" => "software_user",
            "all" or "global" or "all_records" or "all_tt_series" or "global_series" => "all_series",
            "software_user" or "all_series" => normalized,
            _ => "tiktok_username",
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return "";
    }
}
