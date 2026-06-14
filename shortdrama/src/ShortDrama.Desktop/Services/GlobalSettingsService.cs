using ShortDrama.Desktop.Models;
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
            ImageModelId = snapshot.ImageModelId,
            ImageModelApiKey = snapshot.ImageModelApiKey,
            ImageModelEndpoint = snapshot.ImageModelEndpoint,
            ImageEditModelId = snapshot.ImageEditModelId,
            ImageEditApiKey = snapshot.ImageEditApiKey,
            ImageEditEndpoint = snapshot.ImageEditEndpoint,
            ImageEditPath = snapshot.ImageEditPath,
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
            AiTextEndpoint = current.AiTextEndpoint,
            AiTextApiKey = current.AiTextApiKey,
            AiTextModel = current.AiTextModel,
            AiTextTimeoutSeconds = current.AiTextTimeoutSeconds,
            AiTextMaxBatchSize = current.AiTextMaxBatchSize,
            AiTextSystemPrompt = current.AiTextSystemPrompt,
            AiTextBatchPrompt = current.AiTextBatchPrompt,
            AiTextRetryPrompt = current.AiTextRetryPrompt,
            ImageModelId = current.ImageModelId,
            ImageModelApiKey = current.ImageModelApiKey,
            ImageModelEndpoint = current.ImageModelEndpoint,
            ImageEditModelId = current.ImageEditModelId,
            ImageEditApiKey = current.ImageEditApiKey,
            ImageEditEndpoint = current.ImageEditEndpoint,
            ImageEditPath = current.ImageEditPath,
            PosterLayoutDetectPrompt = current.PosterLayoutDetectPrompt,
            PosterInpaintPrompt = current.PosterInpaintPrompt,
            PosterInpaintSafeRetryPrompt = current.PosterInpaintSafeRetryPrompt,
            PosterGenerationPrompt = current.PosterGenerationPrompt,
            PosterGenerationSafeRetryPrompt = current.PosterGenerationSafeRetryPrompt,
            PosterNameSystemPrompt = current.PosterNameSystemPrompt,
            PosterNameUserPrompt = current.PosterNameUserPrompt,
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
            FeishuNotifyStepKeysText = current.FeishuNotifyStepKeysText
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
            ImageModelId: dto.ImageModelId,
            ImageModelApiKey: dto.ImageModelApiKey,
            ImageModelEndpoint: dto.ImageModelEndpoint,
            ImageEditModelId: dto.ImageEditModelId,
            ImageEditApiKey: dto.ImageEditApiKey,
            ImageEditEndpoint: dto.ImageEditEndpoint,
            ImageEditPath: dto.ImageEditPath,
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
            FeishuNotifyStepKeysText: dto.FeishuNotifyStepKeysText);
    }
}
