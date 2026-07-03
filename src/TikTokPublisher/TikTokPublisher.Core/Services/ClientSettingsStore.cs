using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class ClientSettingsStore
{
    public const string SettingsKey = "client_settings";

    private static readonly HashSet<string> ExcludedSaveKeys = new(StringComparer.Ordinal)
    {
        "tiktok_account_profiles_json",
        "active_tiktok_account_profile_id",
        "tiktok_series_url",
        "tiktok_account_nickname",
        "tiktok_login_email",
        "tiktok_login_password",
        "tiktok_last_login_email",
        "tiktok_last_login_at",
        "tiktok_storage_state_path",
        "tiktok_login_browser_mode",
        "tiktok_fingerprint_browser_cdp_endpoint",
        "tiktok_fingerprint_browser_start_command",
        "last_workspace",
        "tiktok_upload_profile_path",
        "tiktok_proxy_enabled",
        "tiktok_proxy_type",
        "tiktok_proxy_host",
        "tiktok_proxy_port",
        "tiktok_proxy_username",
        "tiktok_proxy_password",
        "tiktok_proxy_label",
        "tiktok_static_ip_note",
        "tiktok_submit_enabled",
        "tiktok_submit_action",
        "tiktok_contract_id",
        "tiktok_contract_id_mode",
        "tiktok_anchor_promotion_enabled",
        "tiktok_target_audience_mode",
        "tiktok_genre_count",
        "tiktok_source_language",
        "tiktok_is_ai_drama",
        "tiktok_publish_mode",
        "tiktok_consignment_enabled",
        "tiktok_silence_validation_enabled",
        "tiktok_max_continuous_silence_seconds",
        "tiktok_silence_threshold_db",
        "tiktok_silence_detect_concurrency",
        "tiktok_material_validate_concurrency",
        "tiktok_silence_asr_language",
        "tiktok_silence_repair_mode",
        "tiktok_paid_enabled",
        "tiktok_paid_ratio_enabled",
        "tiktok_paid_ratio_percent",
        "tiktok_profile_preview_episodes",
        "tiktok_free_preview_episodes",
        "tiktok_expected_full_price_mode",
        "tiktok_expected_full_price_option_index",
        "tiktok_expected_full_price_value",
        "tiktok_expected_full_price_label",
        "tiktok_expected_full_price_options_json",
        "tiktok_project_concurrency",
        "tiktok_manual_intervention_on_single_failure",
        "tiktok_upload_stall_seconds",
        "tiktok_upload_strategy",
        "tiktok_upload_batch_size",
        "tiktok_upload_batch_stall_seconds",
        "tiktok_upload_batch_max_retries",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
    };

    public static ClientSettings Load(string? databasePath = null)
    {
        var raw = LoadRawObject(databasePath);
        if (raw is null)
        {
            return new ClientSettings();
        }

        var json = raw.ToJsonString();
        var settings = JsonSerializer.Deserialize<ClientSettings>(json, JsonOptions) ?? new ClientSettings();
        return Normalize(settings);
    }

    public static void Save(ClientSettings settings, string? databasePath = null)
    {
        var normalized = Normalize(settings);
        var path = ResolvePath(databasePath);
        PythonDatabaseInitializer.EnsureInitialized(path);

        var existing = LoadRawObject(path) ?? new JsonObject();
        var incoming = JsonSerializer.SerializeToNode(normalized, JsonOptions)?.AsObject() ?? new JsonObject();

        foreach (var property in incoming)
        {
            if (ExcludedSaveKeys.Contains(property.Key))
            {
                continue;
            }

            if (property.Key == "hgnew_password" &&
                property.Value is JsonValue passwordValue &&
                string.IsNullOrEmpty(passwordValue.GetValue<string?>()))
            {
                continue;
            }

            existing[property.Key] = property.Value?.DeepClone();
        }

        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var payload = existing.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ($key, $json, $now)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        cmd.Parameters.AddWithValue("$json", payload);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public static string MainDatabasePath => AppPaths.PythonDatabaseFile;

    public static string WorkspaceDatabasePath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "";
        }

        return WorkspaceQueuePaths.QueueDatabasePath(workspacePath);
    }

    private static JsonObject? LoadRawObject(string? databasePath = null)
    {
        var path = ResolvePath(databasePath);
        if (!File.Exists(path))
        {
            return null;
        }

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        var json = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonNode.Parse(json)?.AsObject();
    }

    public static void PatchPikachuRuntimeFields(string? fanqieCookie = null, string? deviceId = null, string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(fanqieCookie) && string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var settings = Load(databasePath);
        if (!string.IsNullOrWhiteSpace(fanqieCookie))
        {
            settings.PikachuFanqieCookie = fanqieCookie.Trim();
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            settings.PikachuDeviceId = deviceId.Trim().ToUpperInvariant();
        }

        Save(settings, databasePath);
    }

    private static ClientSettings Normalize(ClientSettings settings)
    {
        var chain = (settings.DramaSourceChain ?? "hgnew").Trim().ToLowerInvariant();
        settings.DramaSourceChain = chain switch
        {
            "hgnew" or "hg52api" or "hglocal" or "pikachu" => chain,
            _ => "hgnew"
        };

        settings.DramaDownloadDefaultQuality = string.IsNullOrWhiteSpace(settings.DramaDownloadDefaultQuality)
            ? "1080P"
            : settings.DramaDownloadDefaultQuality.Trim();
        settings.DramaDownloadConcurrent = Math.Clamp(settings.DramaDownloadConcurrent, 1, 10);
        settings.HongguoDownloadTimeoutSeconds = Math.Clamp(settings.HongguoDownloadTimeoutSeconds, 10, 300);
        settings.HongguoEpisodeDownloadAttempts = Math.Clamp(settings.HongguoEpisodeDownloadAttempts, 1, 10);
        settings.HgnewUdid = NormalizeUdid(settings.HgnewUdid);
        settings.HgnewClientVersion = string.IsNullOrWhiteSpace(settings.HgnewClientVersion)
            ? ClientSettings.DefaultHongguoClientVersion
            : settings.HgnewClientVersion.Trim();
        settings.PikachuDramaType = string.Equals(settings.PikachuDramaType, "manga", StringComparison.OrdinalIgnoreCase)
            ? "manga"
            : "short";
        settings.TiktokSilenceAsrEngine = NormalizeAsrEngine(settings.TiktokSilenceAsrEngine);
        settings.TiktokSilenceRepairMode = NormalizeRepairMode(settings.TiktokSilenceRepairMode);
        return settings;
    }

    public static string NormalizeUdid(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "" : trimmed.ToUpperInvariant();
    }

    private static string NormalizeAsrEngine(string? value) =>
        (value ?? "local").Trim().ToLowerInvariant() switch
        {
            "volcengine" or "local" or "hybrid" => (value ?? "local").Trim().ToLowerInvariant(),
            _ => "local"
        };

    private static string NormalizeRepairMode(string? value) =>
        (value ?? "auto").Trim().ToLowerInvariant() switch
        {
            "auto" or "trim" or "speedup" => (value ?? "auto").Trim().ToLowerInvariant(),
            _ => "auto"
        };

    private static string ResolvePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath) ? AppPaths.PythonDatabaseFile : Path.GetFullPath(databasePath);
}
