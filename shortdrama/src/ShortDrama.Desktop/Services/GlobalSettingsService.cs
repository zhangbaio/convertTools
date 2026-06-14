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

    private static GlobalConfigSnapshot ToSnapshot(string settingsFilePath, GlobalDesktopSettings dto)
    {
        return new GlobalConfigSnapshot(
            SettingsFilePath: settingsFilePath,
            DramaSourceChain: dto.DramaSourceChain,
            DramaServiceOrderSearch: dto.DramaServiceOrderSearch,
            DramaServiceOrderDownload: dto.DramaServiceOrderDownload,
            DramaServiceOrderNewRelease: dto.DramaServiceOrderNewRelease,
            DramaServiceOrderRanking: dto.DramaServiceOrderRanking,
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
            PosterNameUserPrompt: dto.PosterNameUserPrompt);
    }
}
