using System.Text;
using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class ManualMaterialProjectService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly byte[] PlaceholderPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0xF8, 0x0F, 0x00, 0x00,
        0x01, 0x01, 0x00, 0x05, 0x18, 0xD8, 0x4E, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    public IReadOnlyList<string> ListVideoFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => new FileInfo(path).Length > 0)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ManualMaterialProjectResult CreateProject(ManualMaterialProjectRequest request)
    {
        var workspaceRoot = Path.GetFullPath(request.WorkspaceRoot);
        var videoSourceDir = Path.GetFullPath(request.VideoSourceDirectory);
        var newTitle = request.NewTitle.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            throw new InvalidOperationException("新剧名不能为空。");
        }

        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{workspaceRoot}");
        }

        var originalTitle = string.IsNullOrWhiteSpace(request.OriginalTitle)
            ? newTitle
            : request.OriginalTitle.Trim();

        var videoFiles = ListVideoFiles(videoSourceDir);
        if (videoFiles.Count == 0)
        {
            throw new InvalidOperationException($"所选目录中没有可用的视频文件：{videoSourceDir}");
        }

        var episodeCount = request.EpisodeCount is > 0 ? request.EpisodeCount.Value : videoFiles.Count;
        var sourceProjectDir = Path.Combine(workspaceRoot, SanitizeDirectoryName(originalTitle));
        if (Directory.Exists(sourceProjectDir))
        {
            throw new InvalidOperationException($"源项目目录已存在：{sourceProjectDir}");
        }

        var workflowProjectDir = Path.Combine(workspaceRoot, "workflow", $"_{SanitizeDirectoryName(newTitle)}");
        if (Directory.Exists(workflowProjectDir))
        {
            throw new InvalidOperationException($"workflow 项目目录已存在：{workflowProjectDir}");
        }

        Directory.CreateDirectory(sourceProjectDir);
        Directory.CreateDirectory(workflowProjectDir);
        Directory.CreateDirectory(Path.Combine(workflowProjectDir, "videos"));

        CopyVideos(videoFiles, sourceProjectDir);
        CopyVideos(videoFiles, Path.Combine(workflowProjectDir, "videos"));

        WriteProjectInfo(sourceProjectDir, newTitle, originalTitle, episodeCount);
        WriteProjectInfo(workflowProjectDir, newTitle, originalTitle, episodeCount);

        WriteMetadata(sourceProjectDir, newTitle, originalTitle, sourceProjectDir, workflowProjectDir, videoSourceDir);
        WriteMetadata(workflowProjectDir, newTitle, originalTitle, sourceProjectDir, workflowProjectDir, videoSourceDir);

        EnsurePlaceholderImage(Path.Combine(workflowProjectDir, "海报图片.jpg"));
        EnsurePlaceholderImage(Path.Combine(workflowProjectDir, "成本报表.png"));
        for (var index = 1; index <= 4; index++)
        {
            EnsurePlaceholderImage(Path.Combine(workflowProjectDir, $"工程图_{index}.png"));
        }

        WriteDefaultMaterialPublishConfig(workflowProjectDir);

        return new ManualMaterialProjectResult(
            sourceProjectDir,
            workflowProjectDir,
            videoFiles.Count,
            $"手动素材项目创建成功：{newTitle}，共 {videoFiles.Count} 个视频文件。");
    }

    private static void CopyVideos(IReadOnlyList<string> sourceFiles, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in sourceFiles)
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
            if (!File.Exists(targetPath))
            {
                File.Copy(sourceFile, targetPath, overwrite: false);
            }
        }
    }

    private static void WriteProjectInfo(string targetDirectory, string newTitle, string originalTitle, int episodeCount)
    {
        var totalMinutes = Math.Max(1, episodeCount);
        var costWan = Math.Max(1, (int)Math.Round(totalMinutes * 1500d / 10000d, MidpointRounding.AwayFromZero));
        var lines = new[]
        {
            $"新剧名：{newTitle}",
            $"原剧名：{originalTitle}",
            $"短标题：{newTitle}",
            "标签：短视频",
            $"集数：{episodeCount}",
            $"时长：{totalMinutes}分钟",
            $"成本：{costWan}万元",
            "制作公司：未填写公司"
        };

        File.WriteAllText(
            Path.Combine(targetDirectory, "短剧信息.txt"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            Encoding.UTF8);
    }

    private static void WriteMetadata(
        string targetDirectory,
        string newTitle,
        string originalTitle,
        string sourceProjectDir,
        string workflowProjectDir,
        string originalVideoSourceDir)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = newTitle,
            ["originalTitle"] = originalTitle,
            ["sourceName"] = originalTitle,
            ["episodeCount"] = Math.Max(1, Directory.EnumerateFiles(sourceProjectDir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))),
            ["manualProject"] = true,
            ["sourceProjectDir"] = sourceProjectDir,
            ["workflowProjectDir"] = workflowProjectDir,
            ["manualSourceVideoDir"] = originalVideoSourceDir
        };

        File.WriteAllText(
            Path.Combine(targetDirectory, "shortdrama-project.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static void WriteDefaultMaterialPublishConfig(string workflowProjectDir)
    {
        var payload = new
        {
            task_type = "publish_videos",
            pause_on_error = true,
            video_publish = new
            {
                enabled = true,
                run_strategy = "resume",
                state_file = ".weixin-channel-publish-state.json",
                allow_duplicate_publish = false,
                video_source_mode = "project",
                fill_description = true,
                fill_short_title = false,
                description_template = "{新剧名}",
                prepend_hash_to_description = true,
                location_option_text = "不显示",
                link_option_text = "视频号剧集",
                activity_option_text = "不参与活动",
                timing_option_text = "不定时",
                final_action = "draft",
                episode_selection_mode = "range",
                start_episode_index = 2,
                publish_count = 4
            }
        };

        var configPath = Path.Combine(workflowProjectDir, "weixin-channel-publish-test.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static void EnsurePlaceholderImage(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, PlaceholderPngBytes);
        }
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Trim().Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "manual-project" : sanitized;
    }
}

public sealed record ManualMaterialProjectRequest(
    string WorkspaceRoot,
    string VideoSourceDirectory,
    string NewTitle,
    string OriginalTitle,
    int? EpisodeCount);

public sealed record ManualMaterialProjectResult(
    string SourceProjectDirectory,
    string WorkflowProjectDirectory,
    int VideoCount,
    string Message);
