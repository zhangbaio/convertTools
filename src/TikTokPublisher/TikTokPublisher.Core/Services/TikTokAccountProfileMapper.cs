using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>将 Python <c>account_profiles</c> JSON（snake_case）映射为 C# 模型。</summary>
public static class TikTokAccountProfileMapper
{
    private const string FingerprintStartCommandKey = "tiktok_fingerprint_browser_start_command";
    private const string LegacyFingerprintStartCommandKey = "tiktok_fingerprint_start_command";
    private const int DefaultMaxContinuousSilenceSeconds = 20;
    private const double DefaultSilenceThresholdDb = -45.0;

    public static TikTokAccountProfile FromPythonPayload(
        JsonElement payload,
        string profileId,
        string? displayName = null,
        string? authStatePath = null,
        string? lastLoginEmail = null,
        string? lastLoginAt = null)
    {
        var profile = new TikTokAccountProfile
        {
            Id = profileId,
            Name = FirstNonEmpty(S(payload, "name"), displayName, profileId) ?? profileId,
            CreatedAt = S(payload, "created_at"),
            UpdatedAt = S(payload, "updated_at"),
            TiktokAccountNickname = S(payload, "tiktok_account_nickname"),
            TiktokLoginEmail = S(payload, "tiktok_login_email"),
            TiktokLoginPassword = S(payload, "tiktok_login_password"),
            TiktokLastLoginEmail = FirstNonEmpty(lastLoginEmail, S(payload, "tiktok_last_login_email")) ?? "",
            TiktokLastLoginAt = FirstNonEmpty(lastLoginAt, S(payload, "tiktok_last_login_at")) ?? "",
            TiktokStorageStatePath = FirstNonEmpty(authStatePath, S(payload, "tiktok_storage_state_path")) ?? "",
            TiktokLoginBrowserMode = NormalizeBrowserModeFromPython(S(payload, "tiktok_login_browser_mode")),
            TiktokFingerprintBrowserCdpEndpoint = S(payload, "tiktok_fingerprint_browser_cdp_endpoint"),
            TiktokFingerprintStartCommand = FirstNonEmpty(
                S(payload, FingerprintStartCommandKey),
                S(payload, LegacyFingerprintStartCommandKey)) ?? "",
            TiktokSeriesUrl = FirstNonEmpty(S(payload, "tiktok_series_url"), TikTokUrls.DefaultSeriesDraftUrl) ?? TikTokUrls.DefaultSeriesDraftUrl,
            LastWorkspace = S(payload, "last_workspace"),
            LastDownloadWorkspace = S(payload, "last_download_workspace"),
            TiktokUploadProfilePath = S(payload, "tiktok_upload_profile_path"),
            TiktokProxyEnabled = B(payload, "tiktok_proxy_enabled"),
            TiktokProxyType = FirstNonEmpty(S(payload, "tiktok_proxy_type"), "http") ?? "http",
            TiktokProxyHost = S(payload, "tiktok_proxy_host"),
            TiktokProxyPort = I(payload, "tiktok_proxy_port"),
            TiktokProxyUsername = S(payload, "tiktok_proxy_username"),
            TiktokProxyPassword = S(payload, "tiktok_proxy_password"),
            TiktokProxyLabel = S(payload, "tiktok_proxy_label"),
            TiktokStaticIpNote = S(payload, "tiktok_static_ip_note"),
            TiktokSubmitEnabled = payload.TryGetProperty("tiktok_submit_enabled", out _) ? B(payload, "tiktok_submit_enabled") : true,
            TiktokSubmitAction = FirstNonEmpty(S(payload, "tiktok_submit_action"), "draft") ?? "draft",
            TiktokContractId = S(payload, "tiktok_contract_id"),
            TiktokContractIdMode = FirstNonEmpty(S(payload, "tiktok_contract_id_mode"), "manual") ?? "manual",
            TiktokPaidEnabled = B(payload, "tiktok_paid_enabled"),
            TiktokPaidRatioEnabled = B(payload, "tiktok_paid_ratio_enabled"),
            TiktokPaidRatioPercent = D(payload, "tiktok_paid_ratio_percent"),
            TiktokProjectConcurrency = Math.Max(1, I(payload, "tiktok_project_concurrency", 1)),
            TiktokAnchorPromotionEnabled = B(payload, "tiktok_anchor_promotion_enabled"),
            TiktokTargetAudienceMode = FirstNonEmpty(S(payload, "tiktok_target_audience_mode"), "female") ?? "female",
            TiktokGenreCount = Math.Max(1, I(payload, "tiktok_genre_count", 1)),
            TiktokSourceLanguage = FirstNonEmpty(S(payload, "tiktok_source_language"), "zh") ?? "zh",
            TiktokIsAiDrama = payload.TryGetProperty("tiktok_is_ai_drama", out var ai) ? ai.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => B(payload, "tiktok_is_ai_drama", true),
            } : true,
            TiktokPublishMode = FirstNonEmpty(S(payload, "tiktok_publish_mode"), "auto_after_review") ?? "auto_after_review",
            TiktokConsignmentEnabled = B(payload, "tiktok_consignment_enabled"),
            TiktokProfilePreviewEpisodes = Math.Max(1, I(payload, "tiktok_profile_preview_episodes", 1)),
            TiktokFreePreviewEpisodes = Math.Max(1, I(payload, "tiktok_free_preview_episodes", 1)),
            TiktokExpectedFullPriceMode = FirstNonEmpty(S(payload, "tiktok_expected_full_price_mode"), "manual") ?? "manual",
            TiktokExpectedFullPriceOptionIndex = Math.Max(1, I(payload, "tiktok_expected_full_price_option_index", 1)),
            TiktokExpectedFullPriceValue = S(payload, "tiktok_expected_full_price_value"),
            TiktokExpectedFullPriceLabel = S(payload, "tiktok_expected_full_price_label"),
            TiktokExpectedFullPriceOptionsJson = S(payload, "tiktok_expected_full_price_options_json"),
            TiktokUploadStallSeconds = I(payload, "tiktok_upload_stall_seconds", 180),
            TiktokUploadStrategy = FirstNonEmpty(S(payload, "tiktok_upload_strategy"), "classic") ?? "classic",
            TiktokUploadBatchSize = Math.Clamp(I(payload, "tiktok_upload_batch_size", 3), 1, 20),
            TiktokUploadBatchStallSeconds = Math.Clamp(I(payload, "tiktok_upload_batch_stall_seconds", 75), 20, 600),
            TiktokUploadBatchMaxRetries = Math.Clamp(I(payload, "tiktok_upload_batch_max_retries", 3), 1, 10),
            TiktokSilenceValidationEnabled = payload.TryGetProperty("tiktok_silence_validation_enabled", out _)
                ? B(payload, "tiktok_silence_validation_enabled")
                : true,
            TiktokMaxContinuousSilenceSeconds = Math.Max(1, I(payload, "tiktok_max_continuous_silence_seconds", DefaultMaxContinuousSilenceSeconds)),
            TiktokSilenceThresholdDb = D(payload, "tiktok_silence_threshold_db", DefaultSilenceThresholdDb),
            TiktokExcelReportPath = S(payload, "tiktok_excel_report_path"),
        };

