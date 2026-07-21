using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Core.Services;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Config;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.AI;

public sealed class ProjectInfoRewriter : IProjectInfoRewriter
{
    private static readonly string[] WeakMarketingSuffixes =
    [
        "热播版",
        "新篇",
        "新作",
        "完整版",
        "逆袭版",
        "高能版",
        "爆款版",
        "热播",
        "新版"
    ];

    private static readonly string[] UnsafeTitleTerms =
    [
        "拜金",
        "炫富",
        "豪门阔太",
        "富婆包养",
        "陪睡",
        "出轨成瘾",
        "乱伦",
        "禁忌之恋",
        "玩弄儿媳",
        "折磨孕妻",
        "活埋",
        "灭门",
        "血洗",
        "弄死",
        "整死",
        "杀疯",
        "复仇到底",
        "陪葬",
        "撕烂",
        "扒光",
        "发骚",
        "发情",
        "小三上位",
        "往死里整",
        "报复到底",
        "生不如死",
        "血债血偿"
    ];

    private static readonly string[] UnsafeTaglineTerms =
    [
        "往死里",
        "弄死",
        "整死",
        "血债血偿",
        "活埋",
        "灭门",
        "杀疯",
        "往死里整",
        "报复到底",
        "生不如死",
        "陪睡",
        "上床",
        "出轨成瘾",
        "金钱至上",
        "拜金",
        "炫富",
        "低俗"
    ];

    private static readonly string[] OverlyColloquialTerms =
    [
        "太炸裂",
        "太上头",
        "笑不活了",
        "离大谱",
        "逆天改命爽麻了",
        "家人们谁懂",
        "我直接看傻了",
        "气炸了",
        "赢麻了"
    ];

