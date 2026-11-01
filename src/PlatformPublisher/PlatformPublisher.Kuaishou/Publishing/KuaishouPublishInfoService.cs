using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouPublishInfoAiSettings(
    string Endpoint,
    string ApiKey,
    string Model,
    int TimeoutSeconds,
    string Prompt);

/// <summary>
/// 一次请求生成快手新剧名、短标题、简介、标签、观众、分类和情节，并写入短剧信息和自动填表文件。
/// </summary>
public sealed class KuaishouPublishInfoService
{
    public const int TitleMinLength = 2;
    public const int TitleMaxLength = 10;
    public const int ShortTitleMinLength = 6;
    public const int ShortTitleMaxLength = 16;
    public const int SynopsisMinLength = 100;
    public const int SynopsisMaxLength = 400;
    public const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HttpClient Http = new();

    public async Task PrepareAsync(
        string projectDirectory,
        KuaishouPersonalConfig config,
        KuaishouPublishInfoAiSettings ai,
        bool rewrite,
        bool autoFill,
        bool enterprise,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!rewrite && !autoFill)
            return;
        if (string.IsNullOrWhiteSpace(ai.Endpoint) || string.IsNullOrWhiteSpace(ai.ApiKey) || string.IsNullOrWhiteSpace(ai.Model))
            throw new InvalidOperationException("请先在系统设置的 AI文本 中填写接口地址、API Key 和模型名称。");

        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        var infoPath = Path.Combine(directories.WorkflowDirectory, "短剧信息.txt");
        if (!File.Exists(infoPath))
        {
            var sourceInfo = Path.Combine(directories.SourceDirectory, "短剧信息.txt");
            if (File.Exists(sourceInfo))
            {
                Directory.CreateDirectory(directories.WorkflowDirectory);
                File.Copy(sourceInfo, infoPath, overwrite: false);
            }
        }
        if (!File.Exists(infoPath))
            throw new InvalidOperationException("未找到短剧信息.txt，请先下载剧集或导入项目。");

        var info = ReadInfo(infoPath);
        var originalTitle = First(info, "原剧名", "原剧名称", "剧名");
        var sourceSynopsis = First(info, "简介", "剧情简介", "剧情", "介绍");
        if (IsMissingSynopsis(sourceSynopsis))
            throw new InvalidOperationException("缺少有效的原始简介，不能只凭剧名生成快手发布信息。");

        var budget = TitleBudget(config);
        if (rewrite && budget < TitleMinLength)
            throw new InvalidOperationException("快手新剧名别名过长，标题主体至少要留 2 个字。");

