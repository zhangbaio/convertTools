using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Core.Publishing;

public sealed record WeixinDirectoryMaterialItem(string DirectoryPath, string VideoPath, string Description);

public sealed record WeixinDirectoryMaterialPlan(
    string ConfigPath,
    string OutputDirectory,
    IReadOnlyList<WeixinDirectoryMaterialItem> Items);

public sealed class WeixinDirectoryMaterialPublishService
{
    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".ts", ".wmv", ".webm",
    ];

    private static readonly string[] DescriptionFileNames =
    [
        "description.txt", "desc.txt", "描述.txt",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly IWeixinChannelUploader _uploader;
    private readonly string _dataRoot;

    public WeixinDirectoryMaterialPublishService(IWeixinChannelUploader uploader)
        : this(uploader, PlatformPublisherPaths.DataRoot)
    {
    }

    public WeixinDirectoryMaterialPublishService(IWeixinChannelUploader uploader, string dataRoot)
    {
        _uploader = uploader;
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public IReadOnlyList<WeixinDirectoryMaterialItem> Scan(string workspacePath)
    {
        var root = ResolveExistingDirectory(workspacePath);
        return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(directory => new
            {
                Directory = directory,
                Video = PickLargestVideo(directory),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Video))
            .Select(item => new WeixinDirectoryMaterialItem(
                item.Directory,
                item.Video!,
                NormalizeHashtags(ResolveDescription(item.Directory))))
            .ToArray();
    }

    public async Task PublishAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var plan = Prepare(job);
        progress?.Report($"目录批量发表：扫描到 {plan.Items.Count} 条素材，配置已写入独立数据目录。");

        var result = await _uploader.UploadAsync(
            new WeixinUploadRequest(
                job.Id,
                job.ProjectDirectory,
                $"目录批量发表：{job.ProjectName}",
                plan.ConfigPath,
                Path.GetFileName(plan.ConfigPath)),
            progress,
            cancellationToken);

        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "视频号目录批量发表失败。");
    }

    public WeixinDirectoryMaterialPlan Prepare(PublishJob job)
    {
        var items = Scan(job.ProjectDirectory);
        if (items.Count == 0)
            throw new InvalidOperationException("目录批量发表：未找到包含视频的一级子目录。");

        var jobRoot = Path.Combine(_dataRoot, "jobs", job.Id);
        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);

        var baseSettings = ReadBaseSettings(job.ConfigPath, _dataRoot);
        var videoPaths = items.Select(item => item.VideoPath).ToArray();
        var descriptions = new JsonObject();
        foreach (var item in items)
        {
            AddDescription(descriptions, item.VideoPath, item.Description);
            AddDescription(descriptions, Path.GetFileName(item.VideoPath), item.Description);
            AddDescription(descriptions, Path.GetFileNameWithoutExtension(item.VideoPath), item.Description);
        }

        var videoPublish = new JsonObject
        {
            ["enabled"] = true,
            ["run_strategy"] = "resume",
            ["state_file"] = Path.Combine(jobRoot, "directory-publish-state.json"),
            ["allow_duplicate_publish"] = false,
            ["publish_video_source_mode"] = "directory_publish",
            ["video_source_mode"] = "directory_publish",
            ["publish_video_custom_files"] = ToJsonArray(videoPaths),
            ["publish_video_description_map"] = descriptions,
            ["episode_selection_mode"] = "all",
            ["start_episode_index"] = 1,
            ["publish_count"] = items.Count,
            ["episode_indexes"] = new JsonArray(),
            ["fill_description"] = true,
            ["fill_short_title"] = false,
            ["description_template"] = "{新剧名}",
            ["prepend_hash_to_description"] = false,
            ["location_option_text"] = "不显示位置",
            ["link_option_text"] = string.Empty,
            ["activity_option_text"] = string.Empty,
            ["timing_option_text"] = "不定时",
            ["declare_original"] = true,
            ["merge_publish_enabled"] = false,
            ["merge_publish_group_size"] = 0,
            ["allow_empty_short_title"] = true,
            ["allow_empty_tag"] = true,
            ["final_action"] = "publish",
            ["single_test_final_action"] = "publish",
            ["pause_on_error"] = true,
            ["_runtime_account_profile_id"] = StableAccountId(job),
            ["_runtime_account_profile_name"] = job.AccountName,
            ["video_upload_action"] = new JsonObject
            {
                ["input_selector"] = "input[type='file'][accept*='video'], input[type='file']",
            },
        };

        var root = new JsonObject
        {
            ["task_type"] = "publish_videos",
            ["base_url"] = baseSettings.BaseUrl,
            ["auth_file"] = baseSettings.AuthFile,
            ["output_dir"] = outputDirectory,
            ["pause_on_error"] = true,
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["slow_mo_ms"] = 50,
                ["keep_open_seconds"] = 0,
                ["user_data_dir"] = baseSettings.BrowserProfileDirectory,
            },
            ["debug"] = new JsonObject
            {
                ["log_file"] = Path.Combine(outputDirectory, "run.log"),
                ["save_html"] = true,
                ["save_text"] = true,
            },
            ["video_publish"] = videoPublish,
        };

        var configPath = Path.Combine(jobRoot, "directory-publish-config.json");
        File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return new WeixinDirectoryMaterialPlan(configPath, outputDirectory, items);
    }

    private static (string BaseUrl, string AuthFile, string BrowserProfileDirectory) ReadBaseSettings(
        string configPath,
        string dataRoot)
    {
        var baseUrl = "https://channels.weixin.qq.com";
        var authFile = Path.Combine(dataRoot, "accounts", "default", "weixin-auth.json");
        var profileDirectory = Path.Combine(dataRoot, "accounts", "default", "browser");

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return (baseUrl, authFile, profileDirectory);

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        baseUrl = ReadString(root, "base_url") ?? baseUrl;
        authFile = ResolveConfiguredPath(ReadString(root, "auth_file"), baseDirectory, authFile);
        if (root.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.Object)
        {
            profileDirectory = ResolveConfiguredPath(
                ReadString(browser, "user_data_dir"),
                baseDirectory,
                profileDirectory);
        }

        return (baseUrl, authFile, profileDirectory);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveConfiguredPath(string? configured, string baseDirectory, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
    }

    private static void AddDescription(JsonObject map, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
            map[key] = value;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static string ResolveDescription(string directory)
    {
        foreach (var fileName in DescriptionFileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                continue;

            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return Path.GetFileName(directory);
    }

    private static string? PickLargestVideo(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0)
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();

    private static string NormalizeHashtags(string text)
    {
        var value = text.Trim();
        value = Regex.Replace(value, @"(?<=[^\s#])#", " #");
        return Regex.Replace(value, @"[ \t]{2,}", " ").Trim();
    }

    private static string ResolveExistingDirectory(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new DirectoryNotFoundException("目录批量发表：工作目录不能为空。");

        var root = Path.GetFullPath(workspacePath);
        return Directory.Exists(root)
            ? root
            : throw new DirectoryNotFoundException($"目录批量发表：工作目录不存在：{root}");
    }

    private static string StableAccountId(PublishJob job)
    {
        var source = string.IsNullOrWhiteSpace(job.AccountName) ? "default" : job.AccountName.Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
