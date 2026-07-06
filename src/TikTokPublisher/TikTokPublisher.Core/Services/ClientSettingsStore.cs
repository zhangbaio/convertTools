using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;

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
        AppDatabaseInitializer.EnsureInitialized(path);

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

        SaveRawObject(path, existing);
    }

    public static void ResetInstallerDataSecrets(string? databasePath = null)
    {
        var path = ResolvePath(databasePath);
        AppDatabaseInitializer.EnsureInitialized(path);

        var existing = LoadRawObject(path) ?? new JsonObject();
        existing["hgnew_account"] = "";
        existing["hgnew_password"] = "";
        existing["ai_text_api_key"] = "";
        existing["image_model_api_key"] = "";
        existing["ofox_image2_api_key"] = "";
        SaveRawObject(path, existing);
    }

    public static void ResetHgnewCredentials(string? databasePath = null) =>
        ResetInstallerDataSecrets(databasePath);

    public static string MainDatabasePath => AppPaths.AppDatabaseFile;

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

    private static void SaveRawObject(string path, JsonObject settings)
    {
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var payload = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

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
            "hgnew" or "hglocal" or "pikachu" => chain,
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
        settings.TiktokSilenceDetectConcurrency = Math.Clamp(settings.TiktokSilenceDetectConcurrency, 1, 16);
        settings.TiktokMaterialValidateConcurrency = Math.Clamp(settings.TiktokMaterialValidateConcurrency, 1, 16);
        settings.TiktokSilenceAsrLanguage = string.IsNullOrWhiteSpace(settings.TiktokSilenceAsrLanguage)
            ? "zh-CN"
            : settings.TiktokSilenceAsrLanguage.Trim();
        settings.AiTextEndpoint = DefaultIfBlank(settings.AiTextEndpoint, ClientSettingsDefaults.AiTextEndpoint);
        settings.AiTextApiKey ??= "";
        settings.AiTextModel = DefaultIfBlank(settings.AiTextModel, ClientSettingsDefaults.AiTextModel);
        settings.AiTextTimeoutSeconds = Math.Clamp(
            settings.AiTextTimeoutSeconds <= 0
                ? ClientSettingsDefaults.AiTextTimeoutSeconds
                : settings.AiTextTimeoutSeconds,
            10,
            600);
        settings.AiTextMaxBatchSize = Math.Clamp(
            settings.AiTextMaxBatchSize <= 0
                ? ClientSettingsDefaults.AiTextMaxBatchSize
                : settings.AiTextMaxBatchSize,
            1,
            50);
        settings.AiTagSystemPrompt = DefaultIfBlank(settings.AiTagSystemPrompt, ClientSettingsDefaults.AiTagSystemPrompt);
        settings.AiTagBatchPrompt = DefaultIfBlank(settings.AiTagBatchPrompt, ClientSettingsDefaults.AiTagBatchPrompt);
        settings.AiFullInfoSystemPrompt = DefaultIfBlank(settings.AiFullInfoSystemPrompt, ClientSettingsDefaults.AiFullInfoSystemPrompt);
        settings.AiFullInfoBatchPrompt = DefaultIfBlank(settings.AiFullInfoBatchPrompt, ClientSettingsDefaults.AiFullInfoBatchPrompt);
        settings.AiFullInfoRetryPrompt = DefaultIfBlank(settings.AiFullInfoRetryPrompt, ClientSettingsDefaults.AiFullInfoRetryPrompt);
        settings.PosterMode = NormalizePosterMode(settings.PosterMode);
        settings.ImageProvider = NormalizeImageProvider(settings.ImageProvider);
        settings.ImageModelId = DefaultIfBlank(settings.ImageModelId, ClientSettingsDefaults.ImageModelId);
        settings.ImageModelApiKey ??= "";
        settings.ImageModelEndpoint = DefaultIfBlank(settings.ImageModelEndpoint, ClientSettingsDefaults.ImageModelEndpoint);
        settings.DoubaoImageResolution = DefaultIfBlank(settings.DoubaoImageResolution, ClientSettingsDefaults.DoubaoImageResolution);
        settings.DoubaoImageRatio = NormalizeDoubaoImageRatio(settings.DoubaoImageRatio);
        settings.OfoxImage2ModelId = DefaultIfBlank(settings.OfoxImage2ModelId, ClientSettingsDefaults.OfoxImage2ModelId);
        settings.OfoxImage2ApiKey ??= "";
        settings.OfoxImage2Endpoint = DefaultIfBlank(settings.OfoxImage2Endpoint, ClientSettingsDefaults.OfoxImage2Endpoint);
        settings.OfoxImage2Quality = DefaultIfBlank(settings.OfoxImage2Quality, ClientSettingsDefaults.OfoxImage2Quality);
        settings.OfoxImage2Size = DefaultIfBlank(settings.OfoxImage2Size, ClientSettingsDefaults.OfoxImage2Size);
        settings.PosterTitleVerifyMode = NormalizePosterTitleVerifyMode(settings.PosterTitleVerifyMode);
        settings.FrameCoverPrompt = DefaultIfBlank(settings.FrameCoverPrompt, ClientSettingsDefaults.FrameCoverPrompt);
        settings.PosterLayoutDetectPrompt = DefaultIfBlank(settings.PosterLayoutDetectPrompt, ClientSettingsDefaults.PosterLayoutDetectPrompt);
        settings.PosterInpaintPrompt = DefaultIfBlank(settings.PosterInpaintPrompt, ClientSettingsDefaults.PosterInpaintPrompt);
        settings.PosterInpaintSafeRetryPrompt = DefaultIfBlank(settings.PosterInpaintSafeRetryPrompt, ClientSettingsDefaults.PosterInpaintSafeRetryPrompt);
        settings.PosterGenerationPrompt = DefaultIfBlank(settings.PosterGenerationPrompt, ClientSettingsDefaults.PosterGenerationPrompt);
        settings.PosterGenerationSafeRetryPrompt = DefaultIfBlank(settings.PosterGenerationSafeRetryPrompt, ClientSettingsDefaults.PosterGenerationSafeRetryPrompt);
        settings.PosterNameSystemPrompt = DefaultIfBlank(settings.PosterNameSystemPrompt, ClientSettingsDefaults.PosterNameSystemPrompt);
        settings.PosterNameUserPrompt = DefaultIfBlank(settings.PosterNameUserPrompt, ClientSettingsDefaults.PosterNameUserPrompt);
        settings.AuthServerUrl = (settings.AuthServerUrl ?? "").Trim().TrimEnd('/');
        settings.AuthAccount = (settings.AuthAccount ?? "").Trim();
        settings.AuthPassword ??= "";
        settings.AuthLastUsername = (settings.AuthLastUsername ?? "").Trim();
        settings.AuthLastLoginAt = (settings.AuthLastLoginAt ?? "").Trim();
        settings.ManagementDedupScope = NormalizeManagementDedupScope(settings.ManagementDedupScope);
        settings.TiktokOverLimitDownloadEpisodeCount = Math.Clamp(
            settings.TiktokOverLimitDownloadEpisodeCount <= 0
                ? ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount
                : settings.TiktokOverLimitDownloadEpisodeCount,
            1,
            ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
        settings.FeishuCommandAppId = (settings.FeishuCommandAppId ?? "").Trim();
        settings.FeishuCommandAppSecret = settings.FeishuCommandAppSecret ?? "";
        settings.FeishuCommandBotName = (settings.FeishuCommandBotName ?? "").Trim();
        settings.FeishuCommandBotAliases = (settings.FeishuCommandBotAliases ?? "").Trim();
        settings.FeishuCommandAllowedChatIds = (settings.FeishuCommandAllowedChatIds ?? "").Trim();
        settings.FeishuCommandAllowedUserIds = (settings.FeishuCommandAllowedUserIds ?? "").Trim();
        settings.FeishuCommandDefaultWorkspace = (settings.FeishuCommandDefaultWorkspace ?? "").Trim();
        settings.FeishuCommandCommandTtlSeconds = Math.Clamp(settings.FeishuCommandCommandTtlSeconds, 10, 3600);
        settings.FeishuCommandHelpText = DefaultIfBlank(settings.FeishuCommandHelpText, ClientSettingsDefaults.FeishuCommandHelpText);
        settings.FeishuTiktokUploadEnabledStepsJson = TikTokRemoteRunOptions.DumpFeishuTikTokUploadEnabledSteps(
            TikTokRemoteRunOptions.LoadFeishuTikTokUploadEnabledSteps(settings));
        return settings;
    }

    private static string DefaultIfBlank(string? value, string fallback)
    {
        var text = value?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
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

    private static string NormalizePosterMode(string? value) =>
        (value ?? ClientSettingsDefaults.PosterMode).Trim().ToLowerInvariant() switch
        {
            "original" or "poster_ai_erase_pil_title" or "poster_ai_edit" => (value ?? ClientSettingsDefaults.PosterMode).Trim().ToLowerInvariant(),
            "ai" => "poster_ai_edit",
            _ => ClientSettingsDefaults.PosterMode
        };

    private static string NormalizeImageProvider(string? value) =>
        (value ?? ClientSettingsDefaults.ImageProvider).Trim().ToLowerInvariant() switch
        {
            "doubao" or "ofox_image2" => (value ?? ClientSettingsDefaults.ImageProvider).Trim().ToLowerInvariant(),
            _ => ClientSettingsDefaults.ImageProvider
        };

    private static string NormalizePosterTitleVerifyMode(string? value) =>
        (value ?? ClientSettingsDefaults.PosterTitleVerifyMode).Trim().ToLowerInvariant() switch
        {
            "fallback_repaint" or "warn" or "blocking" => (value ?? ClientSettingsDefaults.PosterTitleVerifyMode).Trim().ToLowerInvariant(),
            _ => ClientSettingsDefaults.PosterTitleVerifyMode
        };

    private static string NormalizeDoubaoImageRatio(string? value) =>
        (value ?? ClientSettingsDefaults.DoubaoImageRatio).Trim() switch
        {
            "3:4" => ClientSettingsDefaults.DoubaoImageRatio,
            _ => ClientSettingsDefaults.DoubaoImageRatio
        };

    private static string NormalizeManagementDedupScope(string? value)
    {
        var normalized = (value ?? "tiktok_username").Trim().ToLowerInvariant();
        return normalized switch
        {
            "tiktok" or "tiktok_account" or "tiktok_account_username" or "tt_account" or "account_username" => "tiktok_username",
            "software" or "login_user" or "owner" or "owner_user" => "software_user",
            "tiktok_username" or "software_user" => normalized,
            _ => "tiktok_username"
        };
    }

    private static string ResolvePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath) ? AppPaths.AppDatabaseFile : Path.GetFullPath(databasePath);
}
