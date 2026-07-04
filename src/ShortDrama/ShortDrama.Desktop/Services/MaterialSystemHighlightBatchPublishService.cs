using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public sealed class MaterialSystemHighlightBatchPublishService
{
    public const string DefaultDescription = "热播爆火剧，点击链接，免费观看全集。热门#爆火";
    public static readonly string[] VideoTypeOptions = ["混剪", "解说", "切片"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IWeixinChannelUploader _weixinChannelUploader;

    public MaterialSystemHighlightBatchPublishService(IWeixinChannelUploader weixinChannelUploader)
    {
        _weixinChannelUploader = weixinChannelUploader;
    }

    public async Task<MaterialSystemHighlightBatchPublishResult> PublishAsync(
        MaterialSystemHighlightBatchPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var projects = CreateBatchProjects(options, progress);
        if (projects.Count == 0)
        {
            throw new InvalidOperationException("系统高光发布：请至少填写一个剧名。");
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"系统高光发布：开始 {project.Title}，目标 {project.PublishCount} 个。");
            try
            {
                var result = await _weixinChannelUploader.UploadAsync(
                    new WeixinUploadRequest(
                        ProjectKey: project.ProjectKey,
                        ProjectDir: project.ProjectDirectory,
                        DisplayName: project.Title,
                        ConfigPath: project.ConfigPath,
                        ConfigName: null),
                    progress,
                    cancellationToken);

                if (!result.Ok)
                {
                    failed++;
                    progress?.Report($"系统高光发布失败：{project.Title}，{result.Message}");
                    if (options.StopOnProjectError)
                    {
                        throw new InvalidOperationException(result.Message ?? $"系统高光发布失败：{project.Title}");
                    }
                }
                else
                {
                    succeeded++;
                    progress?.Report($"系统高光发布完成：{project.Title}");
                }
            }
            catch when (!options.StopOnProjectError)
            {
                failed++;
                progress?.Report($"系统高光发布异常：{project.Title}，已跳过继续下一部。");
            }
        }

        return new MaterialSystemHighlightBatchPublishResult(projects, succeeded, failed);
    }

    public IReadOnlyList<MaterialSystemHighlightProject> CreateBatchProjects(
        MaterialSystemHighlightBatchPublishOptions options,
        IProgress<string>? progress = null)
    {
        var workspaceRoot = NormalizeWorkspaceRoot(options.WorkspacePath);
        var workflowRoot = Path.Combine(workspaceRoot, "workflow");
        Directory.CreateDirectory(workflowRoot);

        var result = new List<MaterialSystemHighlightProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in ParseTitles(options.TitlesText))
        {
            if (!seen.Add(NormalizeTitleKey(title)))
            {
                continue;
            }

            var projectDir = ResolveRecordWorkflowDir(workspaceRoot, title, options.AccountId);
            Directory.CreateDirectory(projectDir);
            var sourceProjectDir = ResolveExistingSourceProjectDir(workspaceRoot, title);
            WriteRecordProjectMetadata(projectDir, title, sourceProjectDir, options.AccountId);
            WriteRecordProjectInfo(projectDir, title, options.PublishCount, sourceProjectDir);
            var configPath = WriteMaterialPublishConfig(projectDir, title, options);
            progress?.Report($"系统高光发布：已准备记录项目 {title} -> {projectDir}");

            result.Add(new MaterialSystemHighlightProject(
                ProjectKey: Path.GetFileName(projectDir),
                Title: title,
                ProjectDirectory: projectDir,
                ConfigPath: configPath,
                SourceProjectDirectory: sourceProjectDir,
                PublishCount: Math.Max(1, options.PublishCount)));
        }

        return result;
    }

    private static string WriteMaterialPublishConfig(
        string projectDir,
        string title,
        MaterialSystemHighlightBatchPublishOptions options)
    {
        var outputDir = Path.Combine(projectDir, "output");
        Directory.CreateDirectory(outputDir);

        var publishVideo = new JsonObject
        {
            ["enabled"] = true,
            ["run_strategy"] = "resume",
            ["state_file"] = ".weixin-channel-material-publish-state.json",
            ["allow_duplicate_publish"] = options.AllowDuplicatePublish,
            ["publish_video_source_mode"] = "system_highlight",
            ["video_source_mode"] = "system_highlight",
            ["system_highlight_drama_title"] = title,
            ["episode_selection_mode"] = "range",
            ["start_episode_index"] = 1,
            ["publish_count"] = Math.Max(1, options.PublishCount),
            ["episode_indexes"] = ToJsonArray(Enumerable.Range(1, Math.Max(1, options.PublishCount))),
            ["system_highlight_publish_target_mode"] = NormalizePublishTargetMode(options.PublishTargetMode),
            ["system_highlight_publish_video_types"] = ToJsonArray(NormalizeVideoTypes(options.PublishVideoTypes)),
            ["system_highlight_regenerate_after_publish"] = options.RegenerateAfterPublish,
            ["system_highlight_regenerate_video_types"] = ToJsonArray(NormalizeVideoTypes(options.RegenerateVideoTypes)),
            ["merge_publish_enabled"] = false,
            ["merge_publish_group_size"] = 0,
            ["replace_cover_with_local_image"] = false,
            ["cover_image_path"] = string.Empty,
            ["fill_description"] = true,
            ["fill_short_title"] = false,
            ["description_template"] = string.IsNullOrWhiteSpace(options.DefaultDescription)
                ? DefaultDescription
                : options.DefaultDescription.Trim(),
            ["prepend_hash_to_description"] = false,
            ["location_option_text"] = "不显示",
            ["link_option_text"] = string.Empty,
            ["activity_option_text"] = "不参与活动",
            ["timing_option_text"] = "不定时",
            ["declare_original"] = false,
            ["final_action"] = "publish",
            ["pause_on_error"] = true,
            ["_runtime_account_profile_id"] = options.AccountId ?? string.Empty,
            ["_runtime_account_profile_name"] = options.AccountDisplayName ?? string.Empty
        };

        var root = new JsonObject
        {
            ["task_type"] = "publish_videos",
            ["base_url"] = "https://channels.weixin.qq.com",
            ["auth_file"] = options.AuthFilePath ?? string.Empty,
            ["output_dir"] = outputDir,
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
                ["save_html"] = true,
                ["save_text"] = true,
                ["log_file"] = Path.Combine(outputDir, "run.log")
            },
            ["video_publish"] = publishVideo
        };

        var configPath = Path.Combine(projectDir, "weixin-channel-material.json");
        File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        return configPath;
    }

    private static void WriteRecordProjectMetadata(
        string projectDir,
        string title,
        string? sourceProjectDir,
        string? accountId)
    {
        var payload = new JsonObject
        {
            ["projectType"] = "system_highlight_publish_record",
            ["displayName"] = title,
            ["title"] = title,
            ["newTitle"] = title,
            ["originalTitle"] = title,
            ["sourceTitle"] = title,
            ["shortTitle"] = SanitizeShortTitle(title),
            ["tags"] = string.Empty,
            ["workflowDirName"] = Path.GetFileName(projectDir),
            ["workflowProjectDir"] = projectDir
        };
        if (!string.IsNullOrWhiteSpace(sourceProjectDir))
        {
            payload["sourceProjectDir"] = sourceProjectDir;
        }

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            payload["materialUploadAccountProfileId"] = SanitizeProfileId(accountId);
        }

        File.WriteAllText(
            Path.Combine(projectDir, "shortdrama-project.json"),
            payload.ToJsonString(JsonOptions),
            Encoding.UTF8);
    }

    private static void WriteRecordProjectInfo(string projectDir, string title, int publishCount, string? sourceProjectDir)
    {
        var originalTitle = title;
        if (!string.IsNullOrWhiteSpace(sourceProjectDir))
        {
            var sourceTitles = CollectProjectTitles(sourceProjectDir);
            originalTitle = sourceTitles.OrderBy(item => item.Length).FirstOrDefault() ?? title;
        }

        var lines = new[]
        {
            $"新剧名: {title}",
            $"原剧名: {originalTitle}",
            $"短标题: {SanitizeShortTitle(title)}",
            "标签: ",
            $"集数: {Math.Max(1, publishCount)}"
        };
        File.WriteAllText(
            Path.Combine(projectDir, "短剧信息.txt"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            Encoding.UTF8);
    }

    private static string? ResolveExistingSourceProjectDir(string workspaceRoot, string title)
    {
        var targetKey = NormalizeTitleKey(title);
        if (targetKey.Length == 0 || !Directory.Exists(workspaceRoot))
        {
            return null;
        }

        foreach (var child in Directory.EnumerateDirectories(workspaceRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                name.Equals("workflow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CollectProjectTitles(child).Any(candidate => NormalizeTitleKey(candidate) == targetKey))
            {
                return Path.GetFullPath(child);
            }
        }

        return null;
    }

    private static HashSet<string> CollectProjectTitles(string projectDir)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath, Encoding.UTF8));
                foreach (var key in new[] { "displayName", "title", "newTitle", "originalTitle", "sourceTitle", "sourceName" })
                {
                    if (document.RootElement.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        titles.Add(value.GetString()!.Trim());
                    }
                }
            }
            catch
            {
            }
        }

        var infoPath = Path.Combine(projectDir, "短剧信息.txt");
        if (File.Exists(infoPath))
        {
            foreach (var line in File.ReadLines(infoPath, Encoding.UTF8))
            {
                var parts = line.Split([':', '：'], 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    continue;
                }

                var key = parts[0].Replace(" ", string.Empty, StringComparison.Ordinal);
                if (key is "新剧名" or "新剧名称" or "原剧名" or "原剧名称" &&
                    !string.IsNullOrWhiteSpace(parts[1]))
                {
                    titles.Add(parts[1].Trim());
                }
            }
        }

        return titles;
    }

    private static IReadOnlyList<string> ParseTitles(string text)
    {
        return (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeWorkspaceRoot(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new DirectoryNotFoundException("系统高光发布：工作目录不能为空。");
        }

        var root = Path.GetFullPath(workspacePath);
        if (string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "workflow", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root) ?? root;
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"系统高光发布：工作目录不存在：{root}");
        }

        return root;
    }

    private static string ResolveRecordWorkflowDir(string workspaceRoot, string title, string? profileId)
    {
        var normalizedProfile = SanitizeProfileId(profileId ?? string.Empty);
        var digest = Md5Hex($"{normalizedProfile}::{NormalizeTitleKey(title)}")[..8];
        var profilePrefix = normalizedProfile.Length == 0 ? string.Empty : $"{SafeSlug(normalizedProfile, "default")}-";
        var name = $"_系统高光发布_{profilePrefix}{SafeSlug(title, "title")}-{digest}";
        return Path.Combine(workspaceRoot, "workflow", name);
    }

    private static string NormalizeTitleKey(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", string.Empty).ToLowerInvariant();

    private static string NormalizePublishTargetMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "type" or "types" or "video_type" or "video_types" ? "type" : "count";
    }

    private static IReadOnlyList<string> NormalizeVideoTypes(IReadOnlyList<string>? values)
    {
        var requested = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);
        return VideoTypeOptions
            .Where(value => requested.Count == 0 || requested.Contains(value))
            .DefaultIfEmpty(VideoTypeOptions[0])
            .ToArray();
    }

    private static string SafeSlug(string text, string fallback)
    {
        var slug = Regex.Replace((text ?? string.Empty).Trim(), """[\\/:*?""<>|]+""", "-");
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = Regex.Replace(slug, "-+", "-").Trim('-', '.', '_');
        return (slug.Length == 0 ? fallback : slug)[..Math.Min(slug.Length == 0 ? fallback.Length : slug.Length, 40)];
    }

    private static string SanitizeShortTitle(string text)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"[，,!！\s]+", string.Empty);
        return cleaned.Length <= 15 ? cleaned : cleaned[..15];
    }

    private static string SanitizeProfileId(string value)
    {
        var text = Regex.Replace((value ?? string.Empty).Trim(), @"[^a-zA-Z0-9_-]+", "-").Trim('-', '_');
        return text;
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
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
}

public sealed record MaterialSystemHighlightBatchPublishOptions(
    string WorkspacePath,
    string TitlesText,
    string DefaultDescription,
    int PublishCount,
    string PublishTargetMode,
    IReadOnlyList<string> PublishVideoTypes,
    bool RegenerateAfterPublish,
    IReadOnlyList<string> RegenerateVideoTypes,
    string? AuthFilePath,
    string? BrowserProfileDir,
    string? AccountId,
    string? AccountDisplayName,
    bool AllowDuplicatePublish,
    bool StopOnProjectError = true);

public sealed record MaterialSystemHighlightProject(
    string ProjectKey,
    string Title,
    string ProjectDirectory,
    string ConfigPath,
    string? SourceProjectDirectory,
    int PublishCount);

public sealed record MaterialSystemHighlightBatchPublishResult(
    IReadOnlyList<MaterialSystemHighlightProject> Projects,
    int Succeeded,
    int Failed);
