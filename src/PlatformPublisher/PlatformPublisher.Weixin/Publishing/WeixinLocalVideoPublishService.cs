using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using PlatformPublisher.Analytics.Services;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinLocalVideoPublishPlan(
    string ConfigPath,
    string SourceMode,
    int AvailableVideoCount,
    int PublishCount,
    IReadOnlyList<string> ResolvedFiles);

public sealed class WeixinLocalVideoPublishService
{
    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".ts", ".wmv", ".webm",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly IWeixinChannelUploader _uploader;
    private readonly string _dataRoot;
    private readonly IAiRuntimeSettingsProvider _aiSettingsProvider;
    private readonly IAnalyticsActivitySink _analyticsSink;

    public WeixinLocalVideoPublishService(IWeixinChannelUploader uploader)
        : this(uploader, PlatformPublisherPaths.DataRoot, EmptyAiRuntimeSettingsProvider.Instance, NullAnalyticsActivitySink.Instance)
    {
    }

    public WeixinLocalVideoPublishService(
        IWeixinChannelUploader uploader,
        IAiRuntimeSettingsProvider aiSettingsProvider)
        : this(uploader, PlatformPublisherPaths.DataRoot, aiSettingsProvider, NullAnalyticsActivitySink.Instance)
    {
    }

    public WeixinLocalVideoPublishService(IWeixinChannelUploader uploader, string dataRoot)
        : this(uploader, dataRoot, EmptyAiRuntimeSettingsProvider.Instance, NullAnalyticsActivitySink.Instance)
    {
    }

    private WeixinLocalVideoPublishService(
        IWeixinChannelUploader uploader,
        string dataRoot,
        IAiRuntimeSettingsProvider aiSettingsProvider,
        IAnalyticsActivitySink analyticsSink)
    {
        _uploader = uploader;
        _dataRoot = Path.GetFullPath(dataRoot);
        _aiSettingsProvider = aiSettingsProvider;
        _analyticsSink = analyticsSink;
    }

    public WeixinLocalVideoPublishService(IWeixinChannelUploader uploader, IAiRuntimeSettingsProvider aiSettingsProvider,
        IAnalyticsActivitySink analyticsSink) : this(uploader, PlatformPublisherPaths.DataRoot, aiSettingsProvider, analyticsSink) { }

