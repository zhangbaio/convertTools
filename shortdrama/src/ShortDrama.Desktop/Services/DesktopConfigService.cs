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
            VideoNameTemplate: config.VideoNameTemplate,
            MaterialConvertEnabled: config.MaterialConvertEnabled,
            MaterialTrimHeadSeconds: config.MaterialTrimHeadSeconds,
            MaterialTrimTailSeconds: config.MaterialTrimTailSeconds,
            MaterialSpeedPercent: config.MaterialSpeedPercent,
            MaterialDropEveryNFrames: config.MaterialDropEveryNFrames,
            MaterialDropCount: config.MaterialDropCount,
            MaterialCropWidthPercent: config.MaterialCropWidthPercent,
            MaterialCropHeightPercent: config.MaterialCropHeightPercent);

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
            "# 閸╄櫣顢呮穱鈩冧紖",
            $"CompanyName={config.CompanyName}",
            $"SearchPageSize={config.SearchPageSize}",
            $"TemplateDocxPath={config.TemplateDocxPath}",
            $"CostReportBaseImagePath={config.CostReportBaseImagePath}",
            $"CostReportActorPayRatio={config.CostReportActorPayRatio}",
            $"CostReportLegalRepresentative={config.CostReportLegalRepresentative}",
            string.Empty,
            "# 閺傚洦婀板Ο鈥崇€烽敍鍫濆悑鐎瑰綊鈹嶉崝銊ュ嚒閺堝绁︾粙瀣剁礆",
            $"ChatModelId={config.ChatModelId}",
            $"ChatModelApiKey={config.ChatModelApiKey}",
            $"ChatModelEndpoint={config.ChatModelEndpoint}",
            string.Empty,
            "# 瀵邦喕淇婇崜褔娉︽稉濠佺炊 - 閸╄櫣顢呯拋鍓х枂",
            $"WeixinHeadless={config.WeixinHeadless.ToString().ToLowerInvariant()}",
            $"WeixinSlowMoMs={config.WeixinSlowMoMs}",
            $"WeixinKeepOpenSeconds={config.WeixinKeepOpenSeconds}",
            $"WeixinLoginTimeoutSeconds={config.WeixinLoginTimeoutSeconds}",
            $"WeixinSubmitEnabled={config.WeixinSubmitEnabled.ToString().ToLowerInvariant()}",
            $"WeixinPauseOnError={config.WeixinPauseOnError.ToString().ToLowerInvariant()}",
            $"WeixinSaveHtml={config.WeixinSaveHtml.ToString().ToLowerInvariant()}",
            $"WeixinSaveText={config.WeixinSaveText.ToString().ToLowerInvariant()}",
            string.Empty,
            "# 瀵邦喕淇婇崜褔娉︽稉濠佺炊 - 閸撗呮窗娣団剝浼呴柊宥囩枂",
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
            "# 鐟欏棝顣舵潪顒傜垳",
            $"VideoRes={config.VideoRes}",
            $"VideoBitrateBps={config.VideoBitrateBps}",
            $"VideoBitrateMode={config.VideoBitrateMode}",
            $"VideoAudioBitrateBps={config.VideoAudioBitrateBps}",
            $"VideoFps={config.VideoFps}",
            $"VideoConcurrentCount={config.VideoConcurrentCount}",
            $"VideoUseHardwareEncoder={config.VideoUseHardwareEncoder.ToString().ToLowerInvariant()}",
            $"VideoNameTemplate={config.VideoNameTemplate}",
            string.Empty,
            "# 缁辩姵娼楁潪顒佸床",
            $"MaterialConvertEnabled={config.MaterialConvertEnabled.ToString().ToLowerInvariant()}",
            $"MaterialTrimHeadSeconds={config.MaterialTrimHeadSeconds}",
            $"MaterialTrimTailSeconds={config.MaterialTrimTailSeconds}",
            $"MaterialSpeedPercent={config.MaterialSpeedPercent}",
            $"MaterialDropEveryNFrames={config.MaterialDropEveryNFrames}",
            $"MaterialDropCount={config.MaterialDropCount}",
            $"MaterialCropWidthPercent={config.MaterialCropWidthPercent}",
            $"MaterialCropHeightPercent={config.MaterialCropHeightPercent}",
            string.Empty,
            "# 瀹搞儳鈻奸崶?",
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
            VideoNameTemplate: Get(map, "VideoNameTemplate"),
            MaterialConvertEnabled: bool.TryParse(Get(map, "MaterialConvertEnabled"), out var materialConvertEnabled) && materialConvertEnabled,
            MaterialTrimHeadSeconds: Get(map, "MaterialTrimHeadSeconds"),
            MaterialTrimTailSeconds: Get(map, "MaterialTrimTailSeconds"),
            MaterialSpeedPercent: Get(map, "MaterialSpeedPercent"),
            MaterialDropEveryNFrames: Get(map, "MaterialDropEveryNFrames"),
            MaterialDropCount: Get(map, "MaterialDropCount"),
            MaterialCropWidthPercent: Get(map, "MaterialCropWidthPercent"),
            MaterialCropHeightPercent: Get(map, "MaterialCropHeightPercent"));
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
