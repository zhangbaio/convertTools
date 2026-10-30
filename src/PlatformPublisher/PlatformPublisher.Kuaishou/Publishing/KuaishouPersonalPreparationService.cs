using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouPersonalPreparationResult(
    string WorkflowDirectory,
    string HorizontalCoverPath,
    string VerticalPosterPath,
    string AutoFillPath,
    string PayloadPreviewPath,
    int EpisodeCount);

public sealed record KuaishouPersonalPreparationIssue(string Code, string Message);

public sealed record KuaishouSettlementPrepareOptions(
    bool Download,
    bool Rewrite,
    bool Poster,
    bool ProjectImages,
    bool AutoFill,
    bool Force,
    string TemplateId,
    string TemplateRoot);

public sealed record KuaishouSettlementPrepareResult(
    IReadOnlyList<string> CompletedSteps,
    int EpisodeCount);

public readonly record struct KuaishouPrepareStepEvent(string StepKey, string Status, string Error);

public sealed class KuaishouPersonalPreparationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<KuaishouPersonalPreparationResult> PrepareAsync(
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var prepared = await KuaishouSettlementPrepareRunner.RunAsync(
            string.IsNullOrWhiteSpace(data.SourceDirectory) ? data.WorkflowDirectory : data.SourceDirectory,
            config,
            new KuaishouSettlementPrepareOptions(false, false, true, true, true, overwrite, "", ""),
            null,
            cancellationToken);
        return new(
            data.WorkflowDirectory,
            Path.Combine(data.WorkflowDirectory, "快手横屏封面.jpg"),
            Path.Combine(data.WorkflowDirectory, "快手竖屏海报.jpg"),
            Path.Combine(data.WorkflowDirectory, "kuaishou-auto-fill.json"),
            Path.Combine(data.WorkflowDirectory, "kuaishou-payload-preview.json"),
            prepared.EpisodeCount);
    }

    public async Task<IReadOnlyList<KuaishouPersonalPreparationIssue>> ValidateAsync(
        string workflowDirectory,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<KuaishouPersonalPreparationIssue>();
        var horizontal = Path.Combine(workflowDirectory, "快手横屏封面.jpg");
        var vertical = Path.Combine(workflowDirectory, "快手竖屏海报.jpg");
        if (!File.Exists(horizontal)) issues.Add(new("horizontal-cover", "缺少快手横屏封面.jpg。"));
        if (!File.Exists(vertical)) issues.Add(new("vertical-poster", "缺少快手竖屏海报.jpg。"));

        var images = Directory.Exists(workflowDirectory)
            ? Directory.EnumerateFiles(workflowDirectory, "工程图_*.png").ToArray()
            : [];
        if (images.Length != 4) issues.Add(new("project-images", $"工程图应为 4 张，实际 {images.Length} 张。"));

        var videosDir = Path.Combine(workflowDirectory, "videos");
        var videos = Directory.Exists(videosDir)
            ? Directory.EnumerateFiles(videosDir, "*.mp4").OrderBy(EpisodeNumber).ToArray()
            : [];
        var info = ParseInfo(Path.Combine(workflowDirectory, "短剧信息.txt"));
        var declared = ParseInt(info.GetValueOrDefault("集数"));
        if (videos.Length == 0) issues.Add(new("videos", "videos 目录没有剧集视频。"));
        if (declared != videos.Length) issues.Add(new("episode-count", $"短剧信息为 {declared} 集，videos 实际 {videos.Length} 集。"));
        if (!videos.Select(EpisodeNumber).SequenceEqual(Enumerable.Range(1, videos.Length)))
            issues.Add(new("episode-order", "剧集文件名未按第1集开始连续编号。"));

        ValidateJson(Path.Combine(workflowDirectory, "kuaishou-auto-fill.json"),
            ["description", "audience_gender", "plot_list", "tag_list", "actor_info_list", "production_org", "sale_type"], issues);
        ValidateJson(Path.Combine(workflowDirectory, "kuaishou-payload-preview.json"),
            ["name", "episode_count", "cover_img_key", "poster_img_key", "episodes", "material_file_key_list"], issues);
        return issues;
    }

    private static JsonObject BuildAutoFill(
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config)
    {
        var tags = Split(config.TagLabels).Concat(data.Tags.Select(value => value.TrimStart('#')))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(3).ToArray();
        var plots = Split(config.PlotLabels).DefaultIfEmpty("都市生活").Take(3).ToArray();
        var form = KuaishouPersonalFormResolver.Resolve(config);
        var characters = new JsonArray(form.Actors.Select(actor => (JsonNode)new JsonObject
        {
            ["name"] = actor.Role, ["gender"] = actor.Gender,
        }).ToArray());
        return new JsonObject
        {
            ["generated_at"] = DateTimeOffset.Now.ToString("s"),
            ["material_type"] = 4,
            ["description"] = data.Intro,
            ["audience_gender"] = config.AudienceGender.Contains('女') ? 2 : 1,
            ["audience_gender_label"] = config.AudienceGender,
            ["plot_list"] = new JsonArray(plots.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["plot_label_list"] = new JsonArray(plots.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["plot_catalog_version"] = 2,
            ["tag_catalog_version"] = 2,
            ["people_rule_version"] = 3,
            ["character_info_list"] = characters,
            ["tag_list"] = new JsonArray(tags.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["tag_label_list"] = new JsonArray(tags.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["content_type"] = form.ContentTypeLabel,
            ["comic_product_method"] = form.ComicMethodLabel switch
            {
                "简笔动画" => 1,
                "小说剧" => 2,
                "沙雕动画" => 4,
                "3D动画" => 5,
                "2D动画" => 6,
                _ => 3,
            },
            ["is_finished"] = form.Finished,
            ["full_scene_display"] = form.FullSceneDisplay,
            ["copyright_proof_type"] = form.CopyrightProofTypeLabel,
            ["has_record_number"] = form.HasRecordNumber,
            ["production_form"] = form.ProductionForm,
            ["production_year"] = form.ProductionYear,
            ["episode_average_duration_minutes"] = form.AverageDurationMinutes,
            ["broadcast_info"] = new JsonObject
            {
                ["platform"] = form.BroadcastPlatform,
                ["channel"] = form.BroadcastChannel,
                ["time_mode"] = string.IsNullOrWhiteSpace(config.BroadcastTimeMode) ? "today" : config.BroadcastTimeMode,
                ["date"] = form.BroadcastDate,
            },
            ["actor_info_list"] = new JsonArray(form.Actors.Select(actor => (JsonNode)new JsonObject
            {
                ["name"] = actor.Name, ["gender"] = actor.Gender, ["role"] = actor.Role,
            }).ToArray()),
            ["director_info_list"] = new JsonArray(new JsonObject { ["name"] = form.CreditName, ["gender"] = form.Gender }),
            ["screenwriter_info_list"] = new JsonArray(new JsonObject { ["name"] = form.CreditName, ["gender"] = form.Gender }),
            ["production_org"] = form.ProductionOrg,
            ["special_theme"] = form.SpecialTheme,
            ["sale_type"] = form.SaleTypeCode,
            ["free_episode_count"] = form.FreeEpisodeCount,
            ["unlock_count"] = form.UnlockCount,
            ["small_amount_unlock"] = form.SmallAmountUnlock,
            ["series_price_yuan"] = ParseDouble(form.EpisodePrice, 0.11),
        };
    }

    private static JsonObject BuildPayloadPreview(
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        JsonObject autoFill,
        string horizontal,
        string vertical)
    {
        var episodes = new JsonArray();
        for (var index = 0; index < data.VideoPaths.Count; index++)
            episodes.Add(new JsonObject
            {
                ["cover_img_key"] = vertical,
                ["episode_title"] = data.Title,
                ["free"] = index < Math.Min(config.FreeEpisodeCount, Math.Max(0, data.VideoPaths.Count - 1)),
            });
        var projectImages = new JsonArray(data.ProjectImagePaths.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var now = DateTimeOffset.Now;
        var displayDate = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        var directors = autoFill["director_info_list"]?.AsArray();
        var screenwriters = autoFill["screenwriter_info_list"]?.AsArray();
        var actors = autoFill["actor_info_list"]?.AsArray();
        var copyrightRows = new JsonArray(new JsonObject
        {
            ["material_type"] = 4,
            ["file_key_list"] = projectImages.DeepClone(),
        });
        return new JsonObject
        {
            ["advertiser_id"] = 0,
            ["mini_series_title"] = data.Title,
            ["name"] = data.Title,
            ["title"] = data.Title,
            ["cover_img_key"] = horizontal,
            ["cover_material_id"] = horizontal,
            ["poster_img_key"] = vertical,
            ["poster_material_id"] = vertical,
            ["poster_image_key"] = vertical,
            ["description"] = autoFill["description"]?.DeepClone(),
            ["short_drama_introduction"] = autoFill["description"]?.DeepClone(),
            ["is_finished"] = autoFill["is_finished"]?.DeepClone(),
            ["update_end"] = autoFill["is_finished"]?.DeepClone(),
            ["episode_count"] = data.VideoPaths.Count,
            ["production_date"] = displayDate,
            ["sale_type"] = autoFill["sale_type"]?.DeepClone(),
            ["production_year"] = autoFill["production_year"]?.DeepClone(),
            ["audience_gender"] = autoFill["audience_gender"]?.DeepClone(),
            ["audience_gender_label"] = autoFill["audience_gender_label"]?.DeepClone(),
            ["plot_list"] = autoFill["plot_list"]?.DeepClone(),
            ["plot_label_list"] = autoFill["plot_label_list"]?.DeepClone(),
            ["plot_catalog_version"] = autoFill["plot_catalog_version"]?.DeepClone(),
            ["tag_catalog_version"] = autoFill["tag_catalog_version"]?.DeepClone(),
            ["mini_series_tag_id_list"] = autoFill["tag_list"]?.DeepClone(),
            ["tag_list"] = autoFill["tag_list"]?.DeepClone(),
            ["tag_label_list"] = autoFill["tag_label_list"]?.DeepClone(),
            ["content_type"] = autoFill["content_type"]?.DeepClone(),
            ["product_method"] = autoFill["comic_product_method"]?.DeepClone(),
            ["product_method_label"] = KuaishouFormChoices.LabelOf(KuaishouFormChoices.ComicMethods, config.ProductionMethod, "3"),
            ["sync_profile"] = false,
            ["has_copyright_proof"] = config.HasCopyrightProof,
            ["copyright_proof_type"] = ParseInt(config.CopyrightProofType, 1),
            ["has_filing_no"] = config.HasRecordNumber,
            ["production_form"] = autoFill["production_form"]?.DeepClone(),
            ["episode_average_duration_minutes"] = autoFill["episode_average_duration_minutes"]?.DeepClone(),
            ["broadcast_info"] = autoFill["broadcast_info"]?.DeepClone(),
            ["actor_info_list"] = autoFill["actor_info_list"]?.DeepClone(),
            ["director_info_list"] = autoFill["director_info_list"]?.DeepClone(),
            ["screenwriter_info_list"] = autoFill["screenwriter_info_list"]?.DeepClone(),
            ["production_org"] = autoFill["production_org"]?.DeepClone(),
            ["material_type"] = 4,
            ["material_file_key_list"] = projectImages,
            ["commitment_key_list"] = string.IsNullOrWhiteSpace(data.CommitmentPdfPath)
                ? new JsonArray()
                : new JsonArray(JsonValue.Create(data.CommitmentPdfPath)),
            ["copyright_proof_valid_start_time"] = ParseDate(config.CopyrightValidStartTime, now).ToUnixTimeMilliseconds(),
            ["copyright_proof_valid_end_time"] = ParseDate(config.CopyrightValidEndTime, now.AddYears(10)).ToUnixTimeMilliseconds(),
            ["has_sub_authorization_right"] = config.HasSubAuthorizationRight,
            ["copyright_proof_material_info_list"] = copyrightRows,
            ["series_content_type"] = 2,
            ["display_info_list"] = new JsonArray(new JsonObject
            {
                ["display_platform"] = config.BroadcastPlatform,
                ["display_channel"] = config.BroadcastChannel,
                ["display_date"] = displayDate,
            }),
            ["display_date"] = displayDate,
            ["episodes"] = episodes,
            ["unlock_count"] = autoFill["unlock_count"]?.DeepClone(),
            ["small_amount_unlock"] = autoFill["small_amount_unlock"]?.DeepClone(),
            ["revolution_history_special_theme"] = false,
            ["special_theme"] = false,
            ["episode_avg_time"] = ParseInt(config.AverageEpisodeMinutes, 1),
            ["production_cost"] = ParseDouble(config.ProductionCost, 1),
            ["director_list"] = new JsonArray((directors ?? []).Select(item => item?["name"]?.DeepClone()).ToArray()),
            ["screenwriter_list"] = new JsonArray((screenwriters ?? []).Select(item => item?["name"]?.DeepClone()).ToArray()),
            ["main_actor_list"] = new JsonArray((actors ?? []).Select(item => (JsonNode?)new JsonObject
            {
                ["actor_name"] = item?["name"]?.DeepClone(),
                ["gender"] = item?["gender"]?.DeepClone(),
                ["actor_role"] = item?["role"]?.DeepClone(),
            }).ToArray()),
            ["producer_info"] = screenwriters?.FirstOrDefault()?.DeepClone(),
            ["producer"] = screenwriters?.FirstOrDefault()?["name"]?.DeepClone(),
            ["author_declaration"] = AuthorDeclarationValue(config.AuthorDeclaration),
        };
    }

    private static bool TryRebuildWithAssistant(string workflowDirectory, KuaishouPersonalConfig config, out KuaishouPersonalPreparationResult result)
    {
        result = null!;
        var script = ResolveBridgeScript();
        var toolRoot = ResolveAssistantRoot();
        var python = ResolveAssistantPython();
        if (script is null || toolRoot is null || python is null) return false;
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = toolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--workflow");
        start.ArgumentList.Add(workflowDirectory);
        start.ArgumentList.Add("--tool-root");
        start.ArgumentList.Add(toolRoot);
        var workspace = ResolveWorkspace(workflowDirectory);
        if (workspace is not null)
        {
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(workspace);
        }
        var overlay = Path.Combine(Path.GetTempPath(), "ks-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(overlay, System.Text.Json.JsonSerializer.Serialize(
            KuaishouPersonalFormResolver.SettingsOverlay(config, config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue)));
        start.ArgumentList.Add("--overrides");
        start.ArgumentList.Add(overlay);
        using var process = Process.Start(start);
        if (process is null) return false;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("短剧助手快手填报生成超时。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "短剧助手快手填报生成失败。" : stderr.Trim());
        try { File.Delete(overlay); } catch { /* 临时覆盖文件删除失败不影响结果。 */ }
        var preview = Path.Combine(workflowDirectory, "kuaishou-payload-preview.json");
        var autoFill = Path.Combine(workflowDirectory, "kuaishou-auto-fill.json");
        var episodeCount = 0;
        var line = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(line))
        {
            try { episodeCount = JsonNode.Parse(line)?["episode_count"]?.GetValue<int>() ?? 0; }
            catch (JsonException) { }
        }
        result = new(
            workflowDirectory,
            Path.Combine(workflowDirectory, "快手横屏封面.jpg"),
            Path.Combine(workflowDirectory, "快手竖屏海报.jpg"),
            autoFill,
            preview,
            episodeCount);
        return File.Exists(preview);
    }

    private static string? ResolveBridgeScript()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tools", "kuaishou", "kuaishou_prepare_bridge.py");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }

    private static string? ResolveAssistantRoot()
    {
        var configured = Environment.GetEnvironmentVariable("WEIXIN_CHANNEL_TOOL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "modules", "kuaishou", "upload_service.py")))
            return configured;
        const string local = @"D:\code\weixin-channel-tool";
        return File.Exists(Path.Combine(local, "modules", "kuaishou", "upload_service.py")) ? local : null;
    }

    private static string? ResolveWorkspace(string workflowDirectory)
    {
        var workflow = new DirectoryInfo(workflowDirectory);
        if (workflow.Parent?.Name.Equals("workflow", StringComparison.OrdinalIgnoreCase) == true)
            return workflow.Parent.Parent?.FullName;
        return workflow.Parent?.FullName;
    }

    private static string? ResolveAssistantPython()
    {
        var venv = @"D:\code\weixin-channel-tool\.venv\Scripts\python.exe";
        if (File.Exists(venv)) return venv;
        return "python";
    }

    private static void RestoreOriginalTitle(string sourceDirectory, string workflowDirectory)
    {
        var metadataPath = new[]
        {
            Path.Combine(sourceDirectory, "shortdrama-project.json"),
            Path.Combine(workflowDirectory, "shortdrama-project.json"),
        }.FirstOrDefault(File.Exists);
        if (metadataPath is null) return;
        try
        {
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject();
            var originalTitle = First(metadata?["originalTitle"]?.ToString(), metadata?["title"]?.ToString());
            var infoPath = Path.Combine(workflowDirectory, "短剧信息.txt");
            if (string.IsNullOrWhiteSpace(originalTitle) || !File.Exists(infoPath)) return;
            var lines = File.ReadAllLines(infoPath);
            for (var index = 0; index < lines.Length; index++)
                if (Regex.IsMatch(lines[index], @"^原剧名\s*[：:]"))
                {
                    lines[index] = $"原剧名：{originalTitle}";
                    File.WriteAllLines(infoPath, lines);
                    return;
                }
        }
        catch (JsonException) { }
    }

    private static async Task EnsureCoverAsync(string source, string target, int width, int height, bool overwrite, CancellationToken ct)
    {
        if (!overwrite && File.Exists(target)) return;
        if (!File.Exists(source)) throw new FileNotFoundException("未找到海报图片，无法生成快手封面。", source);
        using var image = await Image.LoadAsync(source, ct);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(width, height), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Center,
        }));
        await image.SaveAsJpegAsync(target, new JpegEncoder { Quality = 92 }, ct);
    }

    private static async Task ValidateImageAsync(string path, int width, int height, string code,
        ICollection<KuaishouPersonalPreparationIssue> issues, CancellationToken ct)
    {
        if (!File.Exists(path)) { issues.Add(new(code, $"缺少 {Path.GetFileName(path)}。")); return; }
        try
        {
            var info = await Image.IdentifyAsync(path, ct);
            if (info.Width != width || info.Height != height)
                issues.Add(new(code, $"{Path.GetFileName(path)} 应为 {width}x{height}，实际 {info.Width}x{info.Height}。"));
        }
        catch (Exception ex) { issues.Add(new(code, $"图片不可读：{ex.Message}")); }
    }

    private static void ValidateJson(string path, string[] fields, ICollection<KuaishouPersonalPreparationIssue> issues)
    {
        if (!File.Exists(path)) { issues.Add(new("json-missing", $"缺少 {Path.GetFileName(path)}。")); return; }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            foreach (var field in fields)
                if (root?[field] is null) issues.Add(new("json-field", $"{Path.GetFileName(path)} 缺少字段 {field}。"));
        }
        catch (Exception ex) { issues.Add(new("json-invalid", $"{Path.GetFileName(path)} 无效：{ex.Message}")); }
    }

    private static async Task WriteJsonAsync(string path, JsonObject value, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value.ToJsonString(JsonOptions), ct);
        File.Move(temporary, path, true);
    }

    private static string FindPoster(string workflow) =>
        new[] { "海报图片.png", "海报图片.jpg", "海报图片.jpeg" }
            .Select(name => Path.Combine(workflow, name)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("工作目录缺少海报图片。");

    private static Dictionary<string, string> ParseInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    private static JsonArray BuildPeople(string value) => new(Split(value).Select(item =>
    {
        var parts = item.Split([':', '：'], StringSplitOptions.TrimEntries);
        return (JsonNode)new JsonObject { ["name"] = parts[0], ["gender"] = parts.ElementAtOrDefault(1) ?? "男" };
    }).ToArray());
    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([',', '，', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static int EpisodeNumber(string path) => ParseInt(Regex.Match(Path.GetFileNameWithoutExtension(path), @"第\s*(\d+)\s*集").Groups[1].Value);
    private static int ParseInt(string? value, int fallback = 0) => int.TryParse(Regex.Match(value ?? string.Empty, @"\d+").Value, out var result) ? result : fallback;
    private static double ParseDouble(string? value, double fallback) => double.TryParse(Regex.Match(value ?? string.Empty, @"\d+(?:\.\d+)?").Value, out var result) ? result : fallback;
    private static DateTimeOffset ParseDate(string? value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(value, out var result) ? result : fallback;
    private static int AuthorDeclarationValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains('无') ? 0 : int.TryParse(value, out var result) ? result : 1;
    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