        var existingTitle = First(info, "新剧名");
        string? retry = null;
        JsonElement payload = default;
        IReadOnlyList<string> issues = [];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(attempt == 1
                ? "快手发布信息：一次请求生成剧名、简介、标签、观众、分类和情节"
                : $"快手发布信息：第 {attempt} 次重试，{retry}");
            payload = await RequestAsync(ai, BuildUserPrompt(
                originalTitle, sourceSynopsis, First(info, "集数"), budget, rewrite, existingTitle, retry), cancellationToken);
            issues = Validate(payload, config, originalTitle, sourceSynopsis, budget, rewrite, autoFill, existingTitle);
            if (issues.Count == 0)
                break;
            retry = string.Join("；", issues);
        }
        if (issues.Count > 0)
            throw new InvalidOperationException("快手发布信息不合格：" + string.Join("；", issues));

        var draft = ReadDraft(payload);
        if (rewrite)
        {
            var title = ApplyAlias(draft.Title, config);
            WriteInfo(infoPath, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["新剧名"] = title,
                ["短标题"] = draft.ShortTitle,
                ["简介"] = draft.Description,
                ["标签"] = string.Join(' ', draft.Tags),
            }, ["推荐语"]);
            progress?.Report($"快手发布信息已写入：新剧名={title}，短标题={draft.ShortTitle}");
        }
        else if (autoFill)
        {
            WriteInfo(infoPath, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["简介"] = draft.Description,
            }, []);
        }

        if (autoFill)
        {
            var autoFillPath = Path.Combine(
                directories.WorkflowDirectory,
                enterprise ? "kuaishou-enterprise-auto-fill.json" : "kuaishou-auto-fill.json");
            WriteAutoFill(autoFillPath, draft, config);
            progress?.Report($"快手字段已补齐：{Path.GetFileName(autoFillPath)}");
        }
    }

    public static int TitleBudget(KuaishouPersonalConfig config)
    {
        if (!config.NewTitleAliasEnabled)
            return TitleMaxLength;
        var alias = config.NewTitleAliasValue?.Trim() ?? "";
        return alias.Length == 0 ? TitleMaxLength : Math.Max(0, TitleMaxLength - alias.Length);
    }

    public static string ApplyAlias(string title, KuaishouPersonalConfig config)
    {
        if (!config.NewTitleAliasEnabled)
            return title;
        var alias = config.NewTitleAliasValue?.Trim() ?? "";
        if (alias.Length == 0)
            return title;
        return string.Equals(config.NewTitleAliasPosition?.Trim(), "prefix", StringComparison.OrdinalIgnoreCase)
            ? alias + title
            : title + alias;
    }

    public static IReadOnlyList<string> Validate(
        JsonElement payload,
        KuaishouPersonalConfig config,
        string originalTitle,
        string sourceSynopsis,
        int titleBudget,
        bool rewrite,
        bool autoFill,
        string existingTitle)
    {
        var issues = new List<string>();
        var title = Text(payload, "title");
        var shortTitle = Text(payload, "short_title");
        var description = Collapse(Text(payload, "description"));
        var tags = Texts(payload, "tags");
        if (rewrite)
        {
            if (title.Length < TitleMinLength || title.Length > titleBudget || title.Any(char.IsWhiteSpace))
                issues.Add($"新剧名必须是 {TitleMinLength}-{titleBudget} 个字且不含空格，当前「{title}」");
            else if (SameText(title, originalTitle))
                issues.Add("新剧名不能与原剧名相同");
            if (shortTitle.Length < ShortTitleMinLength || shortTitle.Length > ShortTitleMaxLength || shortTitle.Any(char.IsWhiteSpace))
                issues.Add($"短标题必须是 {ShortTitleMinLength}-{ShortTitleMaxLength} 个字且不含空格，当前「{shortTitle}」");
            else if (SameText(shortTitle, title) || SameText(shortTitle, originalTitle))
                issues.Add("短标题不能与新剧名或原剧名相同");
            if (tags.Count is < 3 or > 6 || tags.Any(tag => tag.Contains('#')))
                issues.Add("标签需要 3-6 个，且不带 #");
            issues.AddRange(PolicyHits(config, title, shortTitle, string.Join(' ', tags)));
        }
        else if (existingTitle.Length > 0 && title.Length > 0 && !SameText(title, existingTitle))
        {
            issues.Add("这次只补齐字段，新剧名必须保持「" + existingTitle + "」");
        }

        if (description.Length < SynopsisMinLength || description.Length > SynopsisMaxLength)
            issues.Add($"简介必须是 {SynopsisMinLength}-{SynopsisMaxLength} 个字，当前 {description.Length} 字");
        else if (description[^1] is not ('。' or '！' or '？' or '!' or '?'))
            issues.Add("简介必须以完整句子结束");
        else if (HasRepeatedContent(description))
            issues.Add("简介存在明显重复");
        else if (SameText(description, sourceSynopsis))
            issues.Add("简介不能照抄原文");
        issues.AddRange(PolicyHits(config, description));

        if (autoFill)
        {
            if (!KuaishouFormCatalog.TryMatch(KuaishouFormCatalog.Audience, AudienceText(Text(payload, "audience")), out _))
                issues.Add("目标观众必须是「男频」或「女频」");
            issues.AddRange(OptionIssues(payload, "categories", KuaishouFormCatalog.Categories, 1, 3, "短剧分类"));
            issues.AddRange(OptionIssues(payload, "plots", KuaishouFormCatalog.Plots, 1, 3, "情节"));
        }
        return issues;
    }

    public static string UpsertInfo(string existing, IReadOnlyDictionary<string, string> updates, IReadOnlyCollection<string> remove)
    {
        var lines = existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var key = InfoKey(lines[index]);
            if (key.Length == 0)
                continue;
            if (remove.Contains(key))
            {
                lines.RemoveAt(index);
                continue;
            }
            if (!updates.TryGetValue(key, out var value))
                continue;
            var separator = lines[index].Contains('：') ? "：" : ":";
            lines[index] = key + separator + value;
            seen.Add(key);
        }
        foreach (var pair in updates)
        {
            if (!seen.Contains(pair.Key))
                lines.Add(pair.Key + "：" + pair.Value);
        }
        return string.Join(Environment.NewLine, lines.Where((_, index) => index < lines.Count));
    }

    internal static void WriteInfo(string path, IReadOnlyDictionary<string, string> updates, IReadOnlyCollection<string> remove)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        File.WriteAllText(path, UpsertInfo(existing, updates, remove).TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteAutoFill(string path, Draft draft, KuaishouPersonalConfig config)
    {
        var form = KuaishouPersonalFormResolver.Resolve(config);
        var payload = new JsonObject
        {
            ["generated_at"] = DateTimeOffset.Now.ToString("s"),
            ["material_type"] = 4,
            ["description"] = draft.Description,
            ["audience_gender"] = draft.Audience.Value,
            ["audience_gender_label"] = draft.Audience.Label,
            ["plot_list"] = Ids(draft.Plots),
            ["plot_label_list"] = Labels(draft.Plots),
            ["plot_catalog_version"] = 2,
            ["tag_catalog_version"] = 2,
            ["people_rule_version"] = 3,
            ["tag_list"] = Ids(draft.Categories),
            ["tag_label_list"] = Labels(draft.Categories),
            ["content_type"] = form.ContentTypeLabel,
            ["comic_product_method"] = ComicMethodCode(form.ComicMethodLabel),
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
            ["actor_info_list"] = People(form.Actors.Select(actor => (actor.Name, actor.Gender, actor.Role))),
            ["director_info_list"] = People([(form.CreditName, form.Gender, "")]),
            ["screenwriter_info_list"] = People([(form.CreditName, form.Gender, "")]),
            ["production_org"] = form.ProductionOrg,
            ["special_theme"] = form.SpecialTheme,
            ["sale_type"] = form.SaleTypeCode,
            ["free_episode_count"] = form.FreeEpisodeCount,
            ["unlock_count"] = form.UnlockCount,
            ["small_amount_unlock"] = form.SmallAmountUnlock,
            ["series_price_yuan"] = ParseDouble(config.SeriesPrice, 1),
        };
        File.WriteAllText(path, payload.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }), new UTF8Encoding(false));
    }

    private static async Task<JsonElement> RequestAsync(
        KuaishouPublishInfoAiSettings ai,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = ai.Model.Trim(),
            temperature = 0.4,
            messages = new object[]
            {
                new { role = "system", content = ai.Prompt },
                new { role = "user", content = userPrompt },
            },
        }, PromptJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, ai.Endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ai.ApiKey.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(ai.TimeoutSeconds, 10, 600)));
        using var response = await Http.SendAsync(request, timeout.Token);
        var raw = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"快手发布信息请求失败：HTTP {(int)response.StatusCode} {Trim(raw)}");
        using var document = JsonDocument.Parse(raw);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return ParseContent(content);
    }

    private static string BuildUserPrompt(
        string originalTitle,
        string synopsis,
        string episodeCount,
        int titleBudget,
        bool rewrite,
        string existingTitle,
        string? retry)
    {
        var payload = new JsonObject
        {
            ["original_title"] = originalTitle,
            ["synopsis"] = synopsis,
            ["episode_count"] = episodeCount,
            ["title_max_length"] = titleBudget,
            ["keep_existing_title"] = rewrite ? "" : existingTitle,
            ["audience_options"] = new JsonArray(KuaishouFormCatalog.Audience.Select(item => JsonValue.Create(item.Label)).ToArray()),
            ["category_options"] = new JsonArray(KuaishouFormCatalog.Categories.Select(item => JsonValue.Create(item.Label)).ToArray()),
            ["plot_options"] = new JsonArray(KuaishouFormCatalog.Plots.Select(item => JsonValue.Create(item.Label)).ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(retry))
            payload["retry"] = new JsonObject { ["reason"] = retry };
        return payload.ToJsonString(PromptJsonOptions);
    }

    private static JsonElement ParseContent(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && last > firstLine)
                text = text[(firstLine + 1)..last].Trim();
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("快手发布信息没有返回 JSON。");
        using var document = JsonDocument.Parse(text[start..(end + 1)]);
        return document.RootElement.Clone();
    }

    private static Draft ReadDraft(JsonElement payload)
    {
        KuaishouFormCatalog.TryMatch(KuaishouFormCatalog.Audience, AudienceText(Text(payload, "audience")), out var audience);
        return new Draft(
            Text(payload, "title"),
            Text(payload, "short_title"),
            Collapse(Text(payload, "description")),
            Texts(payload, "tags"),
            audience ?? new KuaishouFormOption("男频", 1),
            MatchAll(payload, "categories", KuaishouFormCatalog.Categories),
            MatchAll(payload, "plots", KuaishouFormCatalog.Plots));
    }

    private static IReadOnlyList<string> OptionIssues(
        JsonElement payload, string name, IReadOnlyList<KuaishouFormOption> options, int min, int max, string label)
    {
        var values = Texts(payload, name);
        if (values.Count < min || values.Count > max)
            return [$"{label}需要 {min}-{max} 个"];
        var unknown = values.Where(value => !KuaishouFormCatalog.TryMatch(options, value, out _)).ToArray();
        return unknown.Length == 0 ? [] : [$"{label}不在选项中：{string.Join('、', unknown)}"];
    }

    private static IReadOnlyList<KuaishouFormOption> MatchAll(
        JsonElement payload, string name, IReadOnlyList<KuaishouFormOption> options) =>
        Texts(payload, name)
            .Select(value => KuaishouFormCatalog.TryMatch(options, value, out var option) ? option : null)
            .Where(option => option is not null)
            .Select(option => option!)
            .DistinctBy(option => option.Value)
            .ToArray();

    private static IEnumerable<string> PolicyHits(KuaishouPersonalConfig config, params string[] texts)
    {
        var terms = KuaishouContentPolicyText.EffectiveTerms(
                config.DramaTitlePolicyJson, KuaishouContentPolicyDefaults.TitlePolicyJson, "categories", "disabled")
            .Concat(KuaishouContentPolicyText.EffectiveTerms(
                config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories", "disabled"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var combined = string.Join('\n', texts);
        var hits = terms.Where(term => term.Length > 0 && combined.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(8).ToArray();
        return hits.Length == 0 ? [] : [$"命中禁用词：{string.Join('、', hits)}"];
    }

    private static bool HasRepeatedContent(string value)
    {
        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        var sentences = Regex.Split(compact, "[。！？!?；;]+").Where(part => part.Length >= 8).ToArray();
        return sentences.Length != sentences.Distinct(StringComparer.Ordinal).Count() ||
               Regex.IsMatch(compact, "(.{8,30})\\1");
    }

    private static bool IsMissingSynopsis(string value)
    {
        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        return compact.Length == 0 || compact.EndsWith("，待补充简介。", StringComparison.Ordinal);
    }

    private static bool SameText(string left, string right) =>
        string.Concat((left ?? "").Where(character => !char.IsWhiteSpace(character)))
            .Equals(string.Concat((right ?? "").Where(character => !char.IsWhiteSpace(character))), StringComparison.OrdinalIgnoreCase);

    private static string AudienceText(string value) => value is "男" or "女" ? value + "频" : value;

    private static string Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static IReadOnlyList<string> Texts(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return Split(value.GetString());
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ComicMethodCode(string label) => label switch
    {
        "简笔动画" => 1,
        "小说剧" => 2,
        "沙雕动画" => 4,
        "3D动画" => 5,
        "2D动画" => 6,
        _ => 3,
    };

    private static JsonArray Ids(IReadOnlyList<KuaishouFormOption> options) =>
        new(options.Select(option => (JsonNode?)JsonValue.Create(option.Value)).ToArray());

    private static JsonArray Labels(IReadOnlyList<KuaishouFormOption> options) =>
        new(options.Select(option => (JsonNode?)JsonValue.Create(option.Label)).ToArray());

    private static JsonArray People(IEnumerable<(string Name, string Gender, string Role)> people)
    {
        var array = new JsonArray();
        foreach (var person in people)
        {
            var item = new JsonObject { ["name"] = person.Name, ["gender"] = person.Gender };
            if (person.Role.Length > 0)
                item["role"] = person.Role;
            array.Add(item);
        }
        return array;
    }

    private static Dictionary<string, string> ReadInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var key = InfoKey(line);
            if (key.Length > 0 && !result.ContainsKey(key))
                result[key] = InfoValue(line);
        }
        return result;
    }

    private static string InfoKey(string line)
    {
        var index = line.IndexOfAny([':', '：']);
        return index > 0 ? line[..index].Trim() : "";
    }

    private static string InfoValue(string line)
    {
        var index = line.IndexOfAny([':', '：']);
        return index >= 0 ? line[(index + 1)..].Trim() : "";
    }

    private static string First(IReadOnlyDictionary<string, string> info, params string[] keys) =>
        keys.Select(key => info.TryGetValue(key, out var value) ? value.Trim() : "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static IReadOnlyList<string> Split(string? value) =>
        (value ?? "").Split([' ', '，', ',', '、', '|', '；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Draft(
        string Title,
        string ShortTitle,
        string Description,
        IReadOnlyList<string> Tags,
        KuaishouFormOption Audience,
        IReadOnlyList<KuaishouFormOption> Categories,
        IReadOnlyList<KuaishouFormOption> Plots);
}
