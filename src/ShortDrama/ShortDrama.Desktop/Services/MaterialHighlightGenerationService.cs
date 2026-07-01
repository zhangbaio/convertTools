using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Desktop.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public sealed class MaterialHighlightGenerationService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly Regex EpisodeIndexRegex = new(
        @"(?:第\s*0*(\d+)\s*集|episode\s*0*(\d+)|ep\s*0*(\d+)|^0*(\d+)$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly IExternalProcessRunner _processRunner;
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly ILogger<MaterialHighlightGenerationService> _logger;

    public MaterialHighlightGenerationService(
        IWeixinAutomationConfigLoader configLoader,
        IExternalProcessRunner processRunner,
        GlobalSettingsService globalSettingsService,
        ILogger<MaterialHighlightGenerationService> logger)
    {
        _configLoader = configLoader;
        _processRunner = processRunner;
        _globalSettingsService = globalSettingsService;
        _logger = logger;
    }

    public async Task<MaterialHighlightProjectResult> GenerateAsync(
        MaterialHighlightProjectRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workflowProjectDir = ResolveWorkflowProjectDir(request);
        var configPath = ResolvePublishConfigPath(request, workflowProjectDir);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            progress?.Report("素材高光：未找到素材发布配置，跳过。");
            return new MaterialHighlightProjectResult(false, 0, 0, 0, "missing-config");
        }

        var config = await _configLoader.LoadAsync(configPath, request.SourceProjectDir, cancellationToken);
        if (!config.VideoPublish.Enabled)
        {
            progress?.Report("素材高光：当前项目已禁用素材发布，跳过。");
            return new MaterialHighlightProjectResult(false, 0, 0, 0, "publish-disabled");
        }

        if (!string.Equals(NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode), "material_clips", StringComparison.Ordinal))
        {
            progress?.Report("素材高光：当前项目未启用 material_clips，跳过。");
            return new MaterialHighlightProjectResult(false, 0, 0, 0, "source-mode-project");
        }

        var sourceVideos = ResolveSourceVideoFiles(request, workflowProjectDir);
        var outputDir = Path.Combine(workflowProjectDir, "material-clip-output");
        Directory.CreateDirectory(outputDir);

        if (sourceVideos.Count == 0)
        {
            var existingCount = CountExistingClipFiles(outputDir);
            if (existingCount > 0)
            {
                progress?.Report($"素材高光：未找到源视频，沿用现有 {existingCount} 条高光视频。");
                return new MaterialHighlightProjectResult(true, 0, existingCount, existingCount, "reuse-existing");
            }

            throw new InvalidOperationException($"生成素材高光失败：{request.DisplayName} 未找到可切片的视频目录。");
        }

        var settings = _globalSettingsService.Load();
        var ffmpeg = ResolveBinary("ffmpeg");
        var ffprobe = ResolveBinary("ffprobe");

        var generatedCount = 0;
        var existingCountForProject = 0;
        var totalOutputCount = 0;

        progress?.Report($"素材高光：开始处理 {sourceVideos.Count} 个源视频。");

        for (var sourceIndex = 0; sourceIndex < sourceVideos.Count; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = sourceVideos[sourceIndex];
            var sourceDuration = await ProbeDurationSecondsAsync(ffprobe, sourcePath, cancellationToken);
            var targetDuration = ResolveTargetDurationSeconds(sourceDuration, settings);
            var segmentCount = ResolveSegmentCount(sourceDuration, targetDuration, settings);
            var segments = BuildSegments(sourceDuration, targetDuration, segmentCount);
            var episodeIndex = TryExtractEpisodeIndex(sourcePath) ?? sourceIndex + 1;

            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputPath = BuildOutputPath(outputDir, episodeIndex, segmentIndex + 1, segments.Count);
                totalOutputCount++;

                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    existingCountForProject++;
                    progress?.Report($"素材高光：跳过已存在文件 {Path.GetFileName(outputPath)}");
                    continue;
                }

                progress?.Report(
                    $"素材高光：导出 {sourceIndex + 1}/{sourceVideos.Count} -> {Path.GetFileName(outputPath)}");
                await ExportClipAsync(
                    ffmpeg,
                    sourcePath,
                    outputPath,
                    segments[segmentIndex].StartSeconds,
                    segments[segmentIndex].DurationSeconds,
                    cancellationToken);
                generatedCount++;
            }
        }

        progress?.Report(
            $"素材高光：处理完成，新增 {generatedCount} 条，复用 {existingCountForProject} 条。");
        return new MaterialHighlightProjectResult(
            true,
            generatedCount,
            existingCountForProject,
            totalOutputCount,
            "ok");
    }

    private static string ResolveWorkflowProjectDir(MaterialHighlightProjectRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkflowProjectDir))
        {
            return Path.GetFullPath(request.WorkflowProjectDir);
        }

        if (!string.IsNullOrWhiteSpace(request.PublishConfigPath))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(request.PublishConfigPath));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        throw new InvalidOperationException($"生成素材高光失败：{request.DisplayName} 缺少 workflow 项目目录。");
    }

    private static string? ResolvePublishConfigPath(MaterialHighlightProjectRequest request, string workflowProjectDir)
    {
        if (!string.IsNullOrWhiteSpace(request.PublishConfigPath) && File.Exists(request.PublishConfigPath))
        {
            return Path.GetFullPath(request.PublishConfigPath);
        }

        foreach (var name in new[] { "weixin-channel-publish-test.json", "weixin-channel-publish.json", "weixin-channel-material.json" })
        {
            var candidate = Path.Combine(workflowProjectDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveSourceVideoFiles(
        MaterialHighlightProjectRequest request,
        string workflowProjectDir)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Path.Combine(workflowProjectDir, "videos"));
        AddCandidate(candidates, Path.Combine(request.SourceProjectDir, "videos"));
        AddCandidate(candidates, request.SourceProjectDir);

        var manualSourceDir = ResolveManualSourceVideoDirectory(workflowProjectDir, request.SourceProjectDir);
        if (!string.IsNullOrWhiteSpace(manualSourceDir))
        {
            AddCandidate(candidates, manualSourceDir);
        }

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var videos = Directory.EnumerateFiles(candidate, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (videos.Length > 0)
            {
                return videos;
            }
        }

        return [];
    }

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(fullPath);
        }
    }

    private static string? ResolveManualSourceVideoDirectory(string workflowProjectDir, string sourceProjectDir)
    {
        foreach (var metadataPath in new[]
                 {
                     Path.Combine(workflowProjectDir, "shortdrama-project.json"),
                     Path.Combine(sourceProjectDir, "shortdrama-project.json")
                 })
        {
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                if (!document.RootElement.TryGetProperty("manualSourceVideoDir", out var property) ||
                    property.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var raw = property.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var path = Path.IsPathRooted(raw)
                    ? raw
                    : Path.Combine(Path.GetDirectoryName(metadataPath) ?? workflowProjectDir, raw);
                if (Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch
            {
                // Ignore malformed metadata and keep probing other candidates.
            }
        }

        return null;
    }

    private static int CountExistingClipFiles(string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(outputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeVideoSourceMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "material_clips" or "material_clip" or "material_highlights" or "highlight_clips" or "clip_highlights" => "material_clips",
            _ => "project"
        };
    }

    private static int ResolveTargetDurationSeconds(double sourceDuration, GlobalConfigSnapshot settings)
    {
        var mode = string.IsNullOrWhiteSpace(settings.MaterialClipTargetDurationMode)
            ? "adaptive_range"
            : settings.MaterialClipTargetDurationMode.Trim().ToLowerInvariant();
        var fixedSeconds = ParseInt(settings.MaterialClipTargetDurationSec, 30);
        var ratioPercent = ParseDouble(settings.MaterialClipTargetDurationRatioPercent, 8.0d);
        var minSeconds = ParseInt(settings.MaterialClipMinOutputDurationSec, 0);
        var maxSeconds = Math.Max(Math.Max(1, minSeconds), ParseInt(settings.MaterialClipMaxOutputDurationSec, 45));

        if (mode == "fixed")
        {
            return Math.Max(1, fixedSeconds);
        }

        var targetByRatio = Math.Max(
            1,
            (int)Math.Round(sourceDuration * Math.Max(0.1d, ratioPercent) / 100d, MidpointRounding.AwayFromZero));
        if (mode == "ratio")
        {
            return targetByRatio;
        }

        if (minSeconds > 0)
        {
            targetByRatio = Math.Max(minSeconds, targetByRatio);
        }

        return Math.Min(maxSeconds, targetByRatio);
    }

    private static int ResolveSegmentCount(double sourceDuration, int targetDuration, GlobalConfigSnapshot settings)
    {
        var requested = Math.Max(
            1,
            Math.Min(
                ParseInt(settings.MaterialClipPerEpisodeTopN, 2),
                ParseInt(settings.MaterialClipSplitClipLimit, 4)));
        if (requested <= 1 || sourceDuration <= targetDuration + 2d)
        {
            return 1;
        }

        return requested;
    }

    private static IReadOnlyList<MaterialHighlightSegment> BuildSegments(
        double sourceDuration,
        int targetDuration,
        int segmentCount)
    {
        var safeDuration = Math.Max(1d, sourceDuration);
        var clipDuration = Math.Max(1d, Math.Min(targetDuration, safeDuration));
        if (segmentCount <= 1 || safeDuration <= clipDuration + 0.001d)
        {
            var centeredStart = Math.Max(0d, (safeDuration - clipDuration) / 2d);
            return [new MaterialHighlightSegment(centeredStart, clipDuration)];
        }

        var maxStart = Math.Max(0d, safeDuration - clipDuration);
        var step = maxStart / (segmentCount - 1);
        var segments = new List<MaterialHighlightSegment>(segmentCount);
        for (var index = 0; index < segmentCount; index++)
        {
            segments.Add(new MaterialHighlightSegment(step * index, clipDuration));
        }

        return segments;
    }

    private static int? TryExtractEpisodeIndex(string path)
    {
        var match = EpisodeIndexRegex.Match(Path.GetFileNameWithoutExtension(path));
        if (!match.Success)
        {
            return null;
        }

        foreach (var group in match.Groups.Cast<Group>().Skip(1))
        {
            if (group.Success && int.TryParse(group.Value, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string BuildOutputPath(string outputDir, int episodeIndex, int segmentIndex, int segmentCount)
    {
        var fileName = segmentCount <= 1
            ? $"高光-第{episodeIndex:D3}集.mp4"
            : $"高光-第{episodeIndex:D3}集-{segmentIndex:D2}.mp4";
        return Path.Combine(outputDir, fileName);
    }

    private async Task<double> ProbeDurationSecondsAsync(
        string ffprobe,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path],
            Path.GetDirectoryName(path),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"素材高光分析失败：{Path.GetFileName(path)}，{result.StandardError.Trim()}");
        }

        if (!double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            duration <= 0)
        {
            throw new InvalidOperationException($"素材高光分析失败：无法识别视频时长 -> {Path.GetFileName(path)}");
        }

        return duration;
    }

    private async Task ExportClipAsync(
        string ffmpeg,
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var result = await _processRunner.RunAsync(
            ffmpeg,
            BuildArguments(inputPath, outputPath, startSeconds, durationSeconds),
            Path.GetDirectoryName(outputPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogError(
                "Failed to export material highlight clip: {Input} -> {Output}; stderr={Stderr}",
                inputPath,
                outputPath,
                result.StandardError);
            throw new InvalidOperationException(
                $"素材高光导出失败：{Path.GetFileName(outputPath)}，{TrimProcessError(result.StandardError)}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException($"素材高光导出失败：未生成输出文件 -> {Path.GetFileName(outputPath)}");
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds)
    {
        var filter = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,format=yuv420p";
        return
        [
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-vf", filter,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "21",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "128k",
            "-ac", "2",
            "-movflags", "+faststart",
            outputPath
        ];
    }

    private static string ResolveBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var extensions = OperatingSystem.IsWindows()
                ? new[] { ".exe", ".cmd", ".bat", string.Empty }
                : new[] { string.Empty };

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(dir, name + ext);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
        }

        throw new InvalidOperationException($"未找到 {name}。请先安装 ffmpeg，并确保 {name} 在 PATH 中。");
    }

    private static string TrimProcessError(string stderr)
    {
        var message = (stderr ?? string.Empty).Trim();
        if (message.Length <= 180)
        {
            return message;
        }

        return message[..180] + "...";
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private sealed record MaterialHighlightSegment(double StartSeconds, double DurationSeconds);
}

public sealed record MaterialHighlightProjectRequest(
    string ProjectKey,
    string DisplayName,
    string SourceProjectDir,
    string? WorkflowProjectDir,
    string? PublishConfigPath);

public sealed record MaterialHighlightProjectResult(
    bool UsesMaterialClipSource,
    int GeneratedClipCount,
    int ExistingClipCount,
    int TotalOutputCount,
    string Reason);
