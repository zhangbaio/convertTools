using ShortDrama.Desktop.Models;
using System.Text;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopConfigService
{
    public DesktopConfigSnapshot Load(string rootDir)
    {
        var configFilePath = GetConfigFilePath(rootDir);
        var configDir = GetConfigDirectoryPath(rootDir);
        var map = File.Exists(configFilePath)
            ? ReadConfigMap(configFilePath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new DesktopConfigSnapshot(
            ConfigFilePath: configFilePath,
            CompanyName: Get(map, "CompanyName"),
            SearchPageSize: Get(map, "SearchPageSize"),
            TemplateDocxPath: ResolveConfiguredPath(configDir, Get(map, "TemplateDocxPath", "CostReportTemplatePath")),
            CostReportBaseImagePath: ResolveConfiguredPath(configDir, Get(map, "CostReportBaseImagePath", "CostReportBackgroundImagePath", "CostReportTemplateImagePath")),
            CostReportActorPayRatio: Get(map, "CostReportActorPayRatio", "ActorPayRatio", "ActorPayRatioText"),
            CostReportLegalRepresentative: Get(map, "CostReportLegalRepresentative", "LegalRepresentative", "LegalRepresentativeOrEditor"),
            ChatModelId: Get(map, "ChatModelId"),
            ChatModelApiKey: Get(map, "ChatModelApiKey"),
            ChatModelEndpoint: Get(map, "ChatModelEndpoint"),
            AiTextEndpoint: Get(map, "AiTextEndpoint"),
            AiTextApiKey: Get(map, "AiTextApiKey"),
            AiTextModel: Get(map, "AiTextModel"),
            AiTextTimeoutSeconds: Get(map, "AiTextTimeoutSeconds"),
            AiTextMaxBatchSize: Get(map, "AiTextMaxBatchSize"),
            AiTextSystemPrompt: DecodeMultiline(Get(map, "AiTextSystemPrompt")),
            AiTextBatchPrompt: DecodeMultiline(Get(map, "AiTextBatchPrompt")),
            AiTextRetryPrompt: DecodeMultiline(Get(map, "AiTextRetryPrompt")),
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
            ImageModelId: Get(map, "ImageModelId"),
            ImageModelApiKey: Get(map, "ImageModelApiKey"),
            ImageModelEndpoint: Get(map, "ImageModelEndpoint"),
            ImageEditModelId: Get(map, "ImageEditModelId"),
            ImageEditApiKey: Get(map, "ImageEditApiKey"),
            ImageEditEndpoint: Get(map, "ImageEditEndpoint"),
            ImageEditPath: Get(map, "ImageEditPath"),
            PosterLayoutDetectPrompt: DecodeMultiline(Get(map, "PosterLayoutDetectPrompt")),
            PosterInpaintPrompt: DecodeMultiline(Get(map, "PosterInpaintPrompt")),
            PosterInpaintSafeRetryPrompt: DecodeMultiline(Get(map, "PosterInpaintSafeRetryPrompt")),
            PosterGenerationPrompt: DecodeMultiline(Get(map, "PosterGenerationPrompt")),
            PosterGenerationSafeRetryPrompt: DecodeMultiline(Get(map, "PosterGenerationSafeRetryPrompt")),
            PosterNameSystemPrompt: DecodeMultiline(Get(map, "PosterNameSystemPrompt")),
            PosterNameUserPrompt: DecodeMultiline(Get(map, "PosterNameUserPrompt")),
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
            MaterialCropHeightPercent: Get(map, "MaterialCropHeightPercent"),
            ProjectImageCount: Get(map, "ProjectImageCount"),
            ProjectImageTemplateDir: ResolveConfiguredPath(configDir, Get(map, "ProjectImageTemplateDir")));
    }

    public void Save(DesktopConfigSnapshot config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.ConfigFilePath)!);

        var lines = new List<string>
        {
            "# 工作流配置",
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
            "# AI 文本模型",
            $"ChatModelId={config.ChatModelId}",
            $"ChatModelApiKey={config.ChatModelApiKey}",
            $"ChatModelEndpoint={config.ChatModelEndpoint}",
            string.Empty,
            "# AI 文案（短标题/标签）",
            $"AiTextEndpoint={config.AiTextEndpoint}",
            $"AiTextApiKey={config.AiTextApiKey}",
            $"AiTextModel={config.AiTextModel}",
            $"AiTextTimeoutSeconds={config.AiTextTimeoutSeconds}",
            $"AiTextMaxBatchSize={config.AiTextMaxBatchSize}"
        };

        AppendMultilineOptional(lines, "AiTextBatchPrompt", config.AiTextBatchPrompt);
        AppendMultilineOptional(lines, "AiTextRetryPrompt", config.AiTextRetryPrompt);

        lines.AddRange(
        [
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
        ]);

        AppendOptional(lines, "WeixinSubmissionReportDir", config.WeixinSubmissionReportDir);

        lines.AddRange(
        [
            string.Empty,
            "# AI 图片模型",
            $"ImageModelId={config.ImageModelId}",
            $"ImageModelApiKey={config.ImageModelApiKey}",
            $"ImageModelEndpoint={config.ImageModelEndpoint}",
            string.Empty,
            "# 海报图片编辑接口",
            "# 未配置 ImageEditModelId / ImageEditApiKey / ImageEditEndpoint 时，默认回退到上面的 ImageModel* 配置",
            "# Volcengine Ark 默认走 /images/generations"
        ]);

        AppendOptional(lines, "ImageEditPath", config.ImageEditPath);
        AppendOptional(lines, "ImageEditModelId", config.ImageEditModelId);
        AppendOptional(lines, "ImageEditApiKey", config.ImageEditApiKey);
        AppendOptional(lines, "ImageEditEndpoint", config.ImageEditEndpoint);
        AppendMultilineOptional(lines, "PosterLayoutDetectPrompt", config.PosterLayoutDetectPrompt);
        AppendMultilineOptional(lines, "PosterInpaintPrompt", config.PosterInpaintPrompt);
        AppendMultilineOptional(lines, "PosterInpaintSafeRetryPrompt", config.PosterInpaintSafeRetryPrompt);
        AppendMultilineOptional(lines, "PosterGenerationPrompt", config.PosterGenerationPrompt);
        AppendMultilineOptional(lines, "PosterGenerationSafeRetryPrompt", config.PosterGenerationSafeRetryPrompt);
        AppendMultilineOptional(lines, "PosterNameSystemPrompt", config.PosterNameSystemPrompt);
        AppendMultilineOptional(lines, "PosterNameUserPrompt", config.PosterNameUserPrompt);

        lines.AddRange(
        [
            string.Empty,
            "# 视频转码",
            "# VideoRes 表示短边分辨率",
            $"VideoRes={config.VideoRes}",
            $"VideoBitrateBps={config.VideoBitrateBps}",
            $"VideoBitrateMode={config.VideoBitrateMode}",
            $"VideoAudioBitrateBps={config.VideoAudioBitrateBps}",
            $"VideoFps={config.VideoFps}",
            $"VideoConcurrentCount={config.VideoConcurrentCount}",
            $"VideoUseHardwareEncoder={config.VideoUseHardwareEncoder.ToString().ToLowerInvariant()}",
            $"VideoNameTemplate={config.VideoNameTemplate}",
            string.Empty,
            "# 素材转换",
            $"MaterialConvertEnabled={config.MaterialConvertEnabled.ToString().ToLowerInvariant()}",
            $"MaterialTrimHeadSeconds={config.MaterialTrimHeadSeconds}",
            $"MaterialTrimTailSeconds={config.MaterialTrimTailSeconds}",
            $"MaterialSpeedPercent={config.MaterialSpeedPercent}",
            $"MaterialDropEveryNFrames={config.MaterialDropEveryNFrames}",
            $"MaterialDropCount={config.MaterialDropCount}",
            $"MaterialCropWidthPercent={config.MaterialCropWidthPercent}",
            $"MaterialCropHeightPercent={config.MaterialCropHeightPercent}",
            string.Empty,
            "# 工程图",
            "# 工程图模板现在默认必用，目录下需提供完整的 工程图_1.png ~ 工程图_N.png",
            $"ProjectImageCount={config.ProjectImageCount}",
            $"ProjectImageTemplateDir={config.ProjectImageTemplateDir}"
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

    private static void AppendMultilineOptional(ICollection<string> lines, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        lines.Add($"{key}={normalized.Replace("\n", "\\n", StringComparison.Ordinal)}");
    }

    private static string DecodeMultiline(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\\n", "\n", StringComparison.Ordinal);
    }
}
