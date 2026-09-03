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

public sealed class KuaishouPersonalPreparationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<KuaishouPersonalPreparationResult> PrepareAsync(
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(data.WorkflowDirectory);
        RestoreOriginalTitle(data.SourceDirectory, data.WorkflowDirectory);
        var poster = FindPoster(data.WorkflowDirectory);
        var horizontal = Path.Combine(data.WorkflowDirectory, "快手横屏封面.jpg");
        var vertical = Path.Combine(data.WorkflowDirectory, "快手竖屏海报.jpg");
        await EnsureCoverAsync(poster, horizontal, 1600, 1000, overwrite, cancellationToken);
        await EnsureCoverAsync(poster, vertical, 1400, 2000, overwrite, cancellationToken);

        var info = ParseInfo(Path.Combine(data.WorkflowDirectory, "短剧信息.txt"));
        var autoFill = BuildAutoFill(data, config, info);
        var autoFillPath = Path.Combine(data.WorkflowDirectory, "kuaishou-auto-fill.json");
        await WriteJsonAsync(autoFillPath, autoFill, cancellationToken);

        var preview = BuildPayloadPreview(data, config, autoFill, horizontal, vertical);
        var previewPath = Path.Combine(data.WorkflowDirectory, "kuaishou-payload-preview.json");
        await WriteJsonAsync(previewPath, preview, cancellationToken);

        return new(data.WorkflowDirectory, horizontal, vertical, autoFillPath, previewPath, data.VideoPaths.Count);
    }

    public async Task<IReadOnlyList<KuaishouPersonalPreparationIssue>> ValidateAsync(
        string workflowDirectory,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<KuaishouPersonalPreparationIssue>();
        var horizontal = Path.Combine(workflowDirectory, "快手横屏封面.jpg");
        var vertical = Path.Combine(workflowDirectory, "快手竖屏海报.jpg");
        await ValidateImageAsync(horizontal, 1600, 1000, "horizontal-cover", issues, cancellationToken);
        await ValidateImageAsync(vertical, 1400, 2000, "vertical-poster", issues, cancellationToken);

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
        KuaishouPersonalConfig config,
        IReadOnlyDictionary<string, string> info)
    {
        var tags = Split(config.TagLabels).Concat(data.Tags.Select(value => value.TrimStart('#')))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(3).ToArray();
        var plots = Split(config.PlotLabels).DefaultIfEmpty("都市生活").Take(3).ToArray();
        var actors = new JsonArray(data.Actors.Select(actor => (JsonNode)new JsonObject
        {
            ["name"] = actor.Name, ["gender"] = actor.Gender, ["role"] = actor.Role,
        }).ToArray());
        var characters = new JsonArray(data.Actors.Select(actor => (JsonNode)new JsonObject
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
            ["content_type"] = config.ContentType,
            ["comic_product_method"] = ProductionMethodValue(config.ProductionMethod),
            ["is_finished"] = config.Finished,
            ["full_scene_display"] = config.FullSceneDisplay,
            ["copyright_proof_type"] = First(config.CopyrightProofType, "1"),
            ["has_record_number"] = config.HasRecordNumber,
            ["production_form"] = config.ProductionForm,
            ["production_year"] = ParseInt(config.ProductionYear, DateTime.Now.Year),
            ["episode_average_duration_minutes"] = ParseInt(config.AverageEpisodeMinutes, 1),
            ["broadcast_info"] = new JsonObject
            {
                ["platform"] = config.BroadcastPlatform,
                ["channel"] = config.BroadcastChannel,
                ["time_mode"] = "today",
            },
            ["actor_info_list"] = actors,
            ["director_info_list"] = BuildPeople(config.Directors),
            ["screenwriter_info_list"] = BuildPeople(config.Screenwriters),
            ["production_org"] = First(config.ProductionOrganization, info.GetValueOrDefault("制作公司")),
            ["special_theme"] = false,
            ["sale_type"] = SaleTypeValue(config.SaleType),
            ["free_episode_count"] = config.FreeEpisodeCount,
            ["unlock_count"] = Math.Max(1, config.UnlockEpisodeCount),
            ["small_amount_unlock"] = false,
            ["series_price_yuan"] = ParseDouble(config.EpisodePrice, 1.0),
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
            ["product_method_label"] = config.ProductionMethod,
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
    private static int ProductionMethodValue(string value) => value.Contains("AIGC", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
    private static int SaleTypeValue(string value) => value.Contains("广告", StringComparison.Ordinal) ? 3 : value.Contains("单集", StringComparison.Ordinal) ? 2 : 0;
    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
