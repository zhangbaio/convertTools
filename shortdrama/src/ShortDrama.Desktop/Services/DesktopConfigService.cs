using ShortDrama.Desktop.Models;
using System.Text;

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
        var map = File.Exists(configFilePath)
            ? ReadConfigMap(configFilePath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var project = BuildProjectSnapshot(configFilePath, configDir, map);
        var global = _globalSettingsService.Load();
        return BuildMergedSnapshot(project, global, configDir, map);
    }

    public ProjectConfigSnapshot LoadProject(string rootDir)
    {
        var configFilePath = GetConfigFilePath(rootDir);
        var configDir = GetConfigDirectoryPath(rootDir);
        var map = File.Exists(configFilePath)
            ? ReadConfigMap(configFilePath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return BuildProjectSnapshot(configFilePath, configDir, map);
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
        SaveProject(project);
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
            ImageModelId = config.ImageModelId,
            ImageModelApiKey = config.ImageModelApiKey,
            ImageModelEndpoint = config.ImageModelEndpoint,
            ImageEditModelId = config.ImageEditModelId,
            ImageEditApiKey = config.ImageEditApiKey,
            ImageEditEndpoint = config.ImageEditEndpoint,
            ImageEditPath = config.ImageEditPath,
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

    public void SaveProject(ProjectConfigSnapshot config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.ConfigFilePath)!);

        var lines = new List<string>
        {
            "# 基础设置",
            "# 成本报表固定从当前 config 目录读取 sign.png / seal.png",
            string.Empty,
            "# 基础信息",
            $"CompanyName={config.CompanyName}",
            $"SearchPageSize={config.SearchPageSize}",
            $"TemplateDocxPath={config.TemplateDocxPath}",
            $"CostReportBaseImagePath={config.CostReportBaseImagePath}",
            $"CostReportActorPayRatio={config.CostReportActorPayRatio}",
            $"CostReportLegalRepresentative={config.CostReportLegalRepresentative}",
            string.Empty,
            "# 文本模型（兼容驱动已有流程）",
            $"ChatModelId={config.ChatModelId}",
            $"ChatModelApiKey={config.ChatModelApiKey}",
            $"ChatModelEndpoint={config.ChatModelEndpoint}",
            string.Empty,
            "# 微信剧集上传 - 基础设置",
            $"WeixinHeadless={config.WeixinHeadless.ToString().ToLowerInvariant()}",
            $"WeixinSlowMoMs={config.WeixinSlowMoMs}",
            $"WeixinKeepOpenSeconds={config.WeixinKeepOpenSeconds}",
            $"WeixinLoginTimeoutSeconds={config.WeixinLoginTimeoutSeconds}",
            $"WeixinSubmitEnabled={config.WeixinSubmitEnabled.ToString().ToLowerInvariant()}",
            $"WeixinPauseOnError={config.WeixinPauseOnError.ToString().ToLowerInvariant()}",
            $"WeixinSaveHtml={config.WeixinSaveHtml.ToString().ToLowerInvariant()}",
            $"WeixinSaveText={config.WeixinSaveText.ToString().ToLowerInvariant()}",
            string.Empty,
            "# 微信剧集上传 - 剧目信息配置",
            $"WeixinMonetizationType={config.WeixinMonetizationType}",
            $"WeixinDramaType={config.WeixinDramaType}",
            $"WeixinDramaQualification={config.WeixinDramaQualification}",
            $"WeixinSubmitterIdentity={config.WeixinSubmitterIdentity}",
            $"WeixinTrialEpisodes={config.WeixinTrialEpisodes}",
            $"WeixinFillRecommendation={config.WeixinFillRecommendation.ToString().ToLowerInvariant()}"
        };

        AppendOptional(lines, "WeixinSubmissionReportDir", config.WeixinSubmissionReportDir);

        lines.AddRange(
        [
            string.Empty,
            "# 视频转码",
            $"VideoRes={config.VideoRes}",
            $"VideoBitrateBps={config.VideoBitrateBps}",
            $"VideoBitrateMode={config.VideoBitrateMode}",
            $"VideoAudioBitrateBps={config.VideoAudioBitrateBps}",
            $"VideoFps={config.VideoFps}",
            $"VideoConcurrentCount={config.VideoConcurrentCount}",
            $"VideoUseHardwareEncoder={config.VideoUseHardwareEncoder.ToString().ToLowerInvariant()}",
            $"VideoEncoder={config.VideoEncoder}",
            $"VideoPreset={config.VideoPreset}",
            $"NvencCq={config.NvencCq}",
            $"NvencMaxParallel={config.NvencMaxParallel}",
            $"VerboseTranscodeLogEnabled={config.VerboseTranscodeLogEnabled.ToString().ToLowerInvariant()}",
            $"SkipBitrateDownscaleForHighBitrate={config.SkipBitrateDownscaleForHighBitrate.ToString().ToLowerInvariant()}",
            $"UploadTargetVideoBitrateMbps={config.UploadTargetVideoBitrateMbps}",
            $"UploadMaxVideoBitrateMbps={config.UploadMaxVideoBitrateMbps}",
            $"UploadMinVideoBitrateMbps={config.UploadMinVideoBitrateMbps}",
            $"UploadAudioBitrateKbps={config.UploadAudioBitrateKbps}",
            $"UploadBitrateFallbackEnabled={config.UploadBitrateFallbackEnabled.ToString().ToLowerInvariant()}",
            $"UploadBitrateFallbackVideoBitrateMbps={config.UploadBitrateFallbackVideoBitrateMbps}",
            $"UploadBitrateProfilesJson={config.UploadBitrateProfilesJson}",
            $"VideoNameTemplate={config.VideoNameTemplate}",
            string.Empty,
            "# 素材转换",
            $"MaterialConvertEnabled={config.MaterialConvertEnabled.ToString().ToLowerInvariant()}",
            $"MaterialTrimHeadSeconds={config.MaterialTrimHeadSeconds}",
            $"MaterialTrimTailSeconds={config.MaterialTrimTailSeconds}",
            $"MaterialSpeedPercent={config.MaterialSpeedPercent}",
            $"MaterialDynamicSpeedEnabled={config.MaterialDynamicSpeedEnabled.ToString().ToLowerInvariant()}",
            $"MaterialDynamicSpeedPresetName={config.MaterialDynamicSpeedPresetName}",
            $"MaterialDynamicSpeedHeadSeconds={config.MaterialDynamicSpeedHeadSeconds}",
            $"MaterialDynamicSpeedHeadPercent={config.MaterialDynamicSpeedHeadPercent}",
            $"MaterialDynamicSpeedMiddlePercent={config.MaterialDynamicSpeedMiddlePercent}",
            $"MaterialDynamicSpeedTailSeconds={config.MaterialDynamicSpeedTailSeconds}",
            $"MaterialDynamicSpeedTailPercent={config.MaterialDynamicSpeedTailPercent}",
            $"MaterialFrameSamplingEnabled={config.MaterialFrameSamplingEnabled.ToString().ToLowerInvariant()}",
            $"MaterialFrameSamplingMode={config.MaterialFrameSamplingMode}",
            $"MaterialFrameSamplingInterval={config.MaterialFrameSamplingInterval}",
            $"MaterialDropEveryNFrames={config.MaterialDropEveryNFrames}",
            $"MaterialDropCount={config.MaterialDropCount}",
            $"MaterialCropWidthPercent={config.MaterialCropWidthPercent}",
            $"MaterialCropHeightPercent={config.MaterialCropHeightPercent}",
            $"MaterialForegroundZoomPercent={config.MaterialForegroundZoomPercent}",
            $"MaterialWatermarkEnabled={config.MaterialWatermarkEnabled.ToString().ToLowerInvariant()}",
            $"MaterialWatermarkText={config.MaterialWatermarkText}",
            $"MaterialWatermarkFontSize={config.MaterialWatermarkFontSize}",
            $"MaterialWatermarkPosition={config.MaterialWatermarkPosition}",
            $"MaterialWatermarkMarginX={config.MaterialWatermarkMarginX}",
            $"MaterialWatermarkMarginY={config.MaterialWatermarkMarginY}",
            $"MaterialOutputWidth={config.MaterialOutputWidth}",
            $"MaterialOutputHeight={config.MaterialOutputHeight}",
            $"MaterialPipWidthPercent={config.MaterialPipWidthPercent}",
            $"MaterialPipHeightPercent={config.MaterialPipHeightPercent}",
            string.Empty,
            "# 工程图",
            $"ProjectImageGenerationMode={(string.IsNullOrWhiteSpace(config.ProjectImageGenerationMode) ? "image_template" : config.ProjectImageGenerationMode)}",
            $"ProjectImageTemplateRoot={config.ProjectImageTemplateRoot}",
            $"ProjectImageTemplateId={config.ProjectImageTemplateId}",
            $"ProjectImageTemplateDir={config.ProjectImageTemplateDir}",
            $"ProjectImageCount={config.ProjectImageCount}"
        ]);

        File.WriteAllText(config.ConfigFilePath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Encoding.UTF8);
    }

    public static string GetConfigFilePath(string rootDir)
    {
        return Path.Combine(GetConfigDirectoryPath(rootDir), "config.txt");
    }

    public static string GetConfigDirectoryPath(string rootDir)
    {
        return Path.Combine(rootDir, "config");
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
            MaterialConvertEnabled: bool.TryParse(Get(map, "MaterialConvertEnabled"), out var materialConvertEnabled) && materialConvertEnabled,
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
        if (!string.IsNullOrWhiteSpace(project.ProjectImageTemplateDir))
        {
            return project.ProjectImageTemplateDir;
        }

        if (string.IsNullOrWhiteSpace(project.ProjectImageTemplateRoot))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(project.ProjectImageTemplateId))
        {
            return project.ProjectImageTemplateRoot;
        }

        var candidate = Path.Combine(project.ProjectImageTemplateRoot, project.ProjectImageTemplateId);
        return Directory.Exists(candidate) || File.Exists(Path.Combine(candidate, "template.json"))
            ? candidate
            : project.ProjectImageTemplateRoot;
    }

    private static Dictionary<string, string> ReadConfigMap(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            map[key] = value;
        }

        return map;
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
