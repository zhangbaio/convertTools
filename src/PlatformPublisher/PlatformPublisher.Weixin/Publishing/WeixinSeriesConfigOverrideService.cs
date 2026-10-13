using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinSeriesConfigOverridePlan(
    string SourceConfigPath,
    string OverrideConfigPath,
    int OriginalVideoCount,
    int SelectedVideoCount);

public sealed class WeixinSeriesConfigOverrideService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly string _dataRoot;
    private readonly IAiRuntimeSettingsProvider _aiSettingsProvider;

    public WeixinSeriesConfigOverrideService(IAiRuntimeSettingsProvider aiSettingsProvider)
        : this(PlatformPublisherPaths.DataRoot, aiSettingsProvider)
    {
    }

    public WeixinSeriesConfigOverrideService(string dataRoot, IAiRuntimeSettingsProvider? aiSettingsProvider = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _aiSettingsProvider = aiSettingsProvider ?? EmptyAiRuntimeSettingsProvider.Instance;
    }

    public WeixinSeriesConfigOverridePlan? Prepare(PublishJob job)
    {
        var sourceConfigPath = ResolveSourceConfigPath(job);
        if (sourceConfigPath is null)
            return null;

        var sourceDirectory = Path.GetDirectoryName(sourceConfigPath)!;
        var root = JsonNode.Parse(File.ReadAllText(sourceConfigPath, Encoding.UTF8))?.AsObject()
                   ?? throw new InvalidOperationException("视频号剧集配置不是有效 JSON 对象。");
        AbsolutizeKnownPaths(root, sourceDirectory);

        var originalPaths = ResolveUploadPaths(root);
        var selectedPaths = originalPaths;
        if (!string.IsNullOrWhiteSpace(job.PlatformOptionsJson) && originalPaths.Count > 0)
        {
            var options = WeixinPublishOptions.FromJob(job);
            var indexes = options.ResolveEpisodeIndexes(originalPaths.Count, job.PublishCount);
            selectedPaths = indexes.Select(index => originalPaths[index - 1]).ToArray();
            ApplyUploadPaths(root, selectedPaths);
        }

        var jobRoot = Path.Combine(_dataRoot, "jobs", job.Id, "series-upload");
        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        root["output_dir"] = outputDirectory;
        if (!string.IsNullOrWhiteSpace(job.AccountSessionDirectory))
        {
            var sessionDirectory = Path.GetFullPath(job.AccountSessionDirectory);
            Directory.CreateDirectory(sessionDirectory);
            root["auth_file"] = Path.Combine(sessionDirectory, "weixin-series-auth.json");
            EnsureObject(root, "browser")["user_data_dir"] = sessionDirectory;
        }
        var debug = EnsureObject(root, "debug");
        var publishOptions = WeixinPublishOptions.FromJob(job);
        debug["log_file"] = Path.Combine(outputDirectory, "run.log");
        debug["save_html"] = publishOptions.CaptureDebugDumps;
        debug["save_text"] = publishOptions.CaptureDebugDumps;
        debug["capture_screenshots"] = publishOptions.CaptureScreenshots;
        root["pause_on_error"] = publishOptions.PauseOnError;

        if (root["video_publish"] is JsonObject videoPublish)
        {
            videoPublish["fast_mode"] = publishOptions.FastMode;
            WeixinAiSettingsInjector.Apply(videoPublish, _aiSettingsProvider);
        }

        var overridePath = Path.Combine(jobRoot, "series-upload-config.json");
        File.WriteAllText(overridePath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return new WeixinSeriesConfigOverridePlan(
            sourceConfigPath,
            overridePath,
            originalPaths.Count,
            selectedPaths.Count);
    }

    private static string? ResolveSourceConfigPath(PublishJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.ConfigPath) && File.Exists(job.ConfigPath))
            return Path.GetFullPath(job.ConfigPath);
        if (!Directory.Exists(job.ProjectDirectory))
            return null;

        foreach (var name in new[]
                 {
                     "weixin-channel-autogen.json",
                     "weixin-channel-submit.json",
                     "weixin-channel-config.json",
                 })
        {
            var candidate = Path.Combine(job.ProjectDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveUploadPaths(JsonObject root)
    {
        if (root["second_page"] is not JsonObject secondPage ||
            secondPage["upload"] is not JsonObject upload ||
            upload["paths"] is not JsonArray paths)
            return [];
        return paths
            .Select(node => node?.GetValue<string>() ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private static void ApplyUploadPaths(JsonObject root, IReadOnlyList<string> paths)
    {
        var secondPage = EnsureObject(root, "second_page");
        var upload = EnsureObject(secondPage, "upload");
        upload["paths"] = ToJsonArray(paths);
        if (secondPage["upload_queue"] is JsonObject queue && queue["items"] is JsonArray items)
        {
            var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.OfType<JsonObject>())
            {
                var path = item["path"]?.GetValue<string>() ?? string.Empty;
                item["enabled"] = selected.Contains(path);
            }
        }
    }

    private static void AbsolutizeKnownPaths(JsonObject root, string sourceDirectory)
    {
        AbsolutizeProperty(root, "auth_file", sourceDirectory);
        if (root["browser"] is JsonObject browser)
            AbsolutizeProperty(browser, "user_data_dir", sourceDirectory);
        if (root["first_page"] is JsonObject firstPage && firstPage["actions"] is JsonArray actions)
        {
            foreach (var action in actions.OfType<JsonObject>())
                AbsolutizeArray(action, "paths", sourceDirectory);
        }
        if (root["second_page"] is JsonObject secondPage)
        {
            if (secondPage["upload"] is JsonObject upload)
                AbsolutizeArray(upload, "paths", sourceDirectory);
            if (secondPage["upload_queue"] is JsonObject queue && queue["items"] is JsonArray items)
            {
                foreach (var item in items.OfType<JsonObject>())
                    AbsolutizeProperty(item, "path", sourceDirectory);
            }
        }
        if (root["video_publish"] is JsonObject publish)
        {
            AbsolutizeProperty(publish, "cover_image_path", sourceDirectory);
            AbsolutizeArray(publish, "publish_video_custom_files", sourceDirectory);
        }
    }

    private static void AbsolutizeProperty(JsonObject node, string propertyName, string sourceDirectory)
    {
        var value = node[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)) return;
        node[propertyName] = ResolvePath(value, sourceDirectory);
    }

    private static void AbsolutizeArray(JsonObject node, string propertyName, string sourceDirectory)
    {
        if (node[propertyName] is not JsonArray array) return;
        for (var index = 0; index < array.Count; index++)
        {
            var value = array[index]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value)) array[index] = ResolvePath(value, sourceDirectory);
        }
    }

    private static string ResolvePath(string value, string sourceDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(sourceDirectory, expanded));
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject value) return value;
        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