    public async Task PublishAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var plan = Prepare(job);
        progress?.Report($"{job.Kind.DisplayName()}：检测到 {plan.AvailableVideoCount} 个视频，本次发表 {plan.PublishCount} 个。");
        var request = new WeixinUploadRequest(job.Id, job.ProjectDirectory, job.ProjectName, plan.ConfigPath, Path.GetFileName(plan.ConfigPath))
        {
            MaterialItemCompleted = item => _analyticsSink.Record(job, item.VideoPath, item.Status, item.CompletedAt),
        };
        var result = await _uploader.UploadAsync(
            request,
            progress,
            cancellationToken);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? $"{job.Kind.DisplayName()}失败。");
    }

    public WeixinLocalVideoPublishPlan Prepare(PublishJob job)
    {
        if (job.Kind is not (PublishJobKind.ProjectMaterials or PublishJobKind.LocalVideos or PublishJobKind.CustomVideos or PublishJobKind.AdxMaterials))
            throw new InvalidOperationException($"不支持的视频素材任务：{job.Kind}");

        var resolvedFiles = ResolveVideoFiles(job);
        if (resolvedFiles.Count == 0)
            throw new InvalidOperationException($"{job.Kind.DisplayName()}：没有找到可发表的视频文件。");

        var options = WeixinPublishOptions.FromJob(job);
        var selectedIndexes = options.ResolveEpisodeIndexes(resolvedFiles.Count, job.PublishCount);
        if (selectedIndexes.Count == 0)
            throw new InvalidOperationException("高级发表配置没有选中有效视频。");
        var publishCount = selectedIndexes.Count;
        var sourceMode = job.Kind switch
        {
            PublishJobKind.ProjectMaterials => "project_materials",
            PublishJobKind.CustomVideos => "custom_files",
            PublishJobKind.AdxMaterials => "custom_files",
            _ => "source_videos",
        };
        var accountId = PublishAccountStorageKey.ForJob(job);
        var jobRoot = Path.Combine(_dataRoot, "jobs", job.Id, "local-video-publish");
        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        var baseSettings = ReadBaseSettings(job.ConfigPath, accountId);

        var publishVideo = new JsonObject
        {
            ["enabled"] = true,
            ["run_strategy"] = "resume",
            ["state_file"] = Path.Combine(jobRoot, "local-video-publish-state.json"),
            ["allow_duplicate_publish"] = job.AllowDuplicatePublish,
            ["publish_video_source_mode"] = sourceMode,
            ["video_source_mode"] = sourceMode,
            ["publish_video_custom_files"] = ToJsonArray(resolvedFiles),
            ["episode_selection_mode"] = "explicit",
            ["start_episode_index"] = options.StartEpisodeIndex,
            ["publish_count"] = publishCount,
            ["episode_indexes"] = ToJsonArray(selectedIndexes),
            ["fill_description"] = options.FillDescription,
            ["fill_short_title"] = options.FillShortTitle,
            ["short_title_max_length"] = options.ShortTitleMaxLength,
            ["description_template"] = string.IsNullOrWhiteSpace(options.DescriptionTemplate)
                ? "热门短剧，精彩内容持续更新。"
                : options.DescriptionTemplate.Trim(),
            ["ai_description_enabled"] = options.AiDescriptionEnabled,
            ["ai_description_use_asr"] = options.AiDescriptionUseAsr,
            ["prepend_hash_to_description"] = options.PrependHashToDescription,
            ["location_option_text"] = options.LocationOptionText,
            ["link_option_text"] = options.LinkOptionText,
            ["link_picker_button_text"] = options.LinkPickerButtonText,
            ["link_dialog_title"] = options.LinkDialogTitle,
            ["link_search_placeholder"] = options.LinkSearchPlaceholder,
            ["activity_option_text"] = options.ActivityOptionText,
            ["timing_option_text"] = options.TimingOptionText,
            ["replace_cover_with_local_image"] = options.ReplaceCoverWithLocalImage,
            ["cover_image_path"] = options.CoverImagePath,
            ["declare_original"] = options.DeclareOriginal,
            ["merge_publish_enabled"] = options.MergePublishEnabled,
            ["merge_publish_group_size"] = options.MergePublishGroupSize,
            ["final_action"] = options.FinalAction,
            ["pause_on_error"] = options.PauseOnError,
            ["fast_mode"] = options.FastMode,
            ["_runtime_account_profile_id"] = accountId,
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
            ["pause_on_error"] = options.PauseOnError,
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["slow_mo_ms"] = 50,
                ["keep_open_seconds"] = 0,
                ["user_data_dir"] = baseSettings.BrowserProfileDirectory,
            },
            ["debug"] = new JsonObject
            {
                ["save_html"] = options.CaptureDebugDumps,
                ["save_text"] = options.CaptureDebugDumps,
                ["capture_screenshots"] = options.CaptureScreenshots,
                ["log_file"] = Path.Combine(outputDirectory, "run.log"),
            },
            ["video_publish"] = publishVideo,
        };
        WeixinAiSettingsInjector.Apply(publishVideo, _aiSettingsProvider);

        var configPath = Path.Combine(jobRoot, "local-video-publish-config.json");
        File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return new WeixinLocalVideoPublishPlan(configPath, sourceMode, resolvedFiles.Count, publishCount, resolvedFiles);
    }

    public static IReadOnlyList<string> ResolveVideoFiles(PublishJob job)
    {
        IEnumerable<string> candidates = job.Kind switch
        {
            PublishJobKind.CustomVideos => job.CustomVideoFiles,
            PublishJobKind.AdxMaterials => job.CustomVideoFiles,
            PublishJobKind.LocalVideos => EnumerateTopLevelVideos(job.ProjectDirectory),
            PublishJobKind.ProjectMaterials => ResolveProjectMaterialFiles(job.ProjectDirectory),
            _ => [],
        };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) && IsVideo(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NaturalSortToken, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveProjectMaterialFiles(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory)) return [];
        var materials = EnumerateTopLevelVideos(Path.Combine(projectDirectory, "material-videos")).ToArray();
        var videos = EnumerateTopLevelVideos(Path.Combine(projectDirectory, "videos")).ToArray();
        return materials.Length >= videos.Length && materials.Length > 0 ? materials : videos;
    }

    private static IEnumerable<string> EnumerateTopLevelVideos(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).Where(IsVideo)
            : [];

    private static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string NaturalSortToken(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var number) ? number.ToString("D16") : name;
    }

    private (string BaseUrl, string AuthFile, string BrowserProfileDirectory) ReadBaseSettings(
        string configPath,
        string accountId)
    {
        var accountRoot = Path.Combine(_dataRoot, "accounts", accountId);
        var result = (
            BaseUrl: "https://channels.weixin.qq.com",
            AuthFile: Path.Combine(accountRoot, "weixin-auth.json"),
            BrowserProfileDirectory: Path.Combine(accountRoot, "browser"));
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return result;

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var baseUrl = ReadString(root, "base_url") ?? result.BaseUrl;
        var auth = ResolvePath(ReadString(root, "auth_file"), baseDirectory, result.AuthFile);
        var profile = result.BrowserProfileDirectory;
        if (root.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.Object)
            profile = ResolvePath(ReadString(browser, "user_data_dir"), baseDirectory, profile);
        return (baseUrl, auth, profile);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolvePath(string? value, string baseDirectory, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
