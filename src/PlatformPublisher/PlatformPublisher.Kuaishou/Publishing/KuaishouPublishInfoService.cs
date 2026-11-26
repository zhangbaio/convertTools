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
    public const string OriginalSynopsisKey = "原简介";

    /// <summary>
    /// 写入时「简介」会被改写结果覆盖，原文另存为「原简介」。重新生成时必须拿原文比对，
    /// 否则 AI 拿到的是已经改好的简介，原样返回就会一直被判为照抄。
    /// </summary>
    public static string SourceSynopsis(IReadOnlyDictionary<string, string> info) =>
        First(info, OriginalSynopsisKey, "简介", "剧情简介", "剧情", "介绍");

    private static Dictionary<string, string> WithOriginalSynopsis(Dictionary<string, string> updates, string sourceSynopsis)
    {
        if (sourceSynopsis.Length > 0)
            updates[OriginalSynopsisKey] = sourceSynopsis;
        return updates;
    }

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
            throw new InvalidOperationException("请在快手发布配置的 AI 文本中填写模型和 API Key，或使用本机默认。");

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
        var sourceSynopsis = SourceSynopsis(info);
        if (IsMissingSynopsis(sourceSynopsis))
            throw new InvalidOperationException("缺少有效的原始简介，不能只凭剧名生成快手发布信息。");

        var existingTitle = First(info, "新剧名");
        var compliance = BuildCompliance(config);
        string? retry = null;
        JsonElement? accepted = null;
        IReadOnlyList<string> issues = [];
        IReadOnlyList<string> policyHits = [];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(attempt == 1
                ? "快手发布信息：一次请求生成剧名、简介、标签、观众、分类和情节"
                : $"快手发布信息：第 {attempt} 次重试，{retry}");
            var payload = await RequestAsync(ai, BuildUserPrompt(
                originalTitle, sourceSynopsis, First(info, "集数"), rewrite, existingTitle, compliance, retry), cancellationToken);
            issues = Validate(payload, config, originalTitle, sourceSynopsis, rewrite, autoFill, existingTitle);
            var hits = PolicyHits(payload, config, rewrite);
            if (issues.Count == 0)
            {
                accepted = payload;
                policyHits = hits;
                if (hits.Count == 0)
                    break;
            }
            retry = string.Join("；", issues.Concat(hits));
        }
        if (accepted is null)
            throw new InvalidOperationException("快手发布信息不合格：" + string.Join("；", issues));
        if (policyHits.Count > 0)
            progress?.Report("快手发布信息仍" + string.Join("；", policyHits) + "，已照常写入，请人工确认");

        var draft = ReadDraft(accepted.Value);
        if (rewrite)
        {
            WriteInfo(infoPath, WithOriginalSynopsis(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["新剧名"] = draft.Title,
                ["短标题"] = draft.ShortTitle,
                ["简介"] = draft.Description,
                ["标签"] = string.Join(' ', draft.Tags),
            }, sourceSynopsis), ["推荐语"]);
            progress?.Report($"快手发布信息已写入：新剧名={draft.Title}，短标题={draft.ShortTitle}");
        }
        else if (autoFill)
        {
            WriteInfo(infoPath, WithOriginalSynopsis(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["简介"] = draft.Description,
            }, sourceSynopsis), []);
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

    public static IReadOnlyList<string> Validate(
        JsonElement payload,
        KuaishouPersonalConfig config,
        string originalTitle,
        string sourceSynopsis,
        bool rewrite,
        bool autoFill,
        string existingTitle)
    {
        var issues = new List<string>();
        var title = Text(payload, "title", trim: false);
        var shortTitle = Text(payload, "short_title", trim: false);
        var description = Collapse(Text(payload, "description"));
        if (rewrite)
        {
            if (!IsHanTitle(title, TitleMinLength, TitleMaxLength))
                issues.Add($"新剧名必须是 {TitleMinLength}-{TitleMaxLength} 个汉字，当前「{title}」");
            else if (SameText(title, originalTitle))
                issues.Add("新剧名不能与原剧名相同");
            if (!IsHanTitle(shortTitle, ShortTitleMinLength, ShortTitleMaxLength))
                issues.Add($"短标题必须是 {ShortTitleMinLength}-{ShortTitleMaxLength} 个汉字，当前「{shortTitle}」");
            else if (SameText(shortTitle, title) || SameText(shortTitle, originalTitle))
                issues.Add("短标题不能与新剧名或原剧名相同");
            if (!TryReadOutputStrings(payload, "tags", out var tags) || tags.Count != 3)
                issues.Add("标签必须是包含 3 个非空字符串的数组");
            else if (tags.Any(tag => tag.Contains('#') || tag.Contains('＃')))
                issues.Add("标签不能带 # 或 ＃");
            else if (tags.Any(tag => tag.Trim().Any(char.IsWhiteSpace) || tag.IndexOfAny([',', '，', '、', ';', '；', '|']) >= 0))
                issues.Add("每项只填写一个短标签，不能包含空格或分隔符");
            else if (tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Count)
                issues.Add("3 个标签不能重复");
        }
        else if (existingTitle.Length > 0 && title != existingTitle)
        {
            issues.Add("这次只补齐字段，新剧名必须保持「" + existingTitle + "」");
        }

        if (description.Length < SynopsisMinLength || description.Length > SynopsisMaxLength)
            issues.Add($"简介必须是 {SynopsisMinLength}-{SynopsisMaxLength} 个字，当前 {description.Length} 字");
        else if (description[^1] != '。')
            issues.Add("简介必须以句号结束");
        else if (HasRepeatedContent(description))
            issues.Add("简介存在明显重复");
        else if (SameText(description, sourceSynopsis))
            issues.Add("简介不能照抄原文");

        if (autoFill)
        {
            if (!KuaishouFormCatalog.Audience.Any(option => option.Label == Text(payload, "audience", trim: false)))
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
        var seconds = Math.Clamp(ai.TimeoutSeconds, 10, 600);
        var body = JsonSerializer.Serialize(new
        {
            model = ai.Model.Trim(),
            temperature = 0.4,
            thinking = new { type = "disabled" },
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
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var response = await Http.SendAsync(request, timeout.Token);
            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"快手发布信息请求失败：HTTP {(int)response.StatusCode} {Trim(raw)}");
            using var document = JsonDocument.Parse(raw);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return ParseContent(content);
        }
        catch (OperationCanceledException ex)
        {
            var failure = RequestFailure(ex, cancellationToken, seconds);
            if (ReferenceEquals(failure, ex))
                throw;
            throw failure;
        }
    }

    public static Exception RequestFailure(OperationCanceledException exception, CancellationToken callerToken, int timeoutSeconds)
    {
        if (callerToken.IsCancellationRequested)
            return exception;
        var seconds = Math.Clamp(timeoutSeconds, 10, 600);
        return new InvalidOperationException($"快手发布信息请求超时（{seconds} 秒）", exception);
    }

    private static string BuildUserPrompt(
        string originalTitle,
        string synopsis,
        string episodeCount,
        bool rewrite,
        string existingTitle,
        JsonObject compliance,
        string? retry)
    {
        var payload = new JsonObject
        {
            ["compliance"] = compliance.DeepClone(),
            ["original_title"] = originalTitle,
            ["synopsis"] = synopsis,
            ["episode_count"] = episodeCount,
            ["title_max_length"] = TitleMaxLength,
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
        if (!TryReadOutputStrings(payload, name, out var values) || values.Count < min || values.Count > max)
            return [$"{label}必须是包含 {min}-{max} 个非空字符串的数组"];
        var unknown = values.Where(value => !options.Any(option => option.Label == value)).ToArray();
        if (unknown.Length > 0)
            return [$"{label}不在选项中：{string.Join('、', unknown)}"];
        return values.Distinct(StringComparer.Ordinal).Count() == values.Count ? [] : [$"{label}不能重复"];
    }

    private static IReadOnlyList<KuaishouFormOption> MatchAll(
        JsonElement payload, string name, IReadOnlyList<KuaishouFormOption> options) =>
        Texts(payload, name)
            .Select(value => KuaishouFormCatalog.TryMatch(options, value, out var option) ? option : null)
            .Where(option => option is not null)
            .Select(option => option!)
            .DistinctBy(option => option.Value)
            .ToArray();

    public static JsonObject BuildCompliance(KuaishouPersonalConfig config)
    {
        var combinations = new JsonArray();
        foreach (var (category, groups) in SynopsisCombinations(config))
        {
            var groupJson = new JsonObject();
            foreach (var (group, terms) in groups)
                groupJson[group] = new JsonArray(terms.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray());
            combinations.Add(new JsonObject { ["category"] = category, ["groups"] = groupJson });
        }
        return new JsonObject
        {
            ["rule"] = "title、short_title、tags 不得出现 title_forbidden 中的任何词；description 不得出现 synopsis_forbidden 中的任何词；" +
                       "词语作为更长词语的一部分出现也算违规。description 不得同时出现 synopsis_forbidden_combinations 同一条规则里每个概念组的说法。" +
                       "涉及相关剧情时改用平实、中性的说法，不要用近义词规避。输出前逐条自查，命中就重写。",
            ["title_forbidden"] = CategoryJson(KuaishouContentPolicyText.EffectiveCategoryMap(
                config.DramaTitlePolicyJson, KuaishouContentPolicyDefaults.TitlePolicyJson, "categories", "disabled")),
            ["synopsis_forbidden"] = CategoryJson(KuaishouContentPolicyText.EffectiveCategoryMap(
                config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories", "disabled")),
            ["synopsis_forbidden_combinations"] = combinations,
        };
    }

    public static IReadOnlyList<string> PolicyHits(JsonElement payload, KuaishouPersonalConfig config, bool rewrite)
    {
        var hits = new List<string>();
        if (rewrite)
        {
            var titleText = string.Join('\n', Text(payload, "title"), Text(payload, "short_title"), string.Join(' ', Texts(payload, "tags")));
            hits.AddRange(Matches(titleText, KuaishouContentPolicyText.EffectiveTerms(
                config.DramaTitlePolicyJson, KuaishouContentPolicyDefaults.TitlePolicyJson, "categories", "disabled")));
        }
        var description = Collapse(Text(payload, "description"));
        hits.AddRange(Matches(description, KuaishouContentPolicyText.EffectiveTerms(
            config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories", "disabled")));
        var words = hits.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var combos = SynopsisCombinations(config)
            .Where(rule => rule.Groups.All(group => Matches(description, group.Terms).Any()))
            .Select(rule => rule.Category)
            .ToArray();
        var result = new List<string>();
        if (words.Length > 0)
            result.Add($"命中禁用词：{string.Join('、', words)}");
        if (combos.Length > 0)
            result.Add($"简介命中组合规则：{string.Join('、', combos)}");
        return result;
    }

    private static IReadOnlyList<(string Category, IReadOnlyList<(string Group, IReadOnlyList<string> Terms)> Groups)> SynopsisCombinations(
        KuaishouPersonalConfig config) =>
        KuaishouContentPolicyText.EffectiveCombinations(config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson);

    private static IEnumerable<string> Matches(string text, IEnumerable<string> terms) =>
        terms.Where(term => term.Length > 0 && text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static JsonObject CategoryJson(IReadOnlyDictionary<string, List<string>> categories)
    {
        var result = new JsonObject();
        foreach (var (category, terms) in categories)
            result[category] = new JsonArray(terms.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray());
        return result;
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

    private static string Text(JsonElement payload, string name, bool trim = true)
    {
        var text = payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        return trim ? text.Trim() : text;
    }

    private static bool TryReadOutputStrings(JsonElement payload, string name, out IReadOnlyList<string> values)
    {
        values = [];
        if (!payload.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<string>();
        foreach (var item in field.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return false;
            items.Add(item.GetString()!);
        }
        values = items;
        return true;
    }

    private static bool IsHanTitle(string title, int min, int max)
    {
        var count = 0;
        foreach (var rune in title.EnumerateRunes())
        {
            if (rune.Value is not (0x3007 or >= 0x3400 and <= 0x4DBF or
                >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
                >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x323AF))
                return false;
            count++;
        }
        return count >= min && count <= max;
    }

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
