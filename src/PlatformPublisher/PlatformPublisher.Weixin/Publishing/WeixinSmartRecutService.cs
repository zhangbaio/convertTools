using System.Text.RegularExpressions;
using ChannelsPublisher.Clip;
using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinSmartRecutResult(string OutputDirectory, IReadOnlyList<string> OutputVideos);

public sealed partial class WeixinSmartRecutService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private readonly IAiRuntimeSettingsProvider _runtimeSettingsProvider;
    private readonly IWorkService _workService;

    public WeixinSmartRecutService(IAiRuntimeSettingsProvider runtimeSettingsProvider, IWorkService workService)
    {
        _runtimeSettingsProvider = runtimeSettingsProvider;
        _workService = workService;
    }

    public async Task<WeixinSmartRecutResult> RunAsync(
        string projectDirectory,
        int outputEpisodeCount,
        int minSeconds,
        int maxSeconds,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var projectDir = Path.GetFullPath(projectDirectory);
        var videos = EnumerateSourceVideos(projectDir);
        if (videos.Count == 0)
            throw new InvalidOperationException("智能重剪未找到源剧集视频。");

        var configPath = await _workService.EnsureWeixinUploadConfigAsync(projectDir, null, cancellationToken);
        var workflowDir = Path.GetDirectoryName(configPath)
                          ?? throw new InvalidOperationException("智能重剪无法定位工作项目目录。");
        var outputDir = Path.Combine(workflowDir, "videos");
        Directory.CreateDirectory(outputDir);
        var expectedCount = Math.Clamp(outputEpisodeCount <= 0 ? videos.Count : outputEpisodeCount, 1, 100);
        var existing = Directory.EnumerateFiles(outputDir, "*.mp4", SearchOption.TopDirectoryOnly).ToArray();
        if (!force && existing.Length >= expectedCount)
        {
            progress?.Report($"智能重剪：复用已有工作视频 {existing.Length} 集。");
            return new WeixinSmartRecutResult(outputDir, existing.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        var settings = _runtimeSettingsProvider.Load();
        var hasLocalAsr = !string.IsNullOrWhiteSpace(settings.AsrLocalModelDirectory) && Directory.Exists(settings.AsrLocalModelDirectory);
        var hasOnlineAsr = !string.IsNullOrWhiteSpace(settings.AsrAppId) && !string.IsNullOrWhiteSpace(settings.AsrAccessToken);
        if (!hasLocalAsr && !hasOnlineAsr)
            throw new InvalidOperationException("智能重剪需要可用的本地 ASR 模型或火山 ASR AppId/Token。");

        var episodes = videos.Select((path, index) => new EpisodeSource(ResolveEpisodeIndex(path, index + 1), path)).ToArray();
        var options = new ClipEngineOptions
        {
            Width = 1080,
            Height = 1920,
            Modes = ["mashup"],
            ClipCount = expectedCount,
            ClipMinSeconds = Math.Clamp(minSeconds, 30, 1800),
            ClipMaxSeconds = Math.Clamp(Math.Max(minSeconds, maxSeconds), 30, 3600),
            RenderSpeed = "balanced",
            AudioEnergy = true,
            EnableLlmScore = !string.IsNullOrWhiteSpace(settings.Endpoint) && !string.IsNullOrWhiteSpace(settings.Model),
            AsrEngine = hasLocalAsr && hasOnlineAsr ? "hybrid" : hasLocalAsr ? "local" : "volcengine",
            AsrLanguage = settings.AsrLanguage,
            LocalModelDir = settings.AsrLocalModelDirectory,
            LocalVadPath = settings.AsrVadPath,
            VolcAppId = settings.AsrAppId,
            VolcAccessToken = settings.AsrAccessToken,
            AiEndpoint = settings.Endpoint,
            AiApiKey = settings.ApiKey,
            AiModel = settings.Model,
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
        };
        progress?.Report($"智能重剪：分析 {episodes.Length} 集，计划输出 {expectedCount} 集。 ");
        var engine = new ClipEngine();
        var result = await engine.GenerateAsync(
            projectDir,
            episodes,
            options,
            message => progress?.Report(message),
            cancellationToken);
        if (!result.Ok)
            throw new InvalidOperationException(result.Error ?? "智能重剪失败。");

        var outputs = new List<string>();
        for (var index = 0; index < result.Outputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(outputDir, $"第{index + 1}集.mp4");
            File.Copy(result.Outputs[index], destination, overwrite: true);
            outputs.Add(destination);
        }
        if (outputs.Count == 0)
            throw new InvalidOperationException("智能重剪没有生成可用视频。");
        progress?.Report($"智能重剪完成：生成 {outputs.Count} 集 → {outputDir}");
        return new WeixinSmartRecutResult(outputDir, outputs);
    }

    private static IReadOnlyList<string> EnumerateSourceVideos(string projectDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(projectDirectory, "videos"),
            projectDirectory,
        };
        foreach (var directory in candidates)
        {
            if (!Directory.Exists(directory)) continue;
            var videos = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => ResolveEpisodeIndex(path, int.MaxValue))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (videos.Length > 0) return videos;
        }
        return [];
    }

    private static int ResolveEpisodeIndex(string path, int fallback)
    {
        var match = EpisodeNumberRegex().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : fallback;
    }

    [GeneratedRegex(@"第\s*0*(\d+)\s*集", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumberRegex();
}
