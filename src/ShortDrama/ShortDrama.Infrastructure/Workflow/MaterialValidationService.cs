using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class MaterialValidationService : IMaterialValidationService
{
    private const long MinimumVideoBitrateBps = 4_194_304;
    private static readonly Regex EpisodeRegex = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];

    private readonly IProjectInfoParser _projectInfoParser;
    private readonly IExternalProcessRunner _processRunner;

    public MaterialValidationService(
        IProjectInfoParser projectInfoParser,
        IExternalProcessRunner processRunner)
    {
        _projectInfoParser = projectInfoParser;
        _processRunner = processRunner;
    }

    public async Task<MaterialValidationResult> ValidateAsync(
        string workflowProjectDir,
        CancellationToken cancellationToken)
    {
        var issues = new List<MaterialValidationIssue>();
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            issues.Add(new MaterialValidationIssue("workflow-missing", "错误", $"工作流目录不存在: {workflowProjectDir}", workflowProjectDir));
            return new MaterialValidationResult(issues);
        }

        var infoPath = Path.Combine(workflowProjectDir, "短剧信息.txt");
        var posterPath = Path.Combine(workflowProjectDir, "海报图片.jpg");
        var costPath = Path.Combine(workflowProjectDir, "成本报表.png");
        var videosDir = Path.Combine(workflowProjectDir, "videos");
        var projectImages = Directory.EnumerateFiles(workflowProjectDir, "工程图_*.png", SearchOption.TopDirectoryOnly).ToArray();

        if (!File.Exists(infoPath))
        {
            issues.Add(new MaterialValidationIssue("info-missing", "错误", "缺少短剧信息.txt。", infoPath, CanAutoFix: true));
        }

        if (!File.Exists(posterPath))
        {
            issues.Add(new MaterialValidationIssue("poster-missing", "错误", "缺少海报图片.jpg。", posterPath, CanAutoFix: true));
        }

        if (!File.Exists(costPath))
        {
            issues.Add(new MaterialValidationIssue("cost-missing", "错误", "缺少成本报表.png。", costPath, CanAutoFix: true));
        }

        if (projectImages.Length < 4)
        {
            issues.Add(new MaterialValidationIssue("project-images-missing", "错误", $"工程图不足 4 张，当前仅 {projectImages.Length} 张。", workflowProjectDir, CanAutoFix: true));
        }

        if (!Directory.Exists(videosDir))
        {
            issues.Add(new MaterialValidationIssue("videos-dir-missing", "错误", "缺少 videos 文件夹。", videosDir, CanAutoFix: true));
        }

        if (!EnumerateWeixinConfigPaths(workflowProjectDir).Any())
        {
            issues.Add(new MaterialValidationIssue(
                "weixin-upload-config-missing",
                "错误",
                "缺少微信剧集上传配置文件。",
                workflowProjectDir,
                CanAutoFix: true));
        }

        ProjectInfo? projectInfo = null;
        if (File.Exists(infoPath))
        {
            try
            {
                projectInfo = await _projectInfoParser.ParseAsync(workflowProjectDir, cancellationToken);
            }
            catch (Exception ex)
            {
                issues.Add(new MaterialValidationIssue("info-invalid", "错误", $"短剧信息.txt 无法解析：{ex.Message}", infoPath, CanAutoFix: true));
            }
        }

        var expectedTitle = projectInfo?.Title?.Trim();
        if (!string.IsNullOrWhiteSpace(expectedTitle))
        {
            ValidateTitleConsistency(issues, workflowProjectDir, expectedTitle);
        }

        if (Directory.Exists(videosDir))
        {
            var expectedMaterialVideoCount = 0;
            foreach (var video in Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                expectedMaterialVideoCount++;
                var fileName = Path.GetFileNameWithoutExtension(video);
                if (!string.IsNullOrWhiteSpace(expectedTitle) &&
                    !fileName.StartsWith(expectedTitle + "-", StringComparison.Ordinal))
                {
                    issues.Add(new MaterialValidationIssue(
                        "video-title-mismatch",
                        "错误",
                        $"视频文件名与新剧名不一致：{Path.GetFileName(video)}",
                        video,
                        CanAutoFix: true));
                }

                var bitrate = await TryReadVideoBitrateAsync(video, cancellationToken);
                if (bitrate is null)
                {
                    issues.Add(new MaterialValidationIssue(
                        "video-bitrate-unreadable",
                        "警告",
                        $"无法读取视频码率：{Path.GetFileName(video)}",
                        video,
                        CanAutoFix: true));
                    continue;
                }

                if (bitrate.Value < MinimumVideoBitrateBps)
                {
                    var episodeLabel = ResolveEpisodeLabel(video);
                    issues.Add(new MaterialValidationIssue(
                        "video-bitrate-low",
                        "错误",
                        $"{episodeLabel} 视频码率不足：{bitrate.Value / 1024d / 1024d:0.##} Mbps",
                        video,
                        CanAutoFix: true));
                }
            }

            var materialVideosDir = Path.Combine(workflowProjectDir, "material-videos");
            if (Directory.Exists(materialVideosDir))
            {
                var materialVideos = Directory.EnumerateFiles(materialVideosDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var video in materialVideos)
                {
                    var fileName = Path.GetFileNameWithoutExtension(video);
                    if (!string.IsNullOrWhiteSpace(expectedTitle) &&
                        !fileName.StartsWith(expectedTitle + "-", StringComparison.Ordinal))
                    {
                        issues.Add(new MaterialValidationIssue(
                            "material-video-title-mismatch",
                            "错误",
                            $"素材视频文件名与新剧名不一致：{Path.GetFileName(video)}",
                            video,
                            CanAutoFix: true));
                    }
                }
            }
        }

        return new MaterialValidationResult(issues);
    }

    private static void ValidateTitleConsistency(
        ICollection<MaterialValidationIssue> issues,
        string workflowProjectDir,
        string expectedTitle)
    {
        var workflowName = Path.GetFileName(workflowProjectDir).TrimStart('_');
        if (!string.Equals(workflowName, expectedTitle, StringComparison.Ordinal))
        {
                issues.Add(new MaterialValidationIssue(
                    "workflow-title-mismatch",
                    "错误",
                    $"目录名与新剧名不一致：{workflowName} != {expectedTitle}",
                    workflowProjectDir,
                    CanAutoFix: false));
        }

        foreach (var configPath in EnumerateWeixinConfigPaths(workflowProjectDir))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                var actions = root?["first_page"]?["actions"] as JsonArray;
                var titleAction = actions?
                    .OfType<JsonObject>()
                    .FirstOrDefault(node =>
                        string.Equals(node["type"]?.GetValue<string>(), "fill", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(node["label"]?.GetValue<string>(), "剧目名称", StringComparison.Ordinal));
                var configuredTitle = titleAction?["value"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(configuredTitle) &&
                    !string.Equals(configuredTitle, expectedTitle, StringComparison.Ordinal))
                {
                    issues.Add(new MaterialValidationIssue(
                        "weixin-title-mismatch",
                        "错误",
                        $"微信配置剧目名称不一致：{configuredTitle} != {expectedTitle}",
                        configPath,
                        CanAutoFix: true));
                }
            }
            catch
            {
                issues.Add(new MaterialValidationIssue(
                    "weixin-config-invalid",
                    "警告",
                    $"微信配置无法解析：{Path.GetFileName(configPath)}",
                    configPath));
            }
        }
    }

    private static IEnumerable<string> EnumerateWeixinConfigPaths(string workflowProjectDir)
    {
        foreach (var name in new[] { "weixin-channel-autogen.json", "weixin-channel-submit.json", "weixin-channel-config.json" })
        {
            var path = Path.Combine(workflowProjectDir, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private async Task<long?> TryReadVideoBitrateAsync(string videoPath, CancellationToken cancellationToken)
    {
        var streamResult = await _processRunner.RunAsync(
            "ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=bit_rate", "-of", "default=noprint_wrappers=1:nokey=1", videoPath],
            Path.GetDirectoryName(videoPath),
            cancellationToken);

        var text = streamResult.StandardOutput.Trim();
        if (streamResult.ExitCode == 0 && long.TryParse(text, out var streamBitrate) && streamBitrate > 0)
        {
            return streamBitrate;
        }

        var formatResult = await _processRunner.RunAsync(
            "ffprobe",
            ["-v", "error", "-show_entries", "format=bit_rate", "-of", "default=noprint_wrappers=1:nokey=1", videoPath],
            Path.GetDirectoryName(videoPath),
            cancellationToken);

        text = formatResult.StandardOutput.Trim();
        return formatResult.ExitCode == 0 && long.TryParse(text, out var formatBitrate) && formatBitrate > 0
            ? formatBitrate
            : null;
    }

    private static string ResolveEpisodeLabel(string videoPath)
    {
        var match = EpisodeRegex.Match(Path.GetFileNameWithoutExtension(videoPath));
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode)
            ? $"第{episode}集"
            : Path.GetFileName(videoPath);
    }
}
