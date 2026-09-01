using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinSystemHighlightPlan(string ProjectDirectory, string ConfigPath, string Title, int PublishCount);

public sealed class WeixinSystemHighlightPublishService
{
    public const string DefaultDescription = "热播爆火剧，点击链接，免费观看全集。热门#爆火";
    private static readonly string[] SupportedVideoTypes = ["混剪", "解说", "切片"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly IWeixinChannelUploader _uploader;
    private readonly string _dataRoot;
    private readonly IAiRuntimeSettingsProvider _aiSettingsProvider;

    public WeixinSystemHighlightPublishService(IWeixinChannelUploader uploader)
        : this(uploader, PlatformPublisherPaths.DataRoot, EmptyAiRuntimeSettingsProvider.Instance)
    {
    }

    public WeixinSystemHighlightPublishService(
        IWeixinChannelUploader uploader,
        IAiRuntimeSettingsProvider aiSettingsProvider)
        : this(uploader, PlatformPublisherPaths.DataRoot, aiSettingsProvider)
    {
    }

    public WeixinSystemHighlightPublishService(IWeixinChannelUploader uploader, string dataRoot)
        : this(uploader, dataRoot, EmptyAiRuntimeSettingsProvider.Instance)
    {
    }

    private WeixinSystemHighlightPublishService(
        IWeixinChannelUploader uploader,
        string dataRoot,
        IAiRuntimeSettingsProvider aiSettingsProvider)
    {
        _uploader = uploader;
        _dataRoot = Path.GetFullPath(dataRoot);
        _aiSettingsProvider = aiSettingsProvider;
    }

    public async Task PublishAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var plan = Prepare(job);
        progress?.Report($"系统高光发表：开始处理《{plan.Title}》，目标 {plan.PublishCount} 条。");
        var result = await _uploader.UploadAsync(
            new WeixinUploadRequest(job.Id, plan.ProjectDirectory, plan.Title, plan.ConfigPath, Path.GetFileName(plan.ConfigPath)),
            progress,
            cancellationToken);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? $"系统高光发表失败：《{plan.Title}》");
    }

    public WeixinSystemHighlightPlan Prepare(PublishJob job)
    {
        var title = job.DramaTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("系统高光发表必须填写剧名。");

        var count = Math.Clamp(job.PublishCount, 1, 100);
        var accountId = PublishAccountStorageKey.ForJob(job);
        var projectDirectory = Path.Combine(_dataRoot, "jobs", job.Id, "system-highlight");
        var outputDirectory = Path.Combine(projectDirectory, "output");
        Directory.CreateDirectory(outputDirectory);
        var baseSettings = ReadBaseSettings(job.ConfigPath, accountId);
        var types = NormalizeVideoTypes(job.PublishVideoTypes);
        var options = WeixinPublishOptions.FromJob(job);

        File.WriteAllText(
            Path.Combine(projectDirectory, "短剧信息.txt"),
            $"新剧名: {title}{Environment.NewLine}原剧名: {title}{Environment.NewLine}集数: {count}{Environment.NewLine}",
            Encoding.UTF8);

        var publishVideo = new JsonObject
        {
            ["enabled"] = true,
            ["run_strategy"] = "resume",
            ["state_file"] = Path.Combine(projectDirectory, "system-highlight-state.json"),
            ["allow_duplicate_publish"] = job.AllowDuplicatePublish,
            ["publish_video_source_mode"] = "system_highlight",
            ["video_source_mode"] = "system_highlight",
            ["system_highlight_drama_title"] = title,
            ["episode_selection_mode"] = "range",
            ["start_episode_index"] = 1,
            ["publish_count"] = count,
            ["episode_indexes"] = ToJsonArray(Enumerable.Range(1, count)),
            ["system_highlight_publish_target_mode"] = "type",
            ["system_highlight_publish_video_types"] = ToJsonArray(types),
            ["system_highlight_regenerate_after_publish"] = job.RegenerateHighlightsAfterPublish,
            ["system_highlight_regenerate_video_types"] = ToJsonArray(types),
            ["merge_publish_enabled"] = false,
            ["replace_cover_with_local_image"] = false,
            ["fill_description"] = options.FillDescription,
            ["fill_short_title"] = options.FillShortTitle,
            ["short_title_max_length"] = options.ShortTitleMaxLength,
            ["description_template"] = string.IsNullOrWhiteSpace(options.DescriptionTemplate)
                ? DefaultDescription
                : options.DescriptionTemplate,
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
            ["declare_original"] = false,
            ["final_action"] = options.FinalAction,
            ["pause_on_error"] = options.PauseOnError,
            ["fast_mode"] = options.FastMode,
            ["_runtime_account_profile_id"] = accountId,
            ["_runtime_account_profile_name"] = job.AccountName,
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

        var configPath = Path.Combine(projectDirectory, "system-highlight-config.json");
        File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return new WeixinSystemHighlightPlan(projectDirectory, configPath, title, count);
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
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return result;

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var baseUrl = ReadString(root, "base_url") ?? result.BaseUrl;
        var auth = ResolvePath(ReadString(root, "auth_file"), baseDirectory, result.AuthFile);
        var browserProfile = result.BrowserProfileDirectory;
        if (root.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.Object)
            browserProfile = ResolvePath(ReadString(browser, "user_data_dir"), baseDirectory, browserProfile);
        return (baseUrl, auth, browserProfile);
    }

    private static IReadOnlyList<string> NormalizeVideoTypes(string value)
    {
        var requested = (value ?? string.Empty)
            .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return SupportedVideoTypes.Where(type => requested.Count == 0 || requested.Contains(type)).DefaultIfEmpty("混剪").ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolvePath(string? value, string baseDirectory, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
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