    private const string DefaultAiTextBatchPrompt = """
请根据下面的多个短剧项目信息，逐个生成：
1. title：适合短剧传播的新剧名，6-15 个字，要有宣发钩子感，必须与原剧名明显不同。
   起名要求：
   - 保留核心：意思要和原名及核心剧情强相关，不能跑题。
   - 极致吸睛：优先使用强冲突、强情绪的词汇，制造悬念或强期待感。
   - 优先使用或参考：诱饵、背叛、前夫、替身、沦陷、绝地、复仇、逆袭、打脸、改命、反杀、追妻、豪门、千金、离婚、重生、认亲、守寡、逼婚、抢婚、回头等表达。
   - 生成方向优先：事件化、冲突化、关系反转化、身份转变、命运逆转、情绪拉扯。
   标题风格参考：
   - “新春烟花暖，不及我心寒” -> “妈妈，这次换我为你过年”
   - “恶邻赠暖5年骂我傻” -> “赠暖五年我封墙断供”
   - “非凡青瑶” -> “烧火丫头的逆袭”
   不要只做同义替换，不要只多加一两个字，不要保留与原标题几乎相同的结构。
2. tagline：推荐语，8-20 个字。
3. synopsis：简介，40-90 个字。

输出要求：
1. 只输出 JSON。
2. 当前一次只改写一个项目，请返回单个 JSON 对象，不要返回 items 数组。
3. JSON 格式固定为：
{"title":"...","tagline":"...","synopsis":"..."}
4. 新剧名禁止以下偷懒改法：原剧名原样返回；原剧名 + “热播版/新篇/完整版/逆袭版/高能版/爆款版”；只替换一两个字但整体几乎不变；保留原标题主要词序仅在首尾微调。
5. 不要生成 short_title/短标题 或 tags/标签 字段。
6. 输入中的 forbidden_titles 是历史已用或禁用的新剧名，新的 title 不得与其中任意一项相同或近似。
7. 输入中的 forbidden_synopses 是历史已用或原始简介，新的 synopsis 不得照抄或高度近似。

输入项目：
{items_json}
""";
    private const string DefaultAiTextRetryPrompt = """

上一次结果不合格，请重新生成，并严格遵守：
1. 新剧名必须和原剧名明显不同，不能只是加“热播版/新篇/新作”等尾巴。
2. 新剧名要像全新包装标题，优先体现冲突、关系、身份、命运转折。
3. 不要生成 short_title/短标题 或 tags/标签 字段。
4. 上次不合格的新剧名：{previous_bad_title}
5. 必须继续避开输入中的 forbidden_titles 和 forbidden_synopses。
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int MaxAiAttempts = 2;
    private const double SynopsisSimilarityThreshold = 0.86;

    private readonly IProjectInfoParser _projectInfoParser;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectInfoRewriter> _logger;

    public ProjectInfoRewriter(
        IProjectInfoParser projectInfoParser,
        HttpClient httpClient,
        ILogger<ProjectInfoRewriter> logger)
    {
        _projectInfoParser = projectInfoParser;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ProjectInfoRewriteResult> RewriteAsync(
        ProjectInfoRewriteRequest request,
        CancellationToken cancellationToken)
    {
        var project = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var canonicalOriginalTitle = ResolveCanonicalOriginalTitle(project);
        var config = KeyValueConfigReader.Read(request.ConfigFile);

        var endpoint = GetPreferred(config, "AiTextEndpoint", "ChatModelEndpoint").TrimEnd('/');
        var modelId = GetPreferred(config, "AiTextModel", "ChatModelId");
        var apiKey = GetPreferred(config, "AiTextApiKey", "ChatModelApiKey");
        var systemPrompt = GetOptional(config, "AiTextSystemPrompt") ?? string.Empty;
        var batchPrompt = GetOptional(config, "AiTextBatchPrompt") ?? DefaultAiTextBatchPrompt;
        var retryPrompt = GetOptional(config, "AiTextRetryPrompt") ?? DefaultAiTextRetryPrompt;
        var timeoutSeconds = GetOptionalInt(config, "AiTextTimeoutSeconds") ?? 120;
        var rewriteSynopsis = GetOptionalBool(config, "AiRewriteSynopsis") ?? false;

        if (File.Exists(request.OutputFilePath) && !request.Overwrite)
        {
            throw new InvalidOperationException($"输出文件已存在: {request.OutputFilePath}");
        }

        var outputDirectory = Path.GetDirectoryName(request.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var normalized = await RewriteWithRetryAsync(
            endpoint,
            modelId,
            apiKey,
            systemPrompt,
            batchPrompt,
            retryPrompt,
            timeoutSeconds,
            project,
            canonicalOriginalTitle,
            request,
            rewriteSynopsis,
            cancellationToken);

        await File.WriteAllTextAsync(
            request.OutputFilePath,
            BuildOutputText(project with { OriginalTitle = canonicalOriginalTitle }, normalized.Title, normalized.Tagline, normalized.Synopsis),
            Encoding.UTF8,
            cancellationToken);

        _logger.LogInformation("Rewrote project info to {Path}", request.OutputFilePath);
        return new ProjectInfoRewriteResult(request.OutputFilePath, normalized.Title, normalized.Tagline, normalized.Synopsis, string.Empty, string.Empty);
    }

    private static string BuildPrompt(
        ProjectInfo project,
        string batchPrompt,
        ProjectInfoRewriteRequest request,
        bool rewriteSynopsis)
    {
        var canonicalOriginalTitle = ResolveCanonicalOriginalTitle(project);
        var forbiddenTitles = UniqueTexts(request.ForbiddenTitles);
        var forbiddenSynopses = UniqueTexts(request.ForbiddenSynopses);
        var projectName = FirstNonEmpty(Path.GetFileName(project.ProjectDir), project.Title, canonicalOriginalTitle) ?? canonicalOriginalTitle;
        projectName = projectName.TrimStart('_');
        var existingTitle = FirstNonEmpty(project.Title, canonicalOriginalTitle, projectName) ?? canonicalOriginalTitle;
        var synopsis = FirstNonEmpty(project.Synopsis, $"{projectName}，待补充简介。") ?? string.Empty;
        var itemsJson = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    id = "1",
                    project_name = projectName,
                    title = canonicalOriginalTitle,
                    original_title = canonicalOriginalTitle,
                    new_title = existingTitle,
                    tagline = project.Tagline ?? string.Empty,
                    existing_tagline = project.Tagline ?? string.Empty,
                    synopsis,
                    rewrite_synopsis = rewriteSynopsis,
                    forbidden_titles = forbiddenTitles,
                    forbidden_synopses = rewriteSynopsis ? forbiddenSynopses : Array.Empty<string>(),
                    target_synopsis_length = rewriteSynopsis ? Math.Max(0, request.TargetSynopsisLength) : 0,
                    rewrite_variant_key = request.RewriteVariantKey ?? string.Empty,
                    episode_count = project.EpisodeCount,
                    duration_minutes = project.TotalMinutes,
                    total_minutes = project.TotalMinutes
                }
            }
        });

        var template = string.IsNullOrWhiteSpace(batchPrompt) ? DefaultAiTextBatchPrompt : batchPrompt;
        if (!template.Contains("{items_json}", StringComparison.Ordinal))
        {
            template += "\n\n输入项目：\n{items_json}";
        }

        template += rewriteSynopsis
            ? """

当前流程不再使用 short_title/短标题 和 tags/标签。请不要生成这两个字段。
最终 JSON 只需要 title/new_title、tagline、synopsis；如果返回 items 数组，每个 item 也只需要这些字段。
"""
            : """

当前发布配置关闭了 AI 改写简介。请不要生成 synopsis/description/简介 字段，简介将由系统保留原文。
当前流程不再使用 short_title/短标题 和 tags/标签。请不要生成这两个字段。
最终 JSON 只需要 title/new_title 和 tagline；如果返回 items 数组，每个 item 也只需要这些字段。
""";

        return template.Replace("{items_json}", itemsJson, StringComparison.Ordinal);
    }

    private static string BuildOutputText(ProjectInfo project, string title, string tagline, string synopsis)
    {
        var originalTitleLine = string.IsNullOrWhiteSpace(project.OriginalTitle)
            ? string.Empty
            : $"原剧名: {project.OriginalTitle}\n";

        return
            $"{originalTitleLine}" +
            $"新剧名: {title}\n" +
            $"推荐语: {tagline}\n" +
            $"简介: {synopsis}\n" +
            $"集数: {project.EpisodeCount}\n" +
            $"制作公司: {project.CompanyName}\n";
    }

    private async Task<NormalizedRewrite> RewriteWithRetryAsync(
        string endpoint,
        string modelId,
        string apiKey,
        string systemPrompt,
        string batchPrompt,
        string retryPromptTemplate,
        int timeoutSeconds,
        ProjectInfo project,
        string canonicalOriginalTitle,
        ProjectInfoRewriteRequest request,
        bool rewriteSynopsis,
        CancellationToken cancellationToken)
    {
        string? previousBadTitle = null;
        string? lastFailureMessage = null;
        NormalizedRewrite? lastNormalized = null;

        for (var attempt = 1; attempt <= MaxAiAttempts; attempt++)
        {
            var prompt = attempt == 1
                ? BuildPrompt(project, batchPrompt, request, rewriteSynopsis)
                : BuildRetryPrompt(project, batchPrompt, retryPromptTemplate, request, previousBadTitle, rewriteSynopsis);

            var rewrite = await RequestRewritePayloadAsync(
                endpoint,
                modelId,
                apiKey,
                systemPrompt,
                prompt,
                timeoutSeconds,
                cancellationToken);

            NormalizedRewrite normalized;
            try
            {
                normalized = NormalizeRewrite(project, canonicalOriginalTitle, rewrite, rewriteSynopsis);
            }
            catch (InvalidOperationException ex)
            {
                previousBadTitle = FirstNonEmpty(rewrite.Title, rewrite.NewTitle, rewrite.New_Title, rewrite.新剧名, rewrite.剧名);
                lastFailureMessage = ex.Message;
                continue;
            }

            var qualityIssues = GetRewriteQualityIssues(canonicalOriginalTitle, normalized, request, rewriteSynopsis);
            if (qualityIssues.Count == 0)
            {
                return normalized;
            }

            lastNormalized = normalized;
            previousBadTitle = normalized.Title;
            lastFailureMessage = string.Join("；", qualityIssues);
        }

        var fallback = TryBuildRecentTitleFallback(project, canonicalOriginalTitle, request, lastNormalized, previousBadTitle, rewriteSynopsis);
        if (fallback is not null)
        {
            _logger.LogWarning(
                "AI rewrite did not pass all validation after {Attempts} attempts, using recent generated title fallback. Last error: {Error}; title: {Title}",
                MaxAiAttempts,
                lastFailureMessage ?? "未知错误",
                fallback.Title);
            return fallback;
        }

        throw new InvalidOperationException(
            $"改写结果生成失败，已重试 {MaxAiAttempts} 次仍未通过。最近一次错误：{lastFailureMessage ?? "未知错误"}；最近一次新剧名：{previousBadTitle ?? "无"}");
    }

    private async Task<RewritePayload> RequestRewritePayloadAsync(
        string endpoint,
        string modelId,
        string apiKey,
        string systemPrompt,
        string prompt,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new
            {
                role = "system",
                content = systemPrompt.Trim()
            });
        }

        messages.Add(new
        {
            role = "user",
            content = prompt
        });

        var payload = new
        {
            model = modelId,
            temperature = 0.75,
            messages
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 600)));

        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AiApiErrorMessage.Create("AI 改写接口", response.StatusCode, response.ReasonPhrase, responseText));
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("无法解析改写接口响应。");

        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("改写接口未返回内容。");
        }

        try
        {
            var jsonText = ExtractJsonValue(content);
            var payloadText = ExtractRewritePayloadJson(jsonText);
            return JsonSerializer.Deserialize<RewritePayload>(payloadText, JsonOptions)
                ?? throw new InvalidOperationException("无法解析改写结果 JSON。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"无法解析改写结果 JSON：{ex.Message}; AI原始返回内容:\n{FormatRawContentForLog(content)}",
                ex);
        }
    }

    private static NormalizedRewrite NormalizeRewrite(
        ProjectInfo project,
        string canonicalOriginalTitle,
        RewritePayload rewrite,
        bool rewriteSynopsis)
    {
        var rawTitle = FirstNonEmpty(rewrite.Title, rewrite.NewTitle, rewrite.New_Title, rewrite.新剧名, rewrite.剧名)
            ?? throw new InvalidOperationException("改写结果缺少新剧名。");
        var title = NormalizeRewrittenTitle(rawTitle);
        var rawTagline = FirstNonEmpty(rewrite.Tagline, rewrite.Recommendation, rewrite.推荐语)
            ?? throw new InvalidOperationException("改写结果缺少推荐语。");
        var tagline = NormalizeGeneratedText(project, canonicalOriginalTitle, title, rawTitle, rawTagline);
        if (string.IsNullOrWhiteSpace(tagline))
        {
            throw new InvalidOperationException("改写结果缺少推荐语。");
        }

        var synopsis = PreserveSourceSynopsis(project);
        if (rewriteSynopsis)
        {
            var rawSynopsis = FirstNonEmpty(rewrite.Synopsis, rewrite.Description, rewrite.简介)
                ?? throw new InvalidOperationException("改写结果缺少简介。");
            synopsis = NormalizeGeneratedText(project, canonicalOriginalTitle, title, rawTitle, rawSynopsis);
            if (string.IsNullOrWhiteSpace(synopsis))
            {
                throw new InvalidOperationException("改写结果缺少简介。");
            }
        }

        return new NormalizedRewrite(title, tagline, synopsis, string.Empty, string.Empty);
    }

    private static string BuildRetryPrompt(
        ProjectInfo project,
        string batchPrompt,
        string retryPromptTemplate,
        ProjectInfoRewriteRequest request,
        string? previousBadTitle,
        bool rewriteSynopsis)
    {
        var retryTemplate = (string.IsNullOrWhiteSpace(retryPromptTemplate) ? DefaultAiTextRetryPrompt : retryPromptTemplate)
            .Replace("{previous_bad_title}", previousBadTitle ?? "无", StringComparison.Ordinal)
            .Replace("{previous_bad_short_title}", "无", StringComparison.Ordinal);
        return BuildPrompt(project, retryTemplate, request, rewriteSynopsis);
    }

    private static IReadOnlyList<string> GetRewriteQualityIssues(
        string canonicalOriginalTitle,
        NormalizedRewrite rewrite,
        ProjectInfoRewriteRequest request,
        bool rewriteSynopsis)
    {
        var issues = new List<string>();
        if (TitlesEqual(rewrite.Title, canonicalOriginalTitle) ||
            TitlesTooSimilar(rewrite.Title, canonicalOriginalTitle))
        {
            issues.Add("新剧名与原剧名相同或过于相似");
        }
        else if (IsLazyRetitle(rewrite.Title, canonicalOriginalTitle))
        {
            issues.Add("新剧名疑似只是原标题加宣传后缀");
        }

        if (!IsReasonableTitleLength(rewrite.Title))
        {
            issues.Add("新剧名字数不在 6-15 字范围内");
        }

        var titleSafetyIssue = ValidateTitleSafety(rewrite.Title);
        if (!string.IsNullOrWhiteSpace(titleSafetyIssue))
        {
            issues.Add($"新剧名{titleSafetyIssue}");
        }

        if (HasForbiddenTitle(rewrite.Title, request.ForbiddenTitles))
        {
            issues.Add("新剧名与历史/禁用标题重复或过于相似");
        }

        if (!IsReasonableTaglineLength(rewrite.Tagline))
        {
            issues.Add("推荐语字数不在 8-20 字范围内");
        }

        var taglineSafetyIssue = ValidateTaglineSafety(rewrite.Tagline);
        if (!string.IsNullOrWhiteSpace(taglineSafetyIssue))
        {
            issues.Add($"推荐语{taglineSafetyIssue}");
        }

        if (rewriteSynopsis)
        {
            issues.AddRange(GetSynopsisIssues(rewrite.Synopsis, request));

            if (HasForbiddenSynopsis(rewrite.Synopsis, request.ForbiddenSynopses))
            {
                issues.Add("简介与历史/原简介过于相似");
            }
        }

        return issues;
    }

    private static NormalizedRewrite? TryBuildRecentTitleFallback(
        ProjectInfo project,
        string canonicalOriginalTitle,
        ProjectInfoRewriteRequest request,
        NormalizedRewrite? lastNormalized,
        string? previousBadTitle,
        bool rewriteSynopsis)
    {
        var title = FirstNonEmpty(lastNormalized?.Title, previousBadTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = NormalizeRewrittenTitle(title);
        if (!IsFallbackTitleUsable(title, canonicalOriginalTitle, request))
        {
            return null;
        }

        var synopsis = rewriteSynopsis
            ? BuildFallbackSynopsis(project, canonicalOriginalTitle, title, request, lastNormalized?.Synopsis)
            : PreserveSourceSynopsis(project);
        var tagline = BuildFallbackTagline(project, canonicalOriginalTitle, title, synopsis, lastNormalized?.Tagline);

        return new NormalizedRewrite(title, tagline, synopsis, string.Empty, string.Empty);
    }

    private static bool IsFallbackTitleUsable(
        string title,
        string canonicalOriginalTitle,
        ProjectInfoRewriteRequest request)
    {
        if (TitlesEqual(title, canonicalOriginalTitle) ||
            TitlesTooSimilar(title, canonicalOriginalTitle) ||
            IsLazyRetitle(title, canonicalOriginalTitle) ||
            !IsReasonableTitleLength(title) ||
            !string.IsNullOrWhiteSpace(ValidateTitleSafety(title)) ||
            HasForbiddenTitle(title, request.ForbiddenTitles))
        {
            return false;
        }

        return true;
    }

    private static string PreserveSourceSynopsis(ProjectInfo project) =>
        string.Join(' ', (project.Synopsis ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()))
            .Trim();

    private static string BuildFallbackSynopsis(
        ProjectInfo project,
        string canonicalOriginalTitle,
        string title,
        ProjectInfoRewriteRequest request,
        string? latestSynopsis)
    {
        var synopsis = FirstNonEmpty(latestSynopsis, project.Synopsis);
        if (!string.IsNullOrWhiteSpace(synopsis))
        {
            synopsis = NormalizeGeneratedText(project, canonicalOriginalTitle, title, title, synopsis);
            if (GetSynopsisIssues(synopsis, request).Count == 0 &&
                !HasForbiddenSynopsis(synopsis, request.ForbiddenSynopses))
            {
                return synopsis;
            }
        }

        var fallback = $"主角被迫跌入低谷，却在误解和危机中抓住转机，凭借智慧与韧劲一步步翻盘，揭开真相后重掌人生主动权。";
        if (request.TargetSynopsisLength > 0 && fallback.Length < Math.Max(40, (int)Math.Floor(request.TargetSynopsisLength * 0.75d)))
        {
            fallback += "故事节奏紧凑，情绪反转不断，适合短剧平台连续追看。";
        }

        return fallback;
    }

    private static string BuildFallbackTagline(
        ProjectInfo project,
        string canonicalOriginalTitle,
        string title,
        string synopsis,
        string? latestTagline)
    {
        var tagline = FirstNonEmpty(latestTagline, project.Tagline);
        if (!string.IsNullOrWhiteSpace(tagline))
        {
            tagline = NormalizeGeneratedText(project, canonicalOriginalTitle, title, title, tagline);
            if (IsReasonableTaglineLength(tagline) &&
                string.IsNullOrWhiteSpace(ValidateTaglineSafety(tagline)))
            {
                return tagline;
            }
        }

        foreach (var candidate in new[]
                 {
                     "逆境翻盘高能来袭",
                     "命运反转爽感拉满",
                     "低谷逆袭一路开挂",
                     "真相揭开强势反击"
                 })
        {
            if (!ContainsTitle(candidate, title) && !ContainsTitle(candidate, canonicalOriginalTitle))
            {
                return candidate;
            }
        }

        return "逆境翻盘高能来袭";
    }

    private static string GetPreferred(IReadOnlyDictionary<string, string> config, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"配置缺少必填字段: {string.Join(" / ", keys)}");
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> config, string key)
    {
        return config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int? GetOptionalInt(IReadOnlyDictionary<string, string> config, string key)
    {
        return config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static bool? GetOptionalBool(IReadOnlyDictionary<string, string> config, string key)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return text is "1" or "是" or "启用" or "yes" or "YES" or "on" or "ON";
    }

    private static string ExtractJsonValue(string value)
    {
        foreach (var candidate in BuildJsonCandidateStrings(value))
        {
            if (TryParseFirstJsonValue(candidate, out var jsonText))
            {
                return jsonText;
            }
        }

        throw new InvalidOperationException("AI 返回内容不包含合法 JSON");
    }

    private static IEnumerable<string> BuildJsonCandidateStrings(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        yield return trimmed;

        foreach (Match match in Regex.Matches(trimmed, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase))
        {
            var fenced = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(fenced))
            {
                yield return fenced;
            }
        }

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        var starts = new[] { objectStart, arrayStart }.Where(static index => index >= 0).ToArray();
        if (starts.Length > 0)
        {
            yield return trimmed[starts.Min()..];
        }
    }

    private static bool TryParseFirstJsonValue(string candidate, out string jsonText)
    {
        jsonText = string.Empty;
        var trimmed = candidate.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(trimmed);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        try
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return false;
            }

            jsonText = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatRawContentForLog(string value, int maxLength = 4000)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}\n...[truncated {text.Length - maxLength} chars]";
    }

    private static string ExtractRewritePayloadJson(string jsonText)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array &&
            items.GetArrayLength() > 0)
        {
            return items[0].GetRawText();
        }

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            return root[0].GetRawText();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("改写结果 JSON 必须是对象，或包含 items 数组。");
        }

        return root.GetRawText();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeRewrittenTitle(string value)
    {
        var title = value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("改写结果缺少新剧名。");
        }

        return title;
    }

    private static string NormalizeGeneratedText(ProjectInfo project, string canonicalOriginalTitle, string title, string rawTitle, string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        foreach (var candidate in new[]
                 {
                     canonicalOriginalTitle,
                     title,
                     rawTitle,
                     project.Title,
                     project.OriginalTitle
                 }.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal))
        {
            normalized = RemoveTitleMentions(normalized, candidate!);
        }

        foreach (var candidate in new[]
                 {
                     canonicalOriginalTitle,
                     title,
                     rawTitle,
                     project.Title,
                     project.OriginalTitle
                 }.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal))
        {
            var trimmed = candidate!.Trim();
            var quotedTitle = $"《{trimmed}》";
            while (normalized.Contains(quotedTitle + quotedTitle, StringComparison.Ordinal))
            {
                normalized = normalized.Replace(quotedTitle + quotedTitle, quotedTitle, StringComparison.Ordinal);
            }

            while (normalized.Contains(trimmed + trimmed, StringComparison.Ordinal))
            {
                normalized = normalized.Replace(trimmed + trimmed, trimmed, StringComparison.Ordinal);
            }
        }

        return normalized;
    }

    private static string RemoveTitleMentions(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
        {
            return text.Trim();
        }

        var normalized = text;
        var bareTitle = title.Trim();
        var quotedTitle = $"《{bareTitle}》";

        normalized = normalized.Replace(quotedTitle, string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace(bareTitle, string.Empty, StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, @"^[\s《》:：,\-—]+", string.Empty);
        normalized = Regex.Replace(normalized, @"[《》]+", string.Empty);
        normalized = Regex.Replace(normalized, @"\s{2,}", " ");
        normalized = normalized.Replace("，，", "，", StringComparison.Ordinal)
            .Replace("。。", "。", StringComparison.Ordinal)
            .Replace("！！", "！", StringComparison.Ordinal)
            .Replace("、、", "、", StringComparison.Ordinal)
            .Trim();

        return normalized;
    }

    private static bool ContainsTitle(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var bareTitle = title.Trim();
        var quotedTitle = $"《{bareTitle}》";
        return text.Contains(quotedTitle, StringComparison.Ordinal) ||
               text.Contains(bareTitle, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> UniqueTexts(IEnumerable<string>? values)
    {
        if (values is null) return [];

        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var key = NormalizeTitle(text);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!seen.Add(key)) continue;

            output.Add(text);
        }

        return output;
    }

    private static bool HasForbiddenTitle(string title, IEnumerable<string>? forbiddenTitles)
    {
        foreach (var forbidden in UniqueTexts(forbiddenTitles))
        {
            if (TitlesEqual(title, forbidden) || TitlesTooSimilar(title, forbidden))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasForbiddenSynopsis(string synopsis, IEnumerable<string>? forbiddenSynopses)
    {
        foreach (var forbidden in UniqueTexts(forbiddenSynopses))
        {
            if (SynopsesTooSimilar(synopsis, forbidden))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TitlesEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return NormalizeTitle(left) == NormalizeTitle(right);
    }

    private static bool TitlesTooSimilar(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeTitle(left);
        var normalizedRight = NormalizeTitle(right);
        if (normalizedLeft == normalizedRight)
        {
            return true;
        }

        var commonPrefixLength = GetCommonPrefixLength(normalizedLeft, normalizedRight);
        var minLength = Math.Min(normalizedLeft.Length, normalizedRight.Length);
        if (minLength >= 5 &&
            commonPrefixLength >= 5 &&
            commonPrefixLength >= (int)Math.Floor(minLength * 0.7))
        {
            return true;
        }

        return normalizedLeft.StartsWith(normalizedRight, StringComparison.Ordinal) ||
               normalizedRight.StartsWith(normalizedLeft, StringComparison.Ordinal);
    }

    private static bool IsLazyRetitle(string? candidate, string? original)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        var normalizedCandidate = NormalizeTitle(candidate);
        var normalizedOriginal = NormalizeTitle(original);
        if (normalizedCandidate.Length == 0 || normalizedOriginal.Length == 0)
        {
            return false;
        }

        foreach (var suffix in WeakMarketingSuffixes)
        {
            if (!normalizedCandidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var stripped = normalizedCandidate[..^suffix.Length];
            if (stripped == normalizedOriginal ||
                stripped.StartsWith(normalizedOriginal, StringComparison.Ordinal) ||
                normalizedOriginal.StartsWith(stripped, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetCommonPrefixLength(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < max && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '《' or '》' or '“' or '”' or '"' or '\'' or '-' or '_' or '：' or ':' or '，' or ',' or '。' or '.')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsReasonableTitleLength(string value)
    {
        var length = NormalizeTitle(value).Length;
        return length is >= 6 and <= 15;
    }

    private static bool IsReasonableTaglineLength(string value)
    {
        var length = (value ?? string.Empty).Trim().Length;
        return length is >= 8 and <= 20;
    }

    private static IReadOnlyList<string> GetSynopsisIssues(string synopsis, ProjectInfoRewriteRequest request)
    {
        var text = (synopsis ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ["缺少简介"];
        }

        var target = Math.Max(0, request.TargetSynopsisLength);
        var length = text.Length;
        if (target > 0)
        {
            var lower = Math.Max(40, (int)Math.Floor(target * 0.75d));
            var upper = Math.Min(220, Math.Max(lower, (int)Math.Floor(target * 1.25d) + 5));
            if (length < lower || length > upper)
            {
                return [$"简介字数与原简介差距过大，目标约 {target} 字"];
            }

            return [];
        }

        return length is >= 40 and <= 220
            ? []
            : ["简介字数不在 40-220 字范围内"];
    }

    private static string ValidateTitleSafety(string value)
    {
        var normalized = NormalizeTitle(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (UnsafeTitleTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
        {
            return "包含违背伦理、炫富拜金、极端复仇或低俗表达";
        }

        return OverlyColloquialTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal))
            ? "过度口语化，不适合正式宣发"
            : string.Empty;
    }

    private static string ValidateTaglineSafety(string value)
    {
        var normalized = NormalizeTitle(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (UnsafeTaglineTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
        {
            return "包含不良价值导向或极端表达";
        }

        return OverlyColloquialTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal))
            ? "过度口语化，不适合正式宣发"
            : string.Empty;
    }

    private static bool SynopsesTooSimilar(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeSynopsis(left);
        var normalizedRight = NormalizeSynopsis(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return false;
        }

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
        {
            return true;
        }

        if (Math.Min(normalizedLeft.Length, normalizedRight.Length) < 24)
        {
            return false;
        }

        var lcs = LongestCommonSubsequenceLength(normalizedLeft, normalizedRight);
        var ratio = (double)lcs / Math.Max(normalizedLeft.Length, normalizedRight.Length);
        return ratio >= SynopsisSimilarityThreshold;
    }

    private static string NormalizeSynopsis(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch)) continue;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int LongestCommonSubsequenceLength(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (right.Length > left.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = left[i - 1] == right[j - 1]
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[right.Length];
    }

    private static string BuildDistinctFallbackTitle(string originalTitle)
    {
        var normalized = NormalizeTitle(originalTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "逆天改命风云录";
        }

        if (normalized.Contains("救", StringComparison.Ordinal) &&
            (normalized.Contains("爹", StringComparison.Ordinal) || normalized.Contains("父", StringComparison.Ordinal)))
        {
            return "顽娃逆天救父记";
        }

        if (normalized.Contains("找爸爸", StringComparison.Ordinal) ||
            normalized.Contains("寻父", StringComparison.Ordinal) ||
            (normalized.Contains("萌娃", StringComparison.Ordinal) &&
             (normalized.Contains("爸", StringComparison.Ordinal) || normalized.Contains("父", StringComparison.Ordinal))))
        {
            return "风雪夜萌娃千里寻父";
        }

        if (normalized.Contains("摄政", StringComparison.Ordinal) ||
            normalized.Contains("深宫", StringComparison.Ordinal) ||
            normalized.Contains("皇宫", StringComparison.Ordinal))
        {
            return "逃宫摄政妃破局录";
        }

        if (normalized.Contains("重生", StringComparison.Ordinal))
        {
            return "重生归来我改写命运";
        }

        if (normalized.Contains("离婚", StringComparison.Ordinal) || normalized.Contains("前夫", StringComparison.Ordinal))
        {
            return "离婚后我反杀全场";
        }

        if (normalized.Contains("婆", StringComparison.Ordinal) || normalized.Contains("媳", StringComparison.Ordinal))
        {
            return "恶婆家局我亲手掀桌";
        }

        string[] transformedPrefixes =
        [
            "逆天",
            "绝地",
            "深宫",
            "前夫",
            "豪门",
            "改命"
        ];

        string[] transformedSuffixes =
        {
            "风云录",
            "逆袭记",
            "情劫录",
            "破局录",
            "反杀局"
        };

        foreach (var prefix in transformedPrefixes)
        {
            var candidate = ProjectInfoTextNormalizer.SanitizeShortTitle(prefix + normalized, 15);
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !TitlesTooSimilar(candidate, originalTitle) &&
                !IsLazyRetitle(candidate, originalTitle))
            {
                return candidate;
            }
        }

        foreach (var suffix in transformedSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var maxBaseLength = Math.Max(2, 15 - suffix.Length);
            var baseTitle = normalized.Length > maxBaseLength
                ? normalized[^maxBaseLength..]
                : normalized;
            var candidate = baseTitle + suffix;
            if (!TitlesTooSimilar(candidate, originalTitle) &&
                !IsLazyRetitle(candidate, originalTitle))
            {
                return candidate;
            }
        }

        return "命运反转风云录";
    }

    private static string ResolveCanonicalOriginalTitle(ProjectInfo project)
    {
        if (!string.IsNullOrWhiteSpace(project.ProjectDir))
        {
            var sourceTitle = TryResolveSourceTitle(project.ProjectDir, project);
            if (!string.IsNullOrWhiteSpace(sourceTitle))
            {
                return sourceTitle!;
            }
        }

        return NormalizeTitle(project.OriginalTitle) is { Length: > 0 } normalizedOriginal
            ? normalizedOriginal
            : NormalizeTitle(project.Title);
    }

    private static string? TryResolveSourceTitle(string projectDir, ProjectInfo project)
    {
        var workflowDir = new DirectoryInfo(projectDir);
        if (workflowDir.Parent is null ||
            !string.Equals(workflowDir.Parent.Name, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootDir = workflowDir.Parent.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            return null;
        }

        var workflowName = workflowDir.Name.TrimStart('_');
        foreach (var candidate in Directory.EnumerateDirectories(rootDir, "*", SearchOption.TopDirectoryOnly))
        {
            var metadataPath = Path.Combine(candidate, "shortdrama-project.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var metadata = ProjectAutomationMetadata.Resolve(candidate);
            var possibleSource = FirstNonEmpty(metadata.SourceName, metadata.ProjectKey, metadata.Title);
            if (string.IsNullOrWhiteSpace(possibleSource))
            {
                continue;
            }

            var normalizedSource = NormalizeTitle(possibleSource);
            if (string.IsNullOrWhiteSpace(normalizedSource))
            {
                continue;
            }

            if (workflowName.StartsWith(normalizedSource, StringComparison.Ordinal) ||
                NormalizeTitle(project.Title).StartsWith(normalizedSource, StringComparison.Ordinal) ||
                NormalizeTitle(project.OriginalTitle).StartsWith(normalizedSource, StringComparison.Ordinal))
            {
                return normalizedSource;
            }
        }

        return null;
    }

    private sealed record ChatCompletionResponse(IReadOnlyList<Choice>? Choices);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content);

    private sealed record RewritePayload(
        string? Title,
        string? NewTitle,
        string? New_Title,
        string? Tagline,
        string? Synopsis,
        string? ShortTitle,
        string? Short_Title,
        string? Recommendation,
        string? Description,
        string? 新剧名,
        string? 剧名,
        string? 推荐语,
        string? 简介,
        string? 短标题,
        string[]? Tags,
        string[]? Tag_List,
        string[]? 标签);

    private sealed record NormalizedRewrite(
        string Title,
        string Tagline,
        string Synopsis,
        string ShortTitle,
        string Tags);
}
