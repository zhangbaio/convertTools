using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public sealed class MaterialDirectoryPublishService
{
    private const string ConfigFileName = ".material-dir-publish-config.json";
    private const string DefaultLocationText = "不显示位置";
    private const string DefaultRewritePrompt = """
        你是短视频文案助手。把给定的视频描述改写成一条新的中文描述：
        含义相近但措辞、句式不同，更自然吸引人；必须原样保留其中的话题标签，例如 #话题。
        只输出改写后的描述本身，不要解释，不要加引号。
        """;

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".ts", ".wmv", ".webm"
    ];

    private static readonly string[] DescriptionFileNames =
    [
        "description.txt", "desc.txt", "描述.txt"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IWeixinChannelUploader _weixinChannelUploader;
    private readonly GlobalSettingsService _globalSettingsService;

    public MaterialDirectoryPublishService(
        IWeixinChannelUploader weixinChannelUploader,
        GlobalSettingsService globalSettingsService)
    {
        _weixinChannelUploader = weixinChannelUploader;
        _globalSettingsService = globalSettingsService;
    }

    public IReadOnlyList<MaterialDirectoryPublishItem> ScanPublishItems(string workspacePath)
    {
        var root = ResolveExistingDirectory(workspacePath);
        var items = new List<MaterialDirectoryPublishItem>();

        foreach (var subdir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var video = PickLargestVideo(subdir);
            if (string.IsNullOrWhiteSpace(video))
            {
                continue;
            }

            var description = NormalizeHashtags(ResolveSubdirDescription(subdir));
            items.Add(new MaterialDirectoryPublishItem(
                DirectoryPath: subdir,
                VideoPath: video,
                Description: description,
                OriginalDescription: description,
                AiRewritten: false));
        }

        return items;
    }

    public async Task<MaterialDirectoryPublishResult> PublishAsync(
        MaterialDirectoryPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var root = ResolveExistingDirectory(options.WorkspacePath);
        var items = ScanPublishItems(root).ToList();
        if (items.Count == 0)
        {
            throw new InvalidOperationException("目录批量发表：未找到包含视频的一级子目录。");
        }

        progress?.Report($"目录批量发表：扫描到 {items.Count} 条可发表素材。");
        items = await PrepareItemsAsync(items, options.AiRewriteDescription, progress, cancellationToken);

        foreach (var item in items)
        {
            WritePublishSidecar(item);
        }

        var configPath = WritePublishConfig(root, items, options);
        progress?.Report($"目录批量发表：已写入发表配置 {configPath}");

        var request = new WeixinUploadRequest(
            ProjectKey: BuildProjectKey(root),
            ProjectDir: root,
            DisplayName: $"目录批量发表：{Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}",
            ConfigPath: configPath,
            ConfigName: null);

        var uploadResult = await _weixinChannelUploader.UploadAsync(request, progress, cancellationToken);
        if (!uploadResult.Ok)
        {
            throw new InvalidOperationException(uploadResult.Message ?? "目录批量发表失败。");
        }

        var outputDirectory = Path.Combine(root, "material-dir-publish-output");
        return new MaterialDirectoryPublishResult(
            Total: items.Count,
            ConfigPath: configPath,
            OutputDirectory: outputDirectory,
            Items: items,
            UploadResult: uploadResult);
    }

    private async Task<List<MaterialDirectoryPublishItem>> PrepareItemsAsync(
        IReadOnlyList<MaterialDirectoryPublishItem> items,
        bool aiRewrite,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!aiRewrite)
        {
            return items.ToList();
        }

        var results = new List<MaterialDirectoryPublishItem>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[index];
            var used = item.Description;
            var rewritten = false;

            var cached = TryReadCachedAiDescription(item.VideoPath, item.OriginalDescription);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                used = cached;
                rewritten = true;
                progress?.Report($"目录批量发表：复用 AI 描述 {index + 1}/{items.Count} -> {used}");
            }
            else
            {
                try
                {
                    var next = NormalizeHashtags(await RewriteDescriptionAsync(item.OriginalDescription, cancellationToken));
                    if (!string.IsNullOrWhiteSpace(next))
                    {
                        used = next;
                        rewritten = true;
                        progress?.Report($"目录批量发表：AI 改写 {index + 1}/{items.Count} -> {used}");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"目录批量发表：AI 改写失败，沿用原描述 {index + 1}/{items.Count}：{ex.Message}");
                }
            }

            results.Add(item with
            {
                Description = used,
                AiRewritten = rewritten
            });
        }

        return results;
    }

    private async Task<string> RewriteDescriptionAsync(string source, CancellationToken cancellationToken)
    {
        var settings = _globalSettingsService.Load();
        var endpoint = (settings.AiTextEndpoint ?? string.Empty).Trim().TrimEnd('/');
        var apiKey = (settings.AiTextApiKey ?? string.Empty).Trim();
        var model = (settings.AiTextModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("AI 文本接口未配置，请先在系统设置中填写 Endpoint、Key 和模型。");
        }

        var timeoutSeconds = int.TryParse(settings.AiTextTimeoutSeconds, out var parsed) && parsed > 0
            ? parsed
            : 60;

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 600))
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.7,
                messages = new[]
                {
                    new { role = "system", content = DefaultRewritePrompt },
                    new { role = "user", content = source }
                }
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI 文本接口请求失败：{(int)response.StatusCode} {response.ReasonPhrase}; {body}");
        }

        return ExtractChatContent(body);
    }

    private static string ExtractChatContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim() ?? string.Empty;
        }

        if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string WritePublishConfig(
        string workspacePath,
        IReadOnlyList<MaterialDirectoryPublishItem> items,
        MaterialDirectoryPublishOptions options)
    {
        var outputDirectory = Path.Combine(workspacePath, "material-dir-publish-output");
        Directory.CreateDirectory(outputDirectory);

        if (!string.IsNullOrWhiteSpace(options.AuthFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExpandPath(options.AuthFilePath)) ?? workspacePath);
        }

        if (!string.IsNullOrWhiteSpace(options.BrowserProfileDir))
        {
            Directory.CreateDirectory(ExpandPath(options.BrowserProfileDir));
        }

        var videoPaths = items.Select(item => item.VideoPath).ToArray();
        var videoPublish = new JsonObject
        {
            ["enabled"] = true,
            ["run_strategy"] = "resume",
            ["state_file"] = BuildAccountScopedStateFile(".weixin-channel-directory-publish-state.json", options.AccountId),
            ["allow_duplicate_publish"] = options.AllowDuplicatePublish,
            ["publish_video_source_mode"] = "directory_publish",
            ["video_source_mode"] = "directory_publish",
            ["publish_video_custom_files"] = ToJsonArray(videoPaths),
            ["publish_video_description_map"] = BuildDescriptionMap(items),
            ["episode_selection_mode"] = "all",
            ["start_episode_index"] = 1,
            ["publish_count"] = items.Count,
            ["episode_indexes"] = new JsonArray(),
            ["fill_description"] = true,
            ["fill_short_title"] = false,
            ["description_template"] = "{新剧名}",
            ["prepend_hash_to_description"] = false,
            ["location_option_text"] = options.HideLocation ? DefaultLocationText : string.Empty,
            ["link_option_text"] = string.Empty,
            ["activity_option_text"] = string.Empty,
            ["timing_option_text"] = "不定时",
            ["declare_original"] = options.DeclareOriginal,
            ["merge_publish_enabled"] = false,
            ["merge_publish_group_size"] = 0,
            ["publish_originality_reuse_across_runs"] = true,
            ["allow_empty_short_title"] = true,
            ["allow_empty_tag"] = true,
            ["final_action"] = "publish",
            ["single_test_final_action"] = "publish",
            ["pause_on_error"] = true,
            ["_runtime_account_profile_id"] = options.AccountId ?? string.Empty,
            ["_runtime_account_profile_name"] = options.AccountDisplayName ?? string.Empty,
            ["video_upload_action"] = new JsonObject
            {
                ["input_selector"] = "input[type='file'][accept*='video'], input[type='file']"
            }
        };

        var root = new JsonObject
        {
            ["task_type"] = "publish_videos",
            ["base_url"] = "https://channels.weixin.qq.com",
            ["auth_file"] = options.AuthFilePath ?? string.Empty,
            ["output_dir"] = outputDirectory,
            ["pause_on_error"] = true,
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["slow_mo_ms"] = 50,
                ["keep_open_seconds"] = 0,
                ["user_data_dir"] = options.BrowserProfileDir ?? string.Empty
            },
            ["debug"] = new JsonObject
            {
                ["log_file"] = Path.Combine(outputDirectory, "run.log"),
                ["save_html"] = true,
                ["save_text"] = true
            },
            ["video_publish"] = videoPublish
        };

        var configPath = Path.Combine(workspacePath, ConfigFileName);
        File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return configPath;
    }

    private static JsonObject BuildDescriptionMap(IReadOnlyList<MaterialDirectoryPublishItem> items)
    {
        var map = new JsonObject();
        foreach (var item in items)
        {
            AddDescriptionMapValue(map, item.VideoPath, item.Description);
            AddDescriptionMapValue(map, Path.GetFileName(item.VideoPath), item.Description);
            AddDescriptionMapValue(map, Path.GetFileNameWithoutExtension(item.VideoPath), item.Description);
        }

        return map;
    }

    private static void AddDescriptionMapValue(JsonObject map, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
        {
            map[key] = value;
        }
    }

    private static void WritePublishSidecar(MaterialDirectoryPublishItem item)
    {
        var payload = new JsonObject
        {
            ["description"] = item.Description,
            ["original_description"] = item.OriginalDescription,
            ["ai_rewritten"] = item.AiRewritten
        };

        File.WriteAllText(
            Path.ChangeExtension(item.VideoPath, ".publish.json"),
            payload.ToJsonString(JsonOptions),
            Encoding.UTF8);
    }

    private static string TryReadCachedAiDescription(string videoPath, string originalDescription)
    {
        var sidecar = Path.ChangeExtension(videoPath, ".publish.json");
        if (!File.Exists(sidecar))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = document.RootElement;
            if (!root.TryGetProperty("ai_rewritten", out var rewritten) ||
                rewritten.ValueKind != JsonValueKind.True)
            {
                return string.Empty;
            }

            if (!root.TryGetProperty("original_description", out var original) ||
                original.ValueKind != JsonValueKind.String ||
                !string.Equals(original.GetString()?.Trim(), originalDescription.Trim(), StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return root.TryGetProperty("description", out var description) &&
                   description.ValueKind == JsonValueKind.String
                ? description.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveExistingDirectory(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new DirectoryNotFoundException("目录批量发表：工作目录不能为空。");
        }

        var root = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"目录批量发表：工作目录不存在：{root}");
        }

        return root;
    }

    private static string ResolveSubdirDescription(string subdir)
    {
        foreach (var name in DescriptionFileNames)
        {
            var path = Path.Combine(subdir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch
            {
            }
        }

        return Path.GetFileName(subdir);
    }

    private static string? PickLargestVideo(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0)
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string NormalizeHashtags(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        value = Regex.Replace(value, @"(?<=[^\s#])#", " #");
        value = Regex.Replace(value, @"[ \t]{2,}", " ");
        return value.Trim();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string BuildProjectKey(string workspacePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath)));
        return $"material-dir-publish-{Convert.ToHexString(bytes)[..12].ToLowerInvariant()}";
    }

    private static string BuildAccountScopedStateFile(string stateFile, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return stateFile;
        }

        var safeId = SanitizeAccountId(accountId);
        var extension = Path.GetExtension(stateFile);
        var stem = extension.Length == 0 ? stateFile : stateFile[..^extension.Length];
        return $"{stem}-{safeId}{extension}";
    }

    private static string SanitizeAccountId(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '_');
        return string.IsNullOrWhiteSpace(safe) ? "account" : safe;
    }

    private static string ExpandPath(string path)
    {
        var text = path.Trim();
        if (text.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            text = Path.Combine(home, text.TrimStart('~', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return Path.GetFullPath(text);
    }
}

public sealed record MaterialDirectoryPublishOptions(
    string WorkspacePath,
    string? AuthFilePath,
    string? BrowserProfileDir,
    string? AccountId,
    string? AccountDisplayName,
    bool HideLocation,
    bool DeclareOriginal,
    bool AiRewriteDescription,
    bool AllowDuplicatePublish);

public sealed record MaterialDirectoryPublishItem(
    string DirectoryPath,
    string VideoPath,
    string Description,
    string OriginalDescription,
    bool AiRewritten);

public sealed record MaterialDirectoryPublishResult(
    int Total,
    string ConfigPath,
    string OutputDirectory,
    IReadOnlyList<MaterialDirectoryPublishItem> Items,
    WeixinUploadResult UploadResult);
