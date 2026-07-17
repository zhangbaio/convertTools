using ShortDrama.Desktop.Models;
using ShortDrama.Infrastructure.Imaging;
using ShortDrama.Infrastructure.Media;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopConfigService
{
    private readonly GlobalSettingsService _globalSettingsService;

    public DesktopConfigService(GlobalSettingsService globalSettingsService)
    {
        _globalSettingsService = globalSettingsService;
    }

    public DesktopConfigSnapshot Load(string rootDir)
    {
        var configFilePath = GetConfigFilePath(rootDir);
        var configDir = GetConfigDirectoryPath(rootDir);
        var resolvedPath = ResolveExistingConfigPath(rootDir);
        var map = resolvedPath is not null
            ? ReadConfigMap(resolvedPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var project = BuildProjectSnapshot(configFilePath, configDir, map);
        var global = _globalSettingsService.Load();
        return BuildMergedSnapshot(project, global, configDir, map);
    }

    public ProjectConfigSnapshot LoadProject(string rootDir)
    {
        var configFilePath = GetConfigFilePath(rootDir);
        var configDir = GetConfigDirectoryPath(rootDir);
        var resolvedPath = ResolveExistingConfigPath(rootDir);
        var map = resolvedPath is not null
            ? ReadConfigMap(resolvedPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var project = BuildProjectSnapshot(configFilePath, configDir, map);
        return project;
    }

    public GlobalConfigSnapshot LoadGlobal()
    {
        return _globalSettingsService.Load();
    }

    public void SaveGlobal(GlobalConfigSnapshot global)
    {
        _globalSettingsService.Save(global);
    }

    public DesktopConfigSnapshot BuildMergedSnapshot(ProjectConfigSnapshot project, GlobalConfigSnapshot global)
    {
        var configDir = Path.GetDirectoryName(project.ConfigFilePath) ?? string.Empty;
        return BuildMergedSnapshot(project, global, configDir, null);
    }

    public void Save(ProjectConfigSnapshot project, GlobalConfigSnapshot global)
    {
        SaveProject(project, global);
        _globalSettingsService.Save(global);
    }

    public void Save(DesktopConfigSnapshot config)
    {
        var project = new ProjectConfigSnapshot(
            ConfigFilePath: config.ConfigFilePath,
            CompanyName: config.CompanyName,
            SearchPageSize: config.SearchPageSize,
            TemplateDocxPath: config.TemplateDocxPath,
            CostReportBaseImagePath: config.CostReportBaseImagePath,
            CostReportActorPayRatio: config.CostReportActorPayRatio,
            CostReportLegalRepresentative: config.CostReportLegalRepresentative,
            WeixinHeadless: config.WeixinHeadless,
            WeixinSlowMoMs: config.WeixinSlowMoMs,
            WeixinKeepOpenSeconds: config.WeixinKeepOpenSeconds,
            WeixinLoginTimeoutSeconds: config.WeixinLoginTimeoutSeconds,
            WeixinSubmitEnabled: config.WeixinSubmitEnabled,
            WeixinPauseOnError: config.WeixinPauseOnError,
            WeixinSaveHtml: config.WeixinSaveHtml,
            WeixinSaveText: config.WeixinSaveText,
            WeixinMonetizationType: config.WeixinMonetizationType,
            WeixinDramaType: config.WeixinDramaType,
            WeixinDramaQualification: config.WeixinDramaQualification,
            WeixinSubmitterIdentity: config.WeixinSubmitterIdentity,
            WeixinTrialEpisodes: config.WeixinTrialEpisodes,
            WeixinFillRecommendation: config.WeixinFillRecommendation,
            WeixinSubmissionReportDir: config.WeixinSubmissionReportDir,
            ProjectImageGenerationMode: "image_template",
            ProjectImageTemplateRoot: string.Empty,
            ProjectImageTemplateId: string.Empty,
            ProjectImageTemplateDir: config.ProjectImageTemplateDir,
            ProjectImageCount: config.ProjectImageCount,
            ChatModelId: config.ChatModelId,
            ChatModelApiKey: config.ChatModelApiKey,
            ChatModelEndpoint: config.ChatModelEndpoint,
            VideoRes: config.VideoRes,
            VideoBitrateBps: config.VideoBitrateBps,
            VideoBitrateMode: config.VideoBitrateMode,
            VideoAudioBitrateBps: config.VideoAudioBitrateBps,
            VideoFps: config.VideoFps,
            VideoConcurrentCount: config.VideoConcurrentCount,
            VideoUseHardwareEncoder: config.VideoUseHardwareEncoder,
            VideoEncoder: string.Empty,
            VideoPreset: string.Empty,
            NvencCq: string.Empty,
            NvencMaxParallel: string.Empty,
            VerboseTranscodeLogEnabled: false,
            SkipBitrateDownscaleForHighBitrate: false,
            UploadTargetVideoBitrateMbps: string.Empty,
            UploadMaxVideoBitrateMbps: string.Empty,
            UploadMinVideoBitrateMbps: string.Empty,
            UploadAudioBitrateKbps: string.Empty,
            UploadBitrateFallbackEnabled: false,
            UploadBitrateFallbackVideoBitrateMbps: string.Empty,
            UploadBitrateProfilesJson: string.Empty,
            VideoNameTemplate: config.VideoNameTemplate,
            MaterialConvertEnabled: config.MaterialConvertEnabled,
            MaterialTrimHeadSeconds: config.MaterialTrimHeadSeconds,
            MaterialTrimTailSeconds: config.MaterialTrimTailSeconds,
            MaterialSpeedPercent: config.MaterialSpeedPercent,
            MaterialDynamicSpeedEnabled: false,
            MaterialDynamicSpeedPresetName: "light_rhythm",
            MaterialDynamicSpeedHeadSeconds: "2.5",
            MaterialDynamicSpeedHeadPercent: "8",
            MaterialDynamicSpeedMiddlePercent: "6",
            MaterialDynamicSpeedTailSeconds: "2.5",
            MaterialDynamicSpeedTailPercent: "8",
            MaterialFrameSamplingEnabled: int.TryParse(config.MaterialDropCount, out var materialDropCount) ? materialDropCount > 0 : true,
            MaterialFrameSamplingMode: "fixed_interval",
            MaterialFrameSamplingInterval: string.IsNullOrWhiteSpace(config.MaterialDropEveryNFrames) ? "20" : config.MaterialDropEveryNFrames,
            MaterialDropEveryNFrames: config.MaterialDropEveryNFrames,
            MaterialDropCount: config.MaterialDropCount,
            MaterialCropWidthPercent: config.MaterialCropWidthPercent,
            MaterialCropHeightPercent: config.MaterialCropHeightPercent,
            MaterialForegroundZoomPercent: "0",
            MaterialDedupEnabled: false,
            MaterialDedupColorEnabled: false,
            MaterialDedupNoiseEnabled: false,
            MaterialDedupAudioEnabled: false,
            MaterialDedupMetadataEnabled: false,
            MaterialDedupRotateEnabled: false,
            MaterialDedupVignetteEnabled: false,
            MaterialDedupFadeInEnabled: false,
            MaterialWatermarkEnabled: false,
            MaterialWatermarkText: string.Empty,
            MaterialWatermarkFontSize: "35",
            MaterialWatermarkPosition: "top_right",
            MaterialWatermarkMarginX: "30",
            MaterialWatermarkMarginY: "30",
            MaterialOutputWidth: "1080",
            MaterialOutputHeight: "1920",
            MaterialPipWidthPercent: "100",
            MaterialPipHeightPercent: "100");

        var existingGlobal = _globalSettingsService.Load();
        var global = existingGlobal with
        {
            AiTextEndpoint = config.AiTextEndpoint,
            AiTextApiKey = config.AiTextApiKey,
            AiTextModel = config.AiTextModel,
            AiTextTimeoutSeconds = config.AiTextTimeoutSeconds,
            AiTextMaxBatchSize = config.AiTextMaxBatchSize,
            AiTextSystemPrompt = config.AiTextSystemPrompt,
            AiTextBatchPrompt = config.AiTextBatchPrompt,
            AiTextRetryPrompt = config.AiTextRetryPrompt,
            AiTitleSystemPrompt = config.AiTitleSystemPrompt,
            AiTitleBatchPrompt = config.AiTitleBatchPrompt,
            AiTagSystemPrompt = config.AiTagSystemPrompt,
            AiTagBatchPrompt = config.AiTagBatchPrompt,
            AiFullInfoSystemPrompt = config.AiFullInfoSystemPrompt,
            AiFullInfoBatchPrompt = config.AiFullInfoBatchPrompt,
            AiFullInfoRetryPrompt = config.AiFullInfoRetryPrompt,
            ImageModelId = config.ImageModelId,
            ImageModelApiKey = config.ImageModelApiKey,
            ImageModelEndpoint = config.ImageModelEndpoint,
            ImageEditModelId = config.ImageEditModelId,
            ImageEditApiKey = config.ImageEditApiKey,
            ImageEditEndpoint = config.ImageEditEndpoint,
            ImageEditPath = config.ImageEditPath,
            FrameCoverPrompt = config.FrameCoverPrompt,
            PosterLayoutDetectPrompt = config.PosterLayoutDetectPrompt,
            PosterInpaintPrompt = config.PosterInpaintPrompt,
            PosterInpaintSafeRetryPrompt = config.PosterInpaintSafeRetryPrompt,
            PosterGenerationPrompt = config.PosterGenerationPrompt,
            PosterGenerationSafeRetryPrompt = config.PosterGenerationSafeRetryPrompt,
            PosterNameSystemPrompt = config.PosterNameSystemPrompt,
            PosterNameUserPrompt = config.PosterNameUserPrompt
        };

        Save(project, global);
    }

    public void SaveProject(ProjectConfigSnapshot config, GlobalConfigSnapshot? global = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.ConfigFilePath)!);

        var effectiveGlobal = global ?? _globalSettingsService.Load();
        var merged = BuildMergedSnapshot(config, effectiveGlobal, Path.GetDirectoryName(config.ConfigFilePath) ?? string.Empty, null);
        var payload = BuildProjectConfigPayload(config, effectiveGlobal, merged);
        File.WriteAllText(config.ConfigFilePath, SerializeProjectConfigJson(payload), Encoding.UTF8);
    }

    public static string GetConfigFilePath(string rootDir)
    {
        return Path.Combine(GetConfigDirectoryPath(rootDir), "config.json");
    }

    public static string GetConfigDirectoryPath(string rootDir)
    {
        return Path.Combine(rootDir, "config");
    }

    private static string? ResolveExistingConfigPath(string rootDir)
    {
        var configDir = GetConfigDirectoryPath(rootDir);
        var jsonPath = Path.Combine(configDir, "config.json");
        return File.Exists(jsonPath) ? jsonPath : null;
    }

    private static string SerializeProjectConfigJson(IDictionary<string, object?> payload)
    {
        return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static Dictionary<string, object?> BuildProjectConfigPayload(
        ProjectConfigSnapshot project,
        GlobalConfigSnapshot global,
        DesktopConfigSnapshot merged)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CompanyName"] = project.CompanyName,
            ["SearchPageSize"] = project.SearchPageSize,
            ["TemplateDocxPath"] = project.TemplateDocxPath,
            ["CostReportBaseImagePath"] = project.CostReportBaseImagePath,
            ["CostReportActorPayRatio"] = project.CostReportActorPayRatio,
            ["CostReportLegalRepresentative"] = project.CostReportLegalRepresentative,
            ["ChatModelId"] = project.ChatModelId,
            ["ChatModelApiKey"] = project.ChatModelApiKey,
            ["ChatModelEndpoint"] = project.ChatModelEndpoint,
            ["AiTextEndpoint"] = merged.AiTextEndpoint,
            ["AiTextApiKey"] = merged.AiTextApiKey,
            ["AiTextModel"] = merged.AiTextModel,
            ["AiTextTimeoutSeconds"] = merged.AiTextTimeoutSeconds,
            ["AiTextMaxBatchSize"] = merged.AiTextMaxBatchSize,
            ["AiTextSystemPrompt"] = merged.AiTextSystemPrompt,
            ["AiTextBatchPrompt"] = merged.AiTextBatchPrompt,
            ["AiTextRetryPrompt"] = merged.AiTextRetryPrompt,
            ["AiTitleSystemPrompt"] = merged.AiTitleSystemPrompt,
            ["AiTitleBatchPrompt"] = merged.AiTitleBatchPrompt,
            ["AiTagSystemPrompt"] = merged.AiTagSystemPrompt,
            ["AiTagBatchPrompt"] = merged.AiTagBatchPrompt,
            ["AiFullInfoSystemPrompt"] = merged.AiFullInfoSystemPrompt,
            ["AiFullInfoBatchPrompt"] = merged.AiFullInfoBatchPrompt,
            ["AiFullInfoRetryPrompt"] = merged.AiFullInfoRetryPrompt,
            ["WeixinHeadless"] = project.WeixinHeadless,
            ["WeixinSlowMoMs"] = project.WeixinSlowMoMs,
            ["WeixinKeepOpenSeconds"] = project.WeixinKeepOpenSeconds,
            ["WeixinLoginTimeoutSeconds"] = project.WeixinLoginTimeoutSeconds,
            ["WeixinSubmitEnabled"] = project.WeixinSubmitEnabled,
            ["WeixinPauseOnError"] = project.WeixinPauseOnError,
            ["WeixinSaveHtml"] = project.WeixinSaveHtml,
            ["WeixinSaveText"] = project.WeixinSaveText,
            ["WeixinMonetizationType"] = project.WeixinMonetizationType,
            ["WeixinDramaType"] = project.WeixinDramaType,
            ["WeixinDramaQualification"] = project.WeixinDramaQualification,
            ["WeixinSubmitterIdentity"] = project.WeixinSubmitterIdentity,
            ["WeixinTrialEpisodes"] = project.WeixinTrialEpisodes,
            ["WeixinFillRecommendation"] = project.WeixinFillRecommendation,
            ["WeixinSubmissionReportDir"] = project.WeixinSubmissionReportDir,
            ["ImageModelId"] = merged.ImageModelId,
            ["ImageModelApiKey"] = merged.ImageModelApiKey,
            ["ImageModelEndpoint"] = merged.ImageModelEndpoint,
            ["ImageEditModelId"] = merged.ImageEditModelId,
            ["ImageEditApiKey"] = merged.ImageEditApiKey,
            ["ImageEditEndpoint"] = merged.ImageEditEndpoint,
            ["ImageEditPath"] = merged.ImageEditPath,
            ["FrameCoverPrompt"] = merged.FrameCoverPrompt,
            ["PosterLayoutDetectPrompt"] = merged.PosterLayoutDetectPrompt,
            ["PosterInpaintPrompt"] = merged.PosterInpaintPrompt,
            ["PosterInpaintSafeRetryPrompt"] = merged.PosterInpaintSafeRetryPrompt,
            ["PosterGenerationPrompt"] = merged.PosterGenerationPrompt,
            ["PosterGenerationSafeRetryPrompt"] = merged.PosterGenerationSafeRetryPrompt,
            ["PosterNameSystemPrompt"] = merged.PosterNameSystemPrompt,
            ["PosterNameUserPrompt"] = merged.PosterNameUserPrompt,
            ["video"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["res"] = NormalizeConfigValue(project.VideoRes),
                ["bitrateBps"] = NormalizeConfigValue(project.VideoBitrateBps),
                ["bitrateMode"] = project.VideoBitrateMode,
                ["audioBitrateBps"] = NormalizeConfigValue(project.VideoAudioBitrateBps),
                ["fps"] = NormalizeConfigValue(project.VideoFps),
                ["concurrentCount"] = NormalizeConfigValue(project.VideoConcurrentCount),
                ["useHardwareEncoder"] = project.VideoUseHardwareEncoder,
                ["nameTemplate"] = project.VideoNameTemplate
            },
            ["uploadTranscode"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["videoEncoder"] = project.VideoEncoder,
                ["preset"] = project.VideoPreset,
                ["targetVideoBitrateMbps"] = NormalizeConfigValue(project.UploadTargetVideoBitrateMbps),
                ["maxVideoBitrateMbps"] = NormalizeConfigValue(project.UploadMaxVideoBitrateMbps),
                ["minVideoBitrateMbps"] = NormalizeConfigValue(project.UploadMinVideoBitrateMbps),
                ["audioBitrateKbps"] = NormalizeConfigValue(project.UploadAudioBitrateKbps),
                ["bitrateFallbackEnabled"] = project.UploadBitrateFallbackEnabled,
                ["bitrateFallbackVideoBitrateMbps"] = NormalizeConfigValue(project.UploadBitrateFallbackVideoBitrateMbps),
                ["profiles"] = BuildUploadProfiles(project.UploadBitrateProfilesJson)
            },
            ["nvenc"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cq"] = NormalizeConfigValue(project.NvencCq),
                ["maxParallel"] = NormalizeConfigValue(project.NvencMaxParallel)
            },
            ["materialTranscode"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["enabled"] = project.MaterialConvertEnabled,
                ["trimHeadSeconds"] = NormalizeConfigValue(project.MaterialTrimHeadSeconds),
                ["trimTailSeconds"] = NormalizeConfigValue(project.MaterialTrimTailSeconds),
                ["speedPercent"] = NormalizeConfigValue(project.MaterialSpeedPercent),
                ["dynamicSpeedEnabled"] = project.MaterialDynamicSpeedEnabled,
                ["dynamicSpeedPresetName"] = project.MaterialDynamicSpeedPresetName,
                ["dynamicSpeedHeadSeconds"] = NormalizeConfigValue(project.MaterialDynamicSpeedHeadSeconds),
                ["dynamicSpeedHeadPercent"] = NormalizeConfigValue(project.MaterialDynamicSpeedHeadPercent),
                ["dynamicSpeedMiddlePercent"] = NormalizeConfigValue(project.MaterialDynamicSpeedMiddlePercent),
                ["dynamicSpeedTailSeconds"] = NormalizeConfigValue(project.MaterialDynamicSpeedTailSeconds),
                ["dynamicSpeedTailPercent"] = NormalizeConfigValue(project.MaterialDynamicSpeedTailPercent),
                ["frameSamplingEnabled"] = project.MaterialFrameSamplingEnabled,
                ["frameSamplingMode"] = project.MaterialFrameSamplingMode,
                ["frameSamplingInterval"] = NormalizeConfigValue(project.MaterialFrameSamplingInterval),
                ["dropEveryNFrames"] = NormalizeConfigValue(project.MaterialDropEveryNFrames),
                ["dropCount"] = NormalizeConfigValue(project.MaterialDropCount),
                ["cropWidthPercent"] = NormalizeConfigValue(project.MaterialCropWidthPercent),
                ["cropHeightPercent"] = NormalizeConfigValue(project.MaterialCropHeightPercent),
                ["foregroundZoomPercent"] = NormalizeConfigValue(project.MaterialForegroundZoomPercent),
                ["dedupEnabled"] = project.MaterialDedupEnabled,
                ["dedupColorEnabled"] = project.MaterialDedupColorEnabled,
                ["dedupNoiseEnabled"] = project.MaterialDedupNoiseEnabled,
                ["dedupAudioEnabled"] = project.MaterialDedupAudioEnabled,
                ["dedupMetadataEnabled"] = project.MaterialDedupMetadataEnabled,
                ["dedupRotateEnabled"] = project.MaterialDedupRotateEnabled,
                ["dedupVignetteEnabled"] = project.MaterialDedupVignetteEnabled,
                ["dedupFadeInEnabled"] = project.MaterialDedupFadeInEnabled,
                ["watermarkEnabled"] = project.MaterialWatermarkEnabled,
                ["watermarkText"] = project.MaterialWatermarkText,
                ["watermarkFontSize"] = NormalizeConfigValue(project.MaterialWatermarkFontSize),
                ["watermarkPosition"] = project.MaterialWatermarkPosition,
                ["watermarkMarginX"] = NormalizeConfigValue(project.MaterialWatermarkMarginX),
                ["watermarkMarginY"] = NormalizeConfigValue(project.MaterialWatermarkMarginY),
                ["outputWidth"] = NormalizeConfigValue(project.MaterialOutputWidth),
                ["outputHeight"] = NormalizeConfigValue(project.MaterialOutputHeight),
                ["pipWidthPercent"] = NormalizeConfigValue(project.MaterialPipWidthPercent),
                ["pipHeightPercent"] = NormalizeConfigValue(project.MaterialPipHeightPercent)
            },
            ["ProjectImageGenerationMode"] = string.IsNullOrWhiteSpace(project.ProjectImageGenerationMode) ? "image_template" : project.ProjectImageGenerationMode,
            ["ProjectImageTemplateRoot"] = project.ProjectImageTemplateRoot,
            ["ProjectImageTemplateId"] = project.ProjectImageTemplateId,
            ["ProjectImageTemplateDir"] = project.ProjectImageTemplateDir,
            ["ProjectImageCount"] = project.ProjectImageCount,
            ["GlobalSettingsFilePath"] = global.SettingsFilePath
        };
    }

    private static object? NormalizeConfigValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (bool.TryParse(trimmed, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return trimmed;
    }

    private static IReadOnlyList<object> BuildUploadProfiles(string? profilesJson)
    {
        return UploadTranscodeBitrateProfiles.Parse(profilesJson)
            .Select(item => new
            {
                name = item.Name,
                min_short_edge = item.MinShortEdge,
                max_short_edge = item.MaxShortEdge,
                bitrate_mbps = Math.Round(item.BitrateMbps, 3),
                audio_kbps = item.AudioKbps,
                video_encoder = item.VideoEncoder,
                preset = item.Preset,
                enabled = item.Enabled
            })
            .Cast<object>()
            .ToArray();
    }

    public static string ResolveConfiguredPath(string rootDirOrConfigDir, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(rootDirOrConfigDir, configuredPath));
    }

    private static ProjectConfigSnapshot BuildProjectSnapshot(
        string configFilePath,
        string configDir,
        IReadOnlyDictionary<string, string> map)
    {
        return new ProjectConfigSnapshot(
            ConfigFilePath: configFilePath,
            CompanyName: Get(map, "CompanyName"),
            SearchPageSize: Get(map, "SearchPageSize"),
            TemplateDocxPath: ResolveConfiguredPath(configDir, Get(map, "TemplateDocxPath", "CostReportTemplatePath")),
            CostReportBaseImagePath: ResolveConfiguredPath(configDir, Get(map, "CostReportBaseImagePath", "CostReportBackgroundImagePath", "CostReportTemplateImagePath")),
            CostReportActorPayRatio: Get(map, "CostReportActorPayRatio", "ActorPayRatio", "ActorPayRatioText"),
            CostReportLegalRepresentative: Get(map, "CostReportLegalRepresentative", "LegalRepresentative", "LegalRepresentativeOrEditor"),
            WeixinHeadless: bool.TryParse(Get(map, "WeixinHeadless"), out var weixinHeadless) && weixinHeadless,
            WeixinSlowMoMs: Get(map, "WeixinSlowMoMs"),
            WeixinKeepOpenSeconds: Get(map, "WeixinKeepOpenSeconds"),
            WeixinLoginTimeoutSeconds: Get(map, "WeixinLoginTimeoutSeconds"),
            WeixinSubmitEnabled: bool.TryParse(Get(map, "WeixinSubmitEnabled"), out var weixinSubmitEnabled) && weixinSubmitEnabled,
            WeixinPauseOnError: !bool.TryParse(Get(map, "WeixinPauseOnError"), out var weixinPauseOnError) || weixinPauseOnError,
            WeixinSaveHtml: !bool.TryParse(Get(map, "WeixinSaveHtml"), out var weixinSaveHtml) || weixinSaveHtml,
            WeixinSaveText: !bool.TryParse(Get(map, "WeixinSaveText"), out var weixinSaveText) || weixinSaveText,
            WeixinMonetizationType: Get(map, "WeixinMonetizationType"),
            WeixinDramaType: Get(map, "WeixinDramaType"),
            WeixinDramaQualification: Get(map, "WeixinDramaQualification"),
            WeixinSubmitterIdentity: Get(map, "WeixinSubmitterIdentity"),
            WeixinTrialEpisodes: Get(map, "WeixinTrialEpisodes"),
            WeixinFillRecommendation: !bool.TryParse(Get(map, "WeixinFillRecommendation"), out var weixinFillRecommendation) || weixinFillRecommendation,
            WeixinSubmissionReportDir: ResolveConfiguredPath(configDir, Get(map, "WeixinSubmissionReportDir")),
            ProjectImageGenerationMode: Get(map, "ProjectImageGenerationMode"),
            ProjectImageTemplateRoot: ResolveConfiguredPath(configDir, Get(map, "ProjectImageTemplateRoot")),
            ProjectImageTemplateId: Get(map, "ProjectImageTemplateId"),
            ProjectImageTemplateDir: ResolveConfiguredPath(configDir, Get(map, "ProjectImageTemplateDir")),
            ProjectImageCount: Get(map, "ProjectImageCount"),
            ChatModelId: Get(map, "ChatModelId"),
            ChatModelApiKey: Get(map, "ChatModelApiKey"),
            ChatModelEndpoint: Get(map, "ChatModelEndpoint"),
            VideoRes: Get(map, "VideoRes"),
            VideoBitrateBps: Get(map, "VideoBitrateBps"),
            VideoBitrateMode: Get(map, "VideoBitrateMode"),
            VideoAudioBitrateBps: Get(map, "VideoAudioBitrateBps"),
            VideoFps: Get(map, "VideoFps"),
            VideoConcurrentCount: Get(map, "VideoConcurrentCount"),
            VideoUseHardwareEncoder: bool.TryParse(Get(map, "VideoUseHardwareEncoder"), out var useHw) ? useHw : true,
            VideoEncoder: Get(map, "VideoEncoder"),
            VideoPreset: Get(map, "VideoPreset"),
            NvencCq: Get(map, "NvencCq"),
            NvencMaxParallel: Get(map, "NvencMaxParallel", "VideoConcurrentCount"),
            VerboseTranscodeLogEnabled: bool.TryParse(Get(map, "VerboseTranscodeLogEnabled"), out var verboseTranscodeLogEnabled) && verboseTranscodeLogEnabled,
            SkipBitrateDownscaleForHighBitrate: bool.TryParse(Get(map, "SkipBitrateDownscaleForHighBitrate"), out var skipBitrateDownscaleForHighBitrate) && skipBitrateDownscaleForHighBitrate,
            UploadTargetVideoBitrateMbps: Get(map, "UploadTargetVideoBitrateMbps"),
            UploadMaxVideoBitrateMbps: Get(map, "UploadMaxVideoBitrateMbps"),
            UploadMinVideoBitrateMbps: Get(map, "UploadMinVideoBitrateMbps"),
            UploadAudioBitrateKbps: Get(map, "UploadAudioBitrateKbps"),
            UploadBitrateFallbackEnabled: bool.TryParse(Get(map, "UploadBitrateFallbackEnabled"), out var uploadBitrateFallbackEnabled) && uploadBitrateFallbackEnabled,
            UploadBitrateFallbackVideoBitrateMbps: Get(map, "UploadBitrateFallbackVideoBitrateMbps"),
            UploadBitrateProfilesJson: Get(map, "UploadBitrateProfilesJson"),
            VideoNameTemplate: Get(map, "VideoNameTemplate"),
            MaterialConvertEnabled: !bool.TryParse(Get(map, "MaterialConvertEnabled"), out var materialConvertEnabled) || materialConvertEnabled,
            MaterialTrimHeadSeconds: Get(map, "MaterialTrimHeadSeconds"),
            MaterialTrimTailSeconds: Get(map, "MaterialTrimTailSeconds"),
            MaterialSpeedPercent: Get(map, "MaterialSpeedPercent"),
            MaterialDynamicSpeedEnabled: bool.TryParse(Get(map, "MaterialDynamicSpeedEnabled"), out var materialDynamicSpeedEnabled) && materialDynamicSpeedEnabled,
            MaterialDynamicSpeedPresetName: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedPresetName")) ? "light_rhythm" : Get(map, "MaterialDynamicSpeedPresetName"),
            MaterialDynamicSpeedHeadSeconds: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedHeadSeconds")) ? "2.5" : Get(map, "MaterialDynamicSpeedHeadSeconds"),
            MaterialDynamicSpeedHeadPercent: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedHeadPercent")) ? "8" : Get(map, "MaterialDynamicSpeedHeadPercent"),
            MaterialDynamicSpeedMiddlePercent: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedMiddlePercent")) ? "6" : Get(map, "MaterialDynamicSpeedMiddlePercent"),
            MaterialDynamicSpeedTailSeconds: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedTailSeconds")) ? "2.5" : Get(map, "MaterialDynamicSpeedTailSeconds"),
            MaterialDynamicSpeedTailPercent: string.IsNullOrWhiteSpace(Get(map, "MaterialDynamicSpeedTailPercent")) ? "8" : Get(map, "MaterialDynamicSpeedTailPercent"),
            MaterialFrameSamplingEnabled: bool.TryParse(Get(map, "MaterialFrameSamplingEnabled"), out var materialFrameSamplingEnabled)
                ? materialFrameSamplingEnabled
                : !string.Equals(Get(map, "MaterialDropCount"), "0", StringComparison.Ordinal),
            MaterialFrameSamplingMode: string.IsNullOrWhiteSpace(Get(map, "MaterialFrameSamplingMode")) ? "fixed_interval" : Get(map, "MaterialFrameSamplingMode"),
            MaterialFrameSamplingInterval: !string.IsNullOrWhiteSpace(Get(map, "MaterialFrameSamplingInterval"))
                ? Get(map, "MaterialFrameSamplingInterval")
                : !string.IsNullOrWhiteSpace(Get(map, "MaterialDropEveryNFrames"))
                    ? Get(map, "MaterialDropEveryNFrames")
                    : "20",
            MaterialDropEveryNFrames: Get(map, "MaterialDropEveryNFrames"),
            MaterialDropCount: Get(map, "MaterialDropCount"),
            MaterialCropWidthPercent: Get(map, "MaterialCropWidthPercent"),
            MaterialCropHeightPercent: Get(map, "MaterialCropHeightPercent"),
            MaterialForegroundZoomPercent: string.IsNullOrWhiteSpace(Get(map, "MaterialForegroundZoomPercent")) ? "0" : Get(map, "MaterialForegroundZoomPercent"),
            MaterialDedupEnabled: bool.TryParse(Get(map, "MaterialDedupEnabled"), out var materialDedupEnabled) && materialDedupEnabled,
            MaterialDedupColorEnabled: bool.TryParse(Get(map, "MaterialDedupColorEnabled"), out var materialDedupColorEnabled) && materialDedupColorEnabled,
            MaterialDedupNoiseEnabled: bool.TryParse(Get(map, "MaterialDedupNoiseEnabled"), out var materialDedupNoiseEnabled) && materialDedupNoiseEnabled,
            MaterialDedupAudioEnabled: bool.TryParse(Get(map, "MaterialDedupAudioEnabled"), out var materialDedupAudioEnabled) && materialDedupAudioEnabled,
            MaterialDedupMetadataEnabled: bool.TryParse(Get(map, "MaterialDedupMetadataEnabled"), out var materialDedupMetadataEnabled) && materialDedupMetadataEnabled,
            MaterialDedupRotateEnabled: bool.TryParse(Get(map, "MaterialDedupRotateEnabled"), out var materialDedupRotateEnabled) && materialDedupRotateEnabled,
            MaterialDedupVignetteEnabled: bool.TryParse(Get(map, "MaterialDedupVignetteEnabled"), out var materialDedupVignetteEnabled) && materialDedupVignetteEnabled,
            MaterialDedupFadeInEnabled: bool.TryParse(Get(map, "MaterialDedupFadeInEnabled"), out var materialDedupFadeInEnabled) && materialDedupFadeInEnabled,
            MaterialWatermarkEnabled: bool.TryParse(Get(map, "MaterialWatermarkEnabled"), out var materialWatermarkEnabled) && materialWatermarkEnabled,
            MaterialWatermarkText: Get(map, "MaterialWatermarkText"),
            MaterialWatermarkFontSize: string.IsNullOrWhiteSpace(Get(map, "MaterialWatermarkFontSize")) ? "35" : Get(map, "MaterialWatermarkFontSize"),
            MaterialWatermarkPosition: string.IsNullOrWhiteSpace(Get(map, "MaterialWatermarkPosition")) ? "top_right" : Get(map, "MaterialWatermarkPosition"),
            MaterialWatermarkMarginX: string.IsNullOrWhiteSpace(Get(map, "MaterialWatermarkMarginX")) ? "30" : Get(map, "MaterialWatermarkMarginX"),
            MaterialWatermarkMarginY: string.IsNullOrWhiteSpace(Get(map, "MaterialWatermarkMarginY")) ? "30" : Get(map, "MaterialWatermarkMarginY"),
            MaterialOutputWidth: string.IsNullOrWhiteSpace(Get(map, "MaterialOutputWidth")) ? "1080" : Get(map, "MaterialOutputWidth"),
            MaterialOutputHeight: string.IsNullOrWhiteSpace(Get(map, "MaterialOutputHeight")) ? "1920" : Get(map, "MaterialOutputHeight"),
            MaterialPipWidthPercent: string.IsNullOrWhiteSpace(Get(map, "MaterialPipWidthPercent")) ? "100" : Get(map, "MaterialPipWidthPercent"),
            MaterialPipHeightPercent: string.IsNullOrWhiteSpace(Get(map, "MaterialPipHeightPercent")) ? "100" : Get(map, "MaterialPipHeightPercent"));
    }

    private static DesktopConfigSnapshot BuildMergedSnapshot(
        ProjectConfigSnapshot project,
        GlobalConfigSnapshot global,
        string configDir,
        IReadOnlyDictionary<string, string>? legacyMap)
    {
        string GlobalValue(string preferred, params string[] legacyKeys)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return legacyMap is null ? string.Empty : Get(legacyMap, legacyKeys);
        }

        return new DesktopConfigSnapshot(
            ConfigFilePath: project.ConfigFilePath,
            CompanyName: project.CompanyName,
            SearchPageSize: project.SearchPageSize,
            TemplateDocxPath: project.TemplateDocxPath,
            CostReportBaseImagePath: project.CostReportBaseImagePath,
            CostReportActorPayRatio: project.CostReportActorPayRatio,
            CostReportLegalRepresentative: project.CostReportLegalRepresentative,
            ChatModelId: project.ChatModelId,
            ChatModelApiKey: project.ChatModelApiKey,
            ChatModelEndpoint: project.ChatModelEndpoint,
            AiTextEndpoint: GlobalValue(global.AiTextEndpoint, "AiTextEndpoint"),
            AiTextApiKey: GlobalValue(global.AiTextApiKey, "AiTextApiKey"),
            AiTextModel: GlobalValue(global.AiTextModel, "AiTextModel"),
            AiTextTimeoutSeconds: GlobalValue(global.AiTextTimeoutSeconds, "AiTextTimeoutSeconds"),
            AiTextMaxBatchSize: GlobalValue(global.AiTextMaxBatchSize, "AiTextMaxBatchSize"),
            AiTextSystemPrompt: DecodeMultiline(GlobalValue(global.AiTextSystemPrompt, "AiTextSystemPrompt")),
            AiTextBatchPrompt: DecodeMultiline(GlobalValue(global.AiTextBatchPrompt, "AiTextBatchPrompt")),
            AiTextRetryPrompt: DecodeMultiline(GlobalValue(global.AiTextRetryPrompt, "AiTextRetryPrompt")),
            AiTitleSystemPrompt: DecodeMultiline(GlobalValue(global.AiTitleSystemPrompt, "AiTitleSystemPrompt")),
            AiTitleBatchPrompt: DecodeMultiline(GlobalValue(global.AiTitleBatchPrompt, "AiTitleBatchPrompt")),
            AiTagSystemPrompt: DecodeMultiline(GlobalValue(global.AiTagSystemPrompt, "AiTagSystemPrompt")),
            AiTagBatchPrompt: DecodeMultiline(GlobalValue(global.AiTagBatchPrompt, "AiTagBatchPrompt")),
            AiFullInfoSystemPrompt: DecodeMultiline(GlobalValue(global.AiFullInfoSystemPrompt, "AiFullInfoSystemPrompt")),
            AiFullInfoBatchPrompt: DecodeMultiline(GlobalValue(global.AiFullInfoBatchPrompt, "AiFullInfoBatchPrompt")),
            AiFullInfoRetryPrompt: DecodeMultiline(GlobalValue(global.AiFullInfoRetryPrompt, "AiFullInfoRetryPrompt")),
            WeixinHeadless: project.WeixinHeadless,
            WeixinSlowMoMs: project.WeixinSlowMoMs,
            WeixinKeepOpenSeconds: project.WeixinKeepOpenSeconds,
            WeixinLoginTimeoutSeconds: project.WeixinLoginTimeoutSeconds,
            WeixinSubmitEnabled: project.WeixinSubmitEnabled,
            WeixinPauseOnError: project.WeixinPauseOnError,
            WeixinSaveHtml: project.WeixinSaveHtml,
            WeixinSaveText: project.WeixinSaveText,
            WeixinMonetizationType: project.WeixinMonetizationType,
            WeixinDramaType: project.WeixinDramaType,
            WeixinDramaQualification: project.WeixinDramaQualification,
            WeixinSubmitterIdentity: project.WeixinSubmitterIdentity,
            WeixinTrialEpisodes: project.WeixinTrialEpisodes,
            WeixinFillRecommendation: project.WeixinFillRecommendation,
            WeixinSubmissionReportDir: project.WeixinSubmissionReportDir,
            ImageModelId: GlobalValue(global.ImageModelId, "ImageModelId"),
            ImageModelApiKey: GlobalValue(global.ImageModelApiKey, "ImageModelApiKey"),
            ImageModelEndpoint: GlobalValue(global.ImageModelEndpoint, "ImageModelEndpoint"),
            ImageEditModelId: GlobalValue(global.ImageEditModelId, "ImageEditModelId"),
            ImageEditApiKey: GlobalValue(global.ImageEditApiKey, "ImageEditApiKey"),
            ImageEditEndpoint: GlobalValue(global.ImageEditEndpoint, "ImageEditEndpoint"),
            ImageEditPath: GlobalValue(global.ImageEditPath, "ImageEditPath"),
            FrameCoverPrompt: DecodeMultiline(GlobalValue(global.FrameCoverPrompt, "FrameCoverPrompt")),
            PosterLayoutDetectPrompt: DecodeMultiline(GlobalValue(global.PosterLayoutDetectPrompt, "PosterLayoutDetectPrompt")),
            PosterInpaintPrompt: DecodeMultiline(GlobalValue(global.PosterInpaintPrompt, "PosterInpaintPrompt")),
            PosterInpaintSafeRetryPrompt: DecodeMultiline(GlobalValue(global.PosterInpaintSafeRetryPrompt, "PosterInpaintSafeRetryPrompt")),
            PosterGenerationPrompt: DecodeMultiline(GlobalValue(global.PosterGenerationPrompt, "PosterGenerationPrompt")),
            PosterGenerationSafeRetryPrompt: DecodeMultiline(GlobalValue(global.PosterGenerationSafeRetryPrompt, "PosterGenerationSafeRetryPrompt")),
            PosterNameSystemPrompt: DecodeMultiline(GlobalValue(global.PosterNameSystemPrompt, "PosterNameSystemPrompt")),
            PosterNameUserPrompt: DecodeMultiline(GlobalValue(global.PosterNameUserPrompt, "PosterNameUserPrompt")),
            VideoRes: project.VideoRes,
            VideoBitrateBps: project.VideoBitrateBps,
            VideoBitrateMode: project.VideoBitrateMode,
            VideoAudioBitrateBps: project.VideoAudioBitrateBps,
            VideoFps: project.VideoFps,
            VideoConcurrentCount: project.VideoConcurrentCount,
            VideoUseHardwareEncoder: project.VideoUseHardwareEncoder,
            VideoNameTemplate: project.VideoNameTemplate,
            MaterialConvertEnabled: project.MaterialConvertEnabled,
            MaterialTrimHeadSeconds: project.MaterialTrimHeadSeconds,
            MaterialTrimTailSeconds: project.MaterialTrimTailSeconds,
            MaterialSpeedPercent: project.MaterialSpeedPercent,
            MaterialDropEveryNFrames: project.MaterialDropEveryNFrames,
            MaterialDropCount: project.MaterialDropCount,
            MaterialCropWidthPercent: project.MaterialCropWidthPercent,
            MaterialCropHeightPercent: project.MaterialCropHeightPercent,
            ProjectImageCount: project.ProjectImageCount,
            ProjectImageTemplateDir: ResolveProjectImageTemplateDir(project, configDir));
    }

    private static string ResolveProjectImageTemplateDir(ProjectConfigSnapshot project, string configDir)
    {
        var projectRoot = Directory.GetParent(configDir)?.FullName;
        return ProjectImageTemplateCatalog.ResolveTemplateDirectory(
            project.ProjectImageTemplateRoot,
            project.ProjectImageTemplateId,
            project.ProjectImageTemplateDir,
            projectRoot);
    }

    private static Dictionary<string, string> ReadConfigMap(string path)
    {
        var content = File.ReadAllText(path);
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            throw new InvalidDataException($"配置文件必须是 JSON 格式: {path}");
        }

        using var document = JsonDocument.Parse(content);
        var jsonMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            AddTopLevelValues(document.RootElement, jsonMap);
            AddStructuredAliases(document.RootElement, jsonMap);
        }

        return jsonMap;
    }

    private static void AddTopLevelValues(JsonElement root, IDictionary<string, string> map)
    {
        foreach (var property in root.EnumerateObject())
        {
            map[property.Name] = ToConfigString(property.Value);
        }
    }

    private static void AddStructuredAliases(JsonElement root, IDictionary<string, string> map)
    {
        CopySectionValue(root, map, "video", "res", "VideoRes");
        CopySectionValue(root, map, "video", "bitrateBps", "VideoBitrateBps");
        CopySectionValue(root, map, "video", "bitrateMode", "VideoBitrateMode");
        CopySectionValue(root, map, "video", "audioBitrateBps", "VideoAudioBitrateBps");
        CopySectionValue(root, map, "video", "fps", "VideoFps");
        CopySectionValue(root, map, "video", "concurrentCount", "VideoConcurrentCount");
        CopySectionValue(root, map, "video", "useHardwareEncoder", "VideoUseHardwareEncoder");
        CopySectionValue(root, map, "video", "nameTemplate", "VideoNameTemplate");

        CopySectionValue(root, map, "materialTranscode", "enabled", "MaterialConvertEnabled");
        CopySectionValue(root, map, "materialTranscode", "trimHeadSeconds", "MaterialTrimHeadSeconds");
        CopySectionValue(root, map, "materialTranscode", "trimTailSeconds", "MaterialTrimTailSeconds");
        CopySectionValue(root, map, "materialTranscode", "speedPercent", "MaterialSpeedPercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedEnabled", "MaterialDynamicSpeedEnabled");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedPresetName", "MaterialDynamicSpeedPresetName");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedHeadSeconds", "MaterialDynamicSpeedHeadSeconds");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedHeadPercent", "MaterialDynamicSpeedHeadPercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedMiddlePercent", "MaterialDynamicSpeedMiddlePercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedTailSeconds", "MaterialDynamicSpeedTailSeconds");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedTailPercent", "MaterialDynamicSpeedTailPercent");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingEnabled", "MaterialFrameSamplingEnabled");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingMode", "MaterialFrameSamplingMode");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingInterval", "MaterialFrameSamplingInterval");
        CopySectionValue(root, map, "materialTranscode", "dropEveryNFrames", "MaterialDropEveryNFrames");
        CopySectionValue(root, map, "materialTranscode", "dropCount", "MaterialDropCount");
        CopySectionValue(root, map, "materialTranscode", "cropWidthPercent", "MaterialCropWidthPercent");
        CopySectionValue(root, map, "materialTranscode", "cropHeightPercent", "MaterialCropHeightPercent");
        CopySectionValue(root, map, "materialTranscode", "foregroundZoomPercent", "MaterialForegroundZoomPercent");
        CopySectionValue(root, map, "materialTranscode", "dedupEnabled", "MaterialDedupEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupColorEnabled", "MaterialDedupColorEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupNoiseEnabled", "MaterialDedupNoiseEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupAudioEnabled", "MaterialDedupAudioEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupMetadataEnabled", "MaterialDedupMetadataEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupRotateEnabled", "MaterialDedupRotateEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupVignetteEnabled", "MaterialDedupVignetteEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupFadeInEnabled", "MaterialDedupFadeInEnabled");
        CopySectionValue(root, map, "materialTranscode", "watermarkEnabled", "MaterialWatermarkEnabled");
        CopySectionValue(root, map, "materialTranscode", "watermarkText", "MaterialWatermarkText");
        CopySectionValue(root, map, "materialTranscode", "watermarkFontSize", "MaterialWatermarkFontSize");
        CopySectionValue(root, map, "materialTranscode", "watermarkPosition", "MaterialWatermarkPosition");
        CopySectionValue(root, map, "materialTranscode", "watermarkMarginX", "MaterialWatermarkMarginX");
        CopySectionValue(root, map, "materialTranscode", "watermarkMarginY", "MaterialWatermarkMarginY");
        CopySectionValue(root, map, "materialTranscode", "outputWidth", "MaterialOutputWidth");
        CopySectionValue(root, map, "materialTranscode", "outputHeight", "MaterialOutputHeight");
        CopySectionValue(root, map, "materialTranscode", "pipWidthPercent", "MaterialPipWidthPercent");
        CopySectionValue(root, map, "materialTranscode", "pipHeightPercent", "MaterialPipHeightPercent");

        CopySectionValue(root, map, "uploadTranscode", "videoEncoder", "VideoEncoder");
        CopySectionValue(root, map, "uploadTranscode", "preset", "VideoPreset");
        CopySectionValue(root, map, "uploadTranscode", "targetVideoBitrateMbps", "UploadTargetVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "maxVideoBitrateMbps", "UploadMaxVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "minVideoBitrateMbps", "UploadMinVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "audioBitrateKbps", "UploadAudioBitrateKbps");
        CopySectionValue(root, map, "uploadTranscode", "bitrateFallbackEnabled", "UploadBitrateFallbackEnabled");
        CopySectionValue(root, map, "uploadTranscode", "bitrateFallbackVideoBitrateMbps", "UploadBitrateFallbackVideoBitrateMbps");
        CopyProfiles(root, map);

        CopySectionValue(root, map, "nvenc", "cq", "NvencCq");
        CopySectionValue(root, map, "nvenc", "maxParallel", "NvencMaxParallel");
    }

    private static void CopySectionValue(
        JsonElement root,
        IDictionary<string, string> map,
        string sectionName,
        string propertyName,
        string targetKey)
    {
        if (root.TryGetProperty(sectionName, out var section) &&
            section.ValueKind == JsonValueKind.Object &&
            section.TryGetProperty(propertyName, out var value))
        {
            map[targetKey] = ToConfigString(value);
        }
    }

    private static void CopyProfiles(JsonElement root, IDictionary<string, string> map)
    {
        if (!root.TryGetProperty("uploadTranscode", out var section) ||
            section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        map["UploadBitrateProfilesJson"] = JsonSerializer.Serialize(new
        {
            profiles = JsonSerializer.Deserialize<object[]>(profiles.GetRawText()) ?? []
        });
    }

    private static string ToConfigString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string Get(IReadOnlyDictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void AppendOptional(ICollection<string> lines, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{key}={value}");
        }
    }

    private static string DecodeMultiline(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\\n", "\n", StringComparison.Ordinal);
    }
}