        profile.ProfileDir = AppPaths.ProfileDirFor(profile.Id);
        if (string.IsNullOrWhiteSpace(profile.TiktokStorageStatePath))
            profile.TiktokStorageStatePath = AppPaths.DefaultStorageStatePath(profile.Id);
        else
            profile.TiktokStorageStatePath = ExpandPath(profile.TiktokStorageStatePath);

        profile.LastWorkspace = ExpandPath(profile.LastWorkspace);
        profile.LastDownloadWorkspace = ExpandPath(profile.LastDownloadWorkspace);
        profile.TiktokUploadProfilePath = ExpandPath(profile.TiktokUploadProfilePath);
        return profile;
    }

    public static void ApplyToExisting(TikTokAccountProfile target, TikTokAccountProfile source)
    {
        target.Name = source.Name;
        target.UpdatedAt = source.UpdatedAt;
        target.TiktokAccountNickname = source.TiktokAccountNickname;
        target.TiktokLoginEmail = source.TiktokLoginEmail;
        target.TiktokLoginPassword = source.TiktokLoginPassword;
        target.TiktokLastLoginEmail = source.TiktokLastLoginEmail;
        target.TiktokLastLoginAt = source.TiktokLastLoginAt;
        if (!string.IsNullOrWhiteSpace(source.TiktokStorageStatePath))
            target.TiktokStorageStatePath = source.TiktokStorageStatePath;
        target.TiktokLoginBrowserMode = source.TiktokLoginBrowserMode;
        target.TiktokFingerprintBrowserCdpEndpoint = source.TiktokFingerprintBrowserCdpEndpoint;
        target.TiktokFingerprintStartCommand = source.TiktokFingerprintStartCommand;
        target.TiktokSeriesUrl = source.TiktokSeriesUrl;
        target.LastWorkspace = source.LastWorkspace;
        target.LastDownloadWorkspace = source.LastDownloadWorkspace;
        target.TiktokUploadProfilePath = source.TiktokUploadProfilePath;
        target.TiktokProxyEnabled = source.TiktokProxyEnabled;
        target.TiktokProxyType = source.TiktokProxyType;
        target.TiktokProxyHost = source.TiktokProxyHost;
        target.TiktokProxyPort = source.TiktokProxyPort;
        target.TiktokProxyUsername = source.TiktokProxyUsername;
        target.TiktokProxyPassword = source.TiktokProxyPassword;
        target.TiktokProxyLabel = source.TiktokProxyLabel;
        target.TiktokStaticIpNote = source.TiktokStaticIpNote;
        target.TiktokSubmitEnabled = source.TiktokSubmitEnabled;
        target.TiktokSubmitAction = source.TiktokSubmitAction;
        target.TiktokContractId = source.TiktokContractId;
        target.TiktokContractIdMode = source.TiktokContractIdMode;
        target.TiktokPaidEnabled = source.TiktokPaidEnabled;
        target.TiktokPaidRatioEnabled = source.TiktokPaidRatioEnabled;
        target.TiktokPaidRatioPercent = source.TiktokPaidRatioPercent;
        target.TiktokProjectConcurrency = source.TiktokProjectConcurrency;
        target.TiktokAnchorPromotionEnabled = source.TiktokAnchorPromotionEnabled;
        target.TiktokTargetAudienceMode = source.TiktokTargetAudienceMode;
        target.TiktokGenreCount = source.TiktokGenreCount;
        target.TiktokSourceLanguage = source.TiktokSourceLanguage;
        target.TiktokIsAiDrama = source.TiktokIsAiDrama;
        target.TiktokPublishMode = source.TiktokPublishMode;
        target.TiktokConsignmentEnabled = source.TiktokConsignmentEnabled;
        target.TiktokProfilePreviewEpisodes = source.TiktokProfilePreviewEpisodes;
        target.TiktokFreePreviewEpisodes = source.TiktokFreePreviewEpisodes;
        target.TiktokExpectedFullPriceMode = source.TiktokExpectedFullPriceMode;
        target.TiktokExpectedFullPriceOptionIndex = source.TiktokExpectedFullPriceOptionIndex;
        target.TiktokExpectedFullPriceValue = source.TiktokExpectedFullPriceValue;
        target.TiktokExpectedFullPriceLabel = source.TiktokExpectedFullPriceLabel;
        target.TiktokExpectedFullPriceOptionsJson = source.TiktokExpectedFullPriceOptionsJson;
        target.TiktokUploadStallSeconds = source.TiktokUploadStallSeconds;
        target.TiktokUploadStrategy = source.TiktokUploadStrategy;
        target.TiktokUploadBatchSize = source.TiktokUploadBatchSize;
        target.TiktokUploadBatchStallSeconds = source.TiktokUploadBatchStallSeconds;
        target.TiktokUploadBatchMaxRetries = source.TiktokUploadBatchMaxRetries;
        target.TiktokSilenceValidationEnabled = source.TiktokSilenceValidationEnabled;
        target.TiktokMaxContinuousSilenceSeconds = source.TiktokMaxContinuousSilenceSeconds;
        target.TiktokSilenceThresholdDb = source.TiktokSilenceThresholdDb;
        target.TiktokExcelReportPath = source.TiktokExcelReportPath;
    }

    private static string S(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static bool B(JsonElement el, string name, bool fallback = false)
    {
        if (!el.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) ? b : fallback,
            JsonValueKind.Number => value.GetInt32() != 0,
            _ => fallback,
        };
    }

    private static int I(JsonElement el, string name, int fallback = 0)
    {
        if (!el.TryGetProperty(name, out var value)) return fallback;
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.String => int.TryParse(value.GetString(), out var n) ? n : fallback,
                _ => fallback,
            };
        }
        catch { return fallback; }
    }

    private static double D(JsonElement el, string name, double fallback = 0)
    {
        if (!el.TryGetProperty(name, out var value)) return fallback;
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String => double.TryParse(value.GetString(), out var n) ? n : fallback,
                _ => fallback,
            };
        }
        catch { return fallback; }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private static string ExpandPath(string path)
    {
        var text = (path ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return "";
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
        catch { return text; }
    }

    /// <summary>将 C# 模型字段合并进 Python payload（保留 DB 里已有但 C# 未建模的字段）。</summary>
    public static void MergeIntoPythonPayload(Dictionary<string, object?> payload, TikTokAccountProfile profile)
    {
        payload["id"] = profile.Id;
        payload["name"] = profile.Name;
        if (!string.IsNullOrWhiteSpace(profile.CreatedAt))
            payload["created_at"] = profile.CreatedAt;
        payload["updated_at"] = string.IsNullOrWhiteSpace(profile.UpdatedAt)
            ? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            : profile.UpdatedAt;

        Set(payload, "tiktok_account_nickname", profile.TiktokAccountNickname);
        Set(payload, "tiktok_login_email", profile.TiktokLoginEmail);
        Set(payload, "tiktok_login_password", profile.TiktokLoginPassword);
        Set(payload, "tiktok_last_login_email", profile.TiktokLastLoginEmail);
        Set(payload, "tiktok_last_login_at", profile.TiktokLastLoginAt);
        Set(payload, "tiktok_storage_state_path", profile.TiktokStorageStatePath);
        Set(payload, "tiktok_login_browser_mode", NormalizeBrowserModeToPython(profile.TiktokLoginBrowserMode));
        Set(payload, "tiktok_fingerprint_browser_cdp_endpoint", profile.TiktokFingerprintBrowserCdpEndpoint);
        Set(payload, FingerprintStartCommandKey, profile.TiktokFingerprintStartCommand);
        Set(payload, "tiktok_series_url", profile.TiktokSeriesUrl);
        Set(payload, "last_workspace", profile.LastWorkspace);
        Set(payload, "last_download_workspace", profile.LastDownloadWorkspace);
        Set(payload, "tiktok_upload_profile_path", profile.TiktokUploadProfilePath);
        Set(payload, "tiktok_proxy_enabled", profile.TiktokProxyEnabled);
        Set(payload, "tiktok_proxy_type", profile.TiktokProxyType);
        Set(payload, "tiktok_proxy_host", profile.TiktokProxyHost);
        Set(payload, "tiktok_proxy_port", profile.TiktokProxyPort);
        Set(payload, "tiktok_proxy_username", profile.TiktokProxyUsername);
        Set(payload, "tiktok_proxy_password", profile.TiktokProxyPassword);
        Set(payload, "tiktok_proxy_label", profile.TiktokProxyLabel);
        Set(payload, "tiktok_static_ip_note", profile.TiktokStaticIpNote);
        Set(payload, "tiktok_submit_enabled", profile.TiktokSubmitEnabled);
        Set(payload, "tiktok_submit_action", profile.TiktokSubmitAction);
        Set(payload, "tiktok_contract_id", profile.TiktokContractId);
        Set(payload, "tiktok_contract_id_mode", profile.TiktokContractIdMode);
        Set(payload, "tiktok_paid_enabled", profile.TiktokPaidEnabled);
        Set(payload, "tiktok_paid_ratio_enabled", profile.TiktokPaidRatioEnabled);
        Set(payload, "tiktok_paid_ratio_percent", profile.TiktokPaidRatioPercent);
        Set(payload, "tiktok_project_concurrency", profile.TiktokProjectConcurrency);
        Set(payload, "tiktok_anchor_promotion_enabled", profile.TiktokAnchorPromotionEnabled);
        Set(payload, "tiktok_target_audience_mode", profile.TiktokTargetAudienceMode);
        Set(payload, "tiktok_genre_count", profile.TiktokGenreCount);
        Set(payload, "tiktok_source_language", profile.TiktokSourceLanguage);
        Set(payload, "tiktok_is_ai_drama", profile.TiktokIsAiDrama);
        Set(payload, "tiktok_publish_mode", profile.TiktokPublishMode);
        Set(payload, "tiktok_consignment_enabled", profile.TiktokConsignmentEnabled);
        Set(payload, "tiktok_profile_preview_episodes", profile.TiktokProfilePreviewEpisodes);
        Set(payload, "tiktok_free_preview_episodes", profile.TiktokFreePreviewEpisodes);
        Set(payload, "tiktok_expected_full_price_mode", profile.TiktokExpectedFullPriceMode);
        Set(payload, "tiktok_expected_full_price_option_index", profile.TiktokExpectedFullPriceOptionIndex);
        Set(payload, "tiktok_expected_full_price_value", profile.TiktokExpectedFullPriceValue);
        Set(payload, "tiktok_expected_full_price_label", profile.TiktokExpectedFullPriceLabel);
        Set(payload, "tiktok_expected_full_price_options_json", profile.TiktokExpectedFullPriceOptionsJson);
        Set(payload, "tiktok_upload_stall_seconds", profile.TiktokUploadStallSeconds);
        Set(payload, "tiktok_upload_strategy", profile.TiktokUploadStrategy);
        Set(payload, "tiktok_upload_batch_size", profile.TiktokUploadBatchSize);
        Set(payload, "tiktok_upload_batch_stall_seconds", profile.TiktokUploadBatchStallSeconds);
        Set(payload, "tiktok_upload_batch_max_retries", profile.TiktokUploadBatchMaxRetries);
        Set(payload, "tiktok_silence_validation_enabled", profile.TiktokSilenceValidationEnabled);
        Set(payload, "tiktok_max_continuous_silence_seconds", profile.TiktokMaxContinuousSilenceSeconds);
        Set(payload, "tiktok_silence_threshold_db", profile.TiktokSilenceThresholdDb);
        Set(payload, "tiktok_excel_report_path", profile.TiktokExcelReportPath);
    }

    private static string NormalizeBrowserModeFromPython(string? mode)
    {
        var key = (mode ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "cdp" => "cdp",
            "playwright" or "embedded" => "embedded",
            _ => "embedded",
        };
    }

    private static string NormalizeBrowserModeToPython(string? mode)
    {
        var key = (mode ?? "").Trim().ToLowerInvariant();
        return key == "cdp" ? "cdp" : "playwright";
    }

    private static void Set(Dictionary<string, object?> payload, string key, object? value) =>
        payload[key] = value;
}
