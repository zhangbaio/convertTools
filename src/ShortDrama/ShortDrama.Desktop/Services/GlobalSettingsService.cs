using ShortDrama.Desktop.Models;
using System.Globalization;
using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class GlobalSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public GlobalConfigSnapshot Load()
    {
        var settingsFilePath = GetSettingsFilePath();
        var dto = LoadSettings(settingsFilePath);
        var legacy = LoadLegacySettings();
        dto = MergeLegacySettings(dto, legacy, preferLegacyDefaults: !File.Exists(settingsFilePath));
        return ToSnapshot(settingsFilePath, dto);
    }

    public void Save(GlobalConfigSnapshot snapshot)
    {
        var settingsFilePath = string.IsNullOrWhiteSpace(snapshot.SettingsFilePath)
            ? GetSettingsFilePath()
            : snapshot.SettingsFilePath;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        var payload = new GlobalDesktopSettings
        {
            DramaSourceChain = snapshot.DramaSourceChain,
            DramaServiceOrderSearch = snapshot.DramaServiceOrderSearch,
            DramaServiceOrderDownload = snapshot.DramaServiceOrderDownload,
            DramaServiceOrderNewRelease = snapshot.DramaServiceOrderNewRelease,
            DramaServiceOrderRanking = snapshot.DramaServiceOrderRanking,
            XingeEnabled = snapshot.XingeEnabled,
            XingeServerUrl = snapshot.XingeServerUrl,
            XingeUsername = snapshot.XingeUsername,
            XingePassword = snapshot.XingePassword,
            XingeClientId = snapshot.XingeClientId,
            XingeClientToken = snapshot.XingeClientToken,
            XingeUserRole = snapshot.XingeUserRole,
            XingeClientName = snapshot.XingeClientName,
            XingeWsEnabled = snapshot.XingeWsEnabled,
            XingePollIntervalSeconds = int.TryParse(snapshot.XingePollIntervalSeconds, out var xingePollIntervalSeconds) && xingePollIntervalSeconds > 0
                ? xingePollIntervalSeconds
                : 3,
            XingeUploadLoginQr = snapshot.XingeUploadLoginQr,
            HgnewAccount = snapshot.HgnewAccount,
            HgnewPassword = snapshot.HgnewPassword,
            HgnewUdid = snapshot.HgnewUdid,
            HgnewClientVersion = snapshot.HgnewClientVersion,
            HongguoLocalBaseUrl = snapshot.HongguoLocalBaseUrl,
            HongguoLocalApiKey = snapshot.HongguoLocalApiKey,
            PikachuServerUrl = snapshot.PikachuServerUrl,
            PikachuFanqieCookie = snapshot.PikachuFanqieCookie,
            PikachuDramaType = snapshot.PikachuDramaType,
            AiTextEndpoint = snapshot.AiTextEndpoint,
            AiTextApiKey = snapshot.AiTextApiKey,
            AiTextModel = snapshot.AiTextModel,
            AiTextTimeoutSeconds = snapshot.AiTextTimeoutSeconds,
            AiTextMaxBatchSize = snapshot.AiTextMaxBatchSize,
            AiTextSystemPrompt = snapshot.AiTextSystemPrompt,
            AiTextBatchPrompt = snapshot.AiTextBatchPrompt,
            AiTextRetryPrompt = snapshot.AiTextRetryPrompt,
            AiTitleSystemPrompt = snapshot.AiTitleSystemPrompt,
            AiTitleBatchPrompt = snapshot.AiTitleBatchPrompt,
            AiTagSystemPrompt = snapshot.AiTagSystemPrompt,
            AiTagBatchPrompt = snapshot.AiTagBatchPrompt,
            AiFullInfoSystemPrompt = snapshot.AiFullInfoSystemPrompt,
            AiFullInfoBatchPrompt = snapshot.AiFullInfoBatchPrompt,
            AiFullInfoRetryPrompt = snapshot.AiFullInfoRetryPrompt,
            ImageModelId = snapshot.ImageModelId,
            ImageModelApiKey = snapshot.ImageModelApiKey,
            ImageModelEndpoint = snapshot.ImageModelEndpoint,
            ImageEditModelId = snapshot.ImageEditModelId,
            ImageEditApiKey = snapshot.ImageEditApiKey,
            ImageEditEndpoint = snapshot.ImageEditEndpoint,
            ImageEditPath = snapshot.ImageEditPath,
            FrameCoverPrompt = snapshot.FrameCoverPrompt,
            PosterLayoutDetectPrompt = snapshot.PosterLayoutDetectPrompt,
            PosterInpaintPrompt = snapshot.PosterInpaintPrompt,
            PosterInpaintSafeRetryPrompt = snapshot.PosterInpaintSafeRetryPrompt,
            PosterGenerationPrompt = snapshot.PosterGenerationPrompt,
            PosterGenerationSafeRetryPrompt = snapshot.PosterGenerationSafeRetryPrompt,
            PosterNameSystemPrompt = snapshot.PosterNameSystemPrompt,
            PosterNameUserPrompt = snapshot.PosterNameUserPrompt,
            FeishuNotificationEnabled = snapshot.FeishuNotificationEnabled,
            FeishuAppId = snapshot.FeishuAppId,
            FeishuAppSecret = snapshot.FeishuAppSecret,
            FeishuReceiveId = snapshot.FeishuReceiveId,
            FeishuReceiveIdType = snapshot.FeishuReceiveIdType,
            FeishuNotifyOnStepStart = snapshot.FeishuNotifyOnStepStart,
            FeishuNotifyOnStepSuccess = snapshot.FeishuNotifyOnStepSuccess,
            FeishuNotifyOnStepFailure = snapshot.FeishuNotifyOnStepFailure,
            FeishuNotifyOnQueueSummary = snapshot.FeishuNotifyOnQueueSummary,
            FeishuNotifyOnLoginQr = snapshot.FeishuNotifyOnLoginQr,
            FeishuNotifyStepKeysText = snapshot.FeishuNotifyStepKeysText,
            LastMaterialClipWorkspace = snapshot.LastMaterialClipWorkspace,
            MaterialClipAsrProvider = snapshot.MaterialClipAsrProvider,
            MaterialClipAsrLanguage = snapshot.MaterialClipAsrLanguage,
            MaterialClipVolcengineAppId = snapshot.MaterialClipVolcengineAppId,
            MaterialClipVolcengineAccessToken = snapshot.MaterialClipVolcengineAccessToken,
            MaterialClipDoubaoAppId = snapshot.MaterialClipDoubaoAppId,
            MaterialClipDoubaoAccessToken = snapshot.MaterialClipDoubaoAccessToken,
            MaterialClipAsrEngine = snapshot.MaterialClipAsrEngine,
            MaterialClipAsrLocalModel = snapshot.MaterialClipAsrLocalModel,
            MaterialClipAsrLocalModelDir = snapshot.MaterialClipAsrLocalModelDir,
            MaterialClipAsrLocalVadPath = snapshot.MaterialClipAsrLocalVadPath,
            MaterialClipAsrLocalUseItn = snapshot.MaterialClipAsrLocalUseItn,
            MaterialClipAsrHybridMinCharsPerSec = double.TryParse(snapshot.MaterialClipAsrHybridMinCharsPerSec, NumberStyles.Float, CultureInfo.InvariantCulture, out var materialClipAsrHybridMinCharsPerSec) && materialClipAsrHybridMinCharsPerSec >= 0
                ? materialClipAsrHybridMinCharsPerSec
                : 1.0d,
            MaterialClipMode = snapshot.MaterialClipMode,
            MaterialClipTargetDurationMode = snapshot.MaterialClipTargetDurationMode,
            MaterialClipTargetDurationSec = int.TryParse(snapshot.MaterialClipTargetDurationSec, out var materialClipTargetDurationSec) ? materialClipTargetDurationSec : 30,
            MaterialClipTargetDurationRatioPercent = double.TryParse(snapshot.MaterialClipTargetDurationRatioPercent, out var materialClipTargetDurationRatioPercent) ? materialClipTargetDurationRatioPercent : 8.0d,
            MaterialClipMinOutputDurationSec = int.TryParse(snapshot.MaterialClipMinOutputDurationSec, out var materialClipMinOutputDurationSec) ? materialClipMinOutputDurationSec : 0,
            MaterialClipMaxOutputDurationSec = int.TryParse(snapshot.MaterialClipMaxOutputDurationSec, out var materialClipMaxOutputDurationSec) ? materialClipMaxOutputDurationSec : 45,
            MaterialClipPerEpisodeTopN = int.TryParse(snapshot.MaterialClipPerEpisodeTopN, out var materialClipPerEpisodeTopN) ? materialClipPerEpisodeTopN : 2,
            MaterialClipEnableLlm = snapshot.MaterialClipEnableLlm,
            MaterialClipSplitClipLimit = int.TryParse(snapshot.MaterialClipSplitClipLimit, out var materialClipSplitClipLimit) ? materialClipSplitClipLimit : 4,
        };

        File.WriteAllText(settingsFilePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static string GetSettingsFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shortdrama-desktop")
            : Path.Combine(appData, "ShortDramaDesktop");

        return Path.Combine(baseDir, "global-settings.json");
    }

    private static GlobalDesktopSettings LoadSettings(string settingsFilePath)
    {
        if (!File.Exists(settingsFilePath))
        {
            return new GlobalDesktopSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsFilePath);
            return JsonSerializer.Deserialize<GlobalDesktopSettings>(json, JsonOptions) ?? new GlobalDesktopSettings();
        }
        catch
        {
            return new GlobalDesktopSettings();
        }
    }

    private static Dictionary<string, string> LoadLegacySettings()
    {
        var legacyPath = GetLegacySettingsFilePath();
        if (!File.Exists(legacyPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => string.Empty
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    result[property.Name] = value.Trim();
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetLegacySettingsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "settings.json");
    }

    private static GlobalDesktopSettings MergeLegacySettings(
        GlobalDesktopSettings current,
        IReadOnlyDictionary<string, string> legacy,
        bool preferLegacyDefaults)
    {
        if (legacy.Count == 0)
        {
            return current;
        }

        string PickString(string currentValue, string legacyKey, string defaultValue = "")
        {
            if (!legacy.TryGetValue(legacyKey, out var legacyValue) || string.IsNullOrWhiteSpace(legacyValue))
            {
                return currentValue;
            }

            if (string.IsNullOrWhiteSpace(currentValue))
            {
                return legacyValue;
            }

            return preferLegacyDefaults && string.Equals(currentValue, defaultValue, StringComparison.Ordinal)
                ? legacyValue
                : currentValue;
        }

        string PickStringMany(string currentValue, string defaultValue = "", params string[] legacyKeys)
        {
            foreach (var legacyKey in legacyKeys)
            {
                var next = PickString(currentValue, legacyKey, defaultValue);
                if (!string.Equals(next, currentValue, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(next))
                {
                    return next;
                }
            }

            return currentValue;
        }

        string NormalizeOrder(string raw, params string[] allowed)
        {
            var items = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Trim().ToLowerInvariant())
                .Where(item => allowed.Contains(item, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return items.Count == 0 ? string.Join(',', allowed) : string.Join(',', items);
        }

        var mergedDramaSourceChain = PickString(current.DramaSourceChain, "drama_source_chain", "hgnew");
        if (mergedDramaSourceChain is not ("hgnew" or "hglocal" or "pikachu"))
        {
            mergedDramaSourceChain = "hgnew";
        }

        return new GlobalDesktopSettings
        {
            DramaSourceChain = mergedDramaSourceChain,
            DramaServiceOrderSearch = NormalizeOrder(PickString(current.DramaServiceOrderSearch, "drama_service_order_search", "hgnew,hglocal,pikachu"), "hgnew", "hglocal", "pikachu"),
            DramaServiceOrderDownload = NormalizeOrder(PickString(current.DramaServiceOrderDownload, "drama_service_order_download", "hgnew,hglocal,pikachu"), "hgnew", "hglocal", "pikachu"),
            DramaServiceOrderNewRelease = NormalizeOrder(PickString(current.DramaServiceOrderNewRelease, "drama_service_order_new_release", "hgnew,hglocal"), "hgnew", "hglocal"),
            DramaServiceOrderRanking = NormalizeOrder(PickString(current.DramaServiceOrderRanking, "drama_service_order_ranking", "hglocal,pikachu"), "hglocal", "pikachu"),
            XingeEnabled = current.XingeEnabled,
            XingeServerUrl = current.XingeServerUrl,
            XingeUsername = current.XingeUsername,
            XingePassword = current.XingePassword,
            XingeClientId = current.XingeClientId,
            XingeClientToken = current.XingeClientToken,
            XingeUserRole = current.XingeUserRole,
            XingeClientName = current.XingeClientName,
            XingeWsEnabled = current.XingeWsEnabled,
            XingePollIntervalSeconds = current.XingePollIntervalSeconds,
            XingeUploadLoginQr = current.XingeUploadLoginQr,
            HgnewAccount = PickString(current.HgnewAccount, "hgnew_account"),
            HgnewPassword = PickString(current.HgnewPassword, "hgnew_password"),
            HgnewUdid = PickString(current.HgnewUdid, "hgnew_udid"),
            HgnewClientVersion = PickString(current.HgnewClientVersion, "hgnew_client_version", "1.3.4"),
            HongguoLocalBaseUrl = PickString(current.HongguoLocalBaseUrl, "hongguo_local_base_url"),
            HongguoLocalApiKey = PickString(current.HongguoLocalApiKey, "hongguo_local_api_key"),
            PikachuServerUrl = PickString(current.PikachuServerUrl, "pikachu_server_url", "http://8.138.192.128/start-prod-api"),
            PikachuFanqieCookie = PickString(current.PikachuFanqieCookie, "pikachu_fanqie_cookie"),
            PikachuDramaType = PickString(current.PikachuDramaType, "pikachu_drama_type", "short"),
            AiTextEndpoint = PickString(current.AiTextEndpoint, "ai_text_endpoint"),
            AiTextApiKey = PickString(current.AiTextApiKey, "ai_text_api_key"),
            AiTextModel = PickString(current.AiTextModel, "ai_text_model"),
            AiTextTimeoutSeconds = PickString(current.AiTextTimeoutSeconds, "ai_text_timeout_seconds"),
            AiTextMaxBatchSize = PickString(current.AiTextMaxBatchSize, "ai_text_max_batch_size"),
            AiTextSystemPrompt = PickStringMany(current.AiTextSystemPrompt, current.AiTextSystemPrompt, "ai_text_system_prompt", "ai_full_info_system_prompt"),
            AiTextBatchPrompt = PickStringMany(current.AiTextBatchPrompt, current.AiTextBatchPrompt, "ai_text_batch_prompt", "ai_full_info_batch_prompt"),
            AiTextRetryPrompt = PickStringMany(current.AiTextRetryPrompt, current.AiTextRetryPrompt, "ai_text_retry_prompt", "ai_full_info_retry_prompt"),
            AiTitleSystemPrompt = PickString(current.AiTitleSystemPrompt, "ai_title_system_prompt"),
            AiTitleBatchPrompt = PickString(current.AiTitleBatchPrompt, "ai_title_batch_prompt"),
            AiTagSystemPrompt = PickString(current.AiTagSystemPrompt, "ai_tag_system_prompt"),
            AiTagBatchPrompt = PickString(current.AiTagBatchPrompt, "ai_tag_batch_prompt"),
            AiFullInfoSystemPrompt = PickString(current.AiFullInfoSystemPrompt, "ai_full_info_system_prompt"),
            AiFullInfoBatchPrompt = PickString(current.AiFullInfoBatchPrompt, "ai_full_info_batch_prompt"),
            AiFullInfoRetryPrompt = PickString(current.AiFullInfoRetryPrompt, "ai_full_info_retry_prompt"),
            ImageModelId = PickString(current.ImageModelId, "image_model_id"),
            ImageModelApiKey = PickString(current.ImageModelApiKey, "image_model_api_key"),
            ImageModelEndpoint = PickString(current.ImageModelEndpoint, "image_model_endpoint"),
            ImageEditModelId = PickString(current.ImageEditModelId, "image_edit_model_id"),
            ImageEditApiKey = PickString(current.ImageEditApiKey, "image_edit_api_key"),
            ImageEditEndpoint = PickString(current.ImageEditEndpoint, "image_edit_endpoint"),
            ImageEditPath = PickString(current.ImageEditPath, "image_edit_path"),
            FrameCoverPrompt = PickString(current.FrameCoverPrompt, "frame_cover_prompt"),
            PosterLayoutDetectPrompt = PickString(current.PosterLayoutDetectPrompt, "poster_layout_detect_prompt"),
            PosterInpaintPrompt = PickString(current.PosterInpaintPrompt, "poster_inpaint_prompt"),
            PosterInpaintSafeRetryPrompt = PickString(current.PosterInpaintSafeRetryPrompt, "poster_inpaint_safe_retry_prompt"),
            PosterGenerationPrompt = PickString(current.PosterGenerationPrompt, "poster_generation_prompt"),
            PosterGenerationSafeRetryPrompt = PickString(current.PosterGenerationSafeRetryPrompt, "poster_generation_safe_retry_prompt"),
            PosterNameSystemPrompt = PickString(current.PosterNameSystemPrompt, "poster_name_system_prompt"),
            PosterNameUserPrompt = PickString(current.PosterNameUserPrompt, "poster_name_user_prompt"),
            FeishuNotificationEnabled = current.FeishuNotificationEnabled,
            FeishuAppId = current.FeishuAppId,
            FeishuAppSecret = current.FeishuAppSecret,
            FeishuReceiveId = current.FeishuReceiveId,
            FeishuReceiveIdType = current.FeishuReceiveIdType,
            FeishuNotifyOnStepStart = current.FeishuNotifyOnStepStart,
            FeishuNotifyOnStepSuccess = current.FeishuNotifyOnStepSuccess,
            FeishuNotifyOnStepFailure = current.FeishuNotifyOnStepFailure,
            FeishuNotifyOnQueueSummary = current.FeishuNotifyOnQueueSummary,
            FeishuNotifyOnLoginQr = current.FeishuNotifyOnLoginQr,
            FeishuNotifyStepKeysText = current.FeishuNotifyStepKeysText,
            LastMaterialClipWorkspace = current.LastMaterialClipWorkspace,
            MaterialClipAsrProvider = current.MaterialClipAsrProvider,
            MaterialClipAsrLanguage = current.MaterialClipAsrLanguage,
            MaterialClipVolcengineAppId = current.MaterialClipVolcengineAppId,
            MaterialClipVolcengineAccessToken = current.MaterialClipVolcengineAccessToken,
            MaterialClipDoubaoAppId = current.MaterialClipDoubaoAppId,
            MaterialClipDoubaoAccessToken = current.MaterialClipDoubaoAccessToken,
            MaterialClipAsrEngine = current.MaterialClipAsrEngine,
            MaterialClipAsrLocalModel = current.MaterialClipAsrLocalModel,
            MaterialClipAsrLocalModelDir = current.MaterialClipAsrLocalModelDir,
            MaterialClipAsrLocalVadPath = current.MaterialClipAsrLocalVadPath,
            MaterialClipAsrLocalUseItn = current.MaterialClipAsrLocalUseItn,
            MaterialClipAsrHybridMinCharsPerSec = current.MaterialClipAsrHybridMinCharsPerSec,
            MaterialClipMode = current.MaterialClipMode,
            MaterialClipTargetDurationMode = current.MaterialClipTargetDurationMode,
            MaterialClipTargetDurationSec = current.MaterialClipTargetDurationSec,
            MaterialClipTargetDurationRatioPercent = current.MaterialClipTargetDurationRatioPercent,
            MaterialClipMinOutputDurationSec = current.MaterialClipMinOutputDurationSec,
            MaterialClipMaxOutputDurationSec = current.MaterialClipMaxOutputDurationSec,
            MaterialClipPerEpisodeTopN = current.MaterialClipPerEpisodeTopN,
            MaterialClipEnableLlm = current.MaterialClipEnableLlm,
            MaterialClipSplitClipLimit = current.MaterialClipSplitClipLimit
        };
    }

    private static GlobalConfigSnapshot ToSnapshot(string settingsFilePath, GlobalDesktopSettings dto)
    {
        return new GlobalConfigSnapshot(
            SettingsFilePath: settingsFilePath,
            DramaSourceChain: dto.DramaSourceChain,
            DramaServiceOrderSearch: dto.DramaServiceOrderSearch,
            DramaServiceOrderDownload: dto.DramaServiceOrderDownload,
            DramaServiceOrderNewRelease: dto.DramaServiceOrderNewRelease,
            DramaServiceOrderRanking: dto.DramaServiceOrderRanking,
            XingeEnabled: dto.XingeEnabled,
            XingeServerUrl: dto.XingeServerUrl,
            XingeUsername: dto.XingeUsername,
            XingePassword: dto.XingePassword,
            XingeClientId: dto.XingeClientId,
            XingeClientToken: dto.XingeClientToken,
            XingeUserRole: dto.XingeUserRole,
            XingeClientName: dto.XingeClientName,
            XingeWsEnabled: dto.XingeWsEnabled,
            XingePollIntervalSeconds: Math.Max(1, dto.XingePollIntervalSeconds).ToString(),
            XingeUploadLoginQr: dto.XingeUploadLoginQr,
            HgnewAccount: dto.HgnewAccount,
            HgnewPassword: dto.HgnewPassword,
            HgnewUdid: dto.HgnewUdid,
            HgnewClientVersion: dto.HgnewClientVersion,
            HongguoLocalBaseUrl: dto.HongguoLocalBaseUrl,
            HongguoLocalApiKey: dto.HongguoLocalApiKey,
            PikachuServerUrl: dto.PikachuServerUrl,
            PikachuFanqieCookie: dto.PikachuFanqieCookie,
            PikachuDramaType: dto.PikachuDramaType,
            AiTextEndpoint: dto.AiTextEndpoint,
            AiTextApiKey: dto.AiTextApiKey,
            AiTextModel: dto.AiTextModel,
            AiTextTimeoutSeconds: dto.AiTextTimeoutSeconds,
            AiTextMaxBatchSize: dto.AiTextMaxBatchSize,
            AiTextSystemPrompt: dto.AiTextSystemPrompt,
            AiTextBatchPrompt: dto.AiTextBatchPrompt,
            AiTextRetryPrompt: dto.AiTextRetryPrompt,
            AiTitleSystemPrompt: dto.AiTitleSystemPrompt,
            AiTitleBatchPrompt: dto.AiTitleBatchPrompt,
            AiTagSystemPrompt: dto.AiTagSystemPrompt,
            AiTagBatchPrompt: dto.AiTagBatchPrompt,
            AiFullInfoSystemPrompt: dto.AiFullInfoSystemPrompt,
            AiFullInfoBatchPrompt: dto.AiFullInfoBatchPrompt,
            AiFullInfoRetryPrompt: dto.AiFullInfoRetryPrompt,
            ImageModelId: dto.ImageModelId,
            ImageModelApiKey: dto.ImageModelApiKey,
            ImageModelEndpoint: dto.ImageModelEndpoint,
            ImageEditModelId: dto.ImageEditModelId,
            ImageEditApiKey: dto.ImageEditApiKey,
            ImageEditEndpoint: dto.ImageEditEndpoint,
            ImageEditPath: dto.ImageEditPath,
            FrameCoverPrompt: dto.FrameCoverPrompt,
            PosterLayoutDetectPrompt: dto.PosterLayoutDetectPrompt,
            PosterInpaintPrompt: dto.PosterInpaintPrompt,
            PosterInpaintSafeRetryPrompt: dto.PosterInpaintSafeRetryPrompt,
            PosterGenerationPrompt: dto.PosterGenerationPrompt,
            PosterGenerationSafeRetryPrompt: dto.PosterGenerationSafeRetryPrompt,
            PosterNameSystemPrompt: dto.PosterNameSystemPrompt,
            PosterNameUserPrompt: dto.PosterNameUserPrompt,
            FeishuNotificationEnabled: dto.FeishuNotificationEnabled,
            FeishuAppId: dto.FeishuAppId,
            FeishuAppSecret: dto.FeishuAppSecret,
            FeishuReceiveId: dto.FeishuReceiveId,
            FeishuReceiveIdType: dto.FeishuReceiveIdType,
            FeishuNotifyOnStepStart: dto.FeishuNotifyOnStepStart,
            FeishuNotifyOnStepSuccess: dto.FeishuNotifyOnStepSuccess,
            FeishuNotifyOnStepFailure: dto.FeishuNotifyOnStepFailure,
            FeishuNotifyOnQueueSummary: dto.FeishuNotifyOnQueueSummary,
            FeishuNotifyOnLoginQr: dto.FeishuNotifyOnLoginQr,
            FeishuNotifyStepKeysText: dto.FeishuNotifyStepKeysText,
            LastMaterialClipWorkspace: dto.LastMaterialClipWorkspace,
            MaterialClipAsrProvider: dto.MaterialClipAsrProvider,
            MaterialClipAsrLanguage: dto.MaterialClipAsrLanguage,
            MaterialClipVolcengineAppId: dto.MaterialClipVolcengineAppId,
            MaterialClipVolcengineAccessToken: dto.MaterialClipVolcengineAccessToken,
            MaterialClipDoubaoAppId: dto.MaterialClipDoubaoAppId,
            MaterialClipDoubaoAccessToken: dto.MaterialClipDoubaoAccessToken,
            MaterialClipMode: dto.MaterialClipMode,
            MaterialClipTargetDurationMode: dto.MaterialClipTargetDurationMode,
            MaterialClipTargetDurationSec: dto.MaterialClipTargetDurationSec.ToString(),
            MaterialClipTargetDurationRatioPercent: dto.MaterialClipTargetDurationRatioPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialClipMinOutputDurationSec: dto.MaterialClipMinOutputDurationSec.ToString(),
            MaterialClipMaxOutputDurationSec: dto.MaterialClipMaxOutputDurationSec.ToString(),
            MaterialClipPerEpisodeTopN: dto.MaterialClipPerEpisodeTopN.ToString(),
            MaterialClipEnableLlm: dto.MaterialClipEnableLlm,
            MaterialClipSplitClipLimit: dto.MaterialClipSplitClipLimit.ToString(),
            MaterialClipAsrEngine: dto.MaterialClipAsrEngine,
            MaterialClipAsrLocalModel: dto.MaterialClipAsrLocalModel,
            MaterialClipAsrLocalModelDir: dto.MaterialClipAsrLocalModelDir,
            MaterialClipAsrLocalVadPath: dto.MaterialClipAsrLocalVadPath,
            MaterialClipAsrLocalUseItn: dto.MaterialClipAsrLocalUseItn,
            MaterialClipAsrHybridMinCharsPerSec: dto.MaterialClipAsrHybridMinCharsPerSec.ToString("0.###", CultureInfo.InvariantCulture));
    }
}
