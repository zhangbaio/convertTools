using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
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
4. short_title：适合短剧传播的短标题，长度 6-15 个字，不能包含空格、逗号、句号、感叹号、问号等特殊字符；不能直接等于 title，也不能只是原剧名或 title 的截断。短标题要像一个独立卖点词组，例如“封墙断供”“为母改命”“大圣不朽”“婚房追偿”。
5. tags：3-6 个标签，突出题材、关系、情绪或卖点。每个标签不要带 #，程序会自动格式化成 #标签 串联形式；标签不能直接使用完整剧名。标签应尽量像“婆媳”“逆袭”“复仇”“重生”“萌宝”“婚姻”“亲情”“年代”“虐恋”“打脸”这类高复用传播词。

输出要求：
1. 只输出 JSON。
2. 当前一次只改写一个项目，请返回单个 JSON 对象，不要返回 items 数组。
3. JSON 格式固定为：
{"title":"...","tagline":"...","synopsis":"...","short_title":"...","tags":["...","..."]}
4. 新剧名禁止以下偷懒改法：原剧名原样返回；原剧名 + “热播版/新篇/完整版/逆袭版/高能版/爆款版”；只替换一两个字但整体几乎不变；保留原标题主要词序仅在首尾微调。
5. short_title 必须比 title 更短、更利于传播，但要保持独立表达，不要只删几个字。
6. tags 必须是题材词、情绪词、人物关系词、剧情卖点词，不要输出完整标题，也不要输出只比标题少几个字的近似词。

输入项目：
{items_json}
""";
    private const string DefaultAiTextRetryPrompt = """

上一次结果不合格，请重新生成，并严格遵守：
1. 新剧名必须和原剧名明显不同，不能只是加“热播版/新篇/新作”等尾巴。
2. 新剧名要像全新包装标题，优先体现冲突、关系、身份、命运转折。
3. 短标题必须独立，不得与新剧名相同或近似。
4. 标签必须是传播词，不得与完整剧名相同或近似。
5. 上次不合格的新剧名：{previous_bad_title}
6. 上次不合格的短标题：{previous_bad_short_title}
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int MaxAiAttempts = 2;

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
        var batchPrompt = GetOptional(config, "AiTextBatchPrompt") ?? DefaultAiTextBatchPrompt;
        var retryPrompt = GetOptional(config, "AiTextRetryPrompt") ?? DefaultAiTextRetryPrompt;

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
            batchPrompt,
            retryPrompt,
            project,
            canonicalOriginalTitle,
            cancellationToken);

        await File.WriteAllTextAsync(
            request.OutputFilePath,
            BuildOutputText(project with { OriginalTitle = canonicalOriginalTitle }, normalized.Title, normalized.Tagline, normalized.Synopsis, normalized.ShortTitle, normalized.Tags),
            Encoding.UTF8,
            cancellationToken);

        _logger.LogInformation("Rewrote project info to {Path}", request.OutputFilePath);
        return new ProjectInfoRewriteResult(request.OutputFilePath, normalized.Title, normalized.Tagline, normalized.Synopsis, normalized.ShortTitle, normalized.Tags);
    }

    private static string BuildPrompt(ProjectInfo project, string batchPrompt)
    {
        var canonicalOriginalTitle = ResolveCanonicalOriginalTitle(project);
        var itemsJson = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    id = "1",
                    title = canonicalOriginalTitle,
                    original_title = canonicalOriginalTitle,
                    tagline = project.Tagline ?? string.Empty,
                    synopsis = project.Synopsis ?? string.Empty,
                    short_title = project.ShortTitle ?? string.Empty,
                    tags = ProjectInfoTextNormalizer.NormalizeTags(project.Tags ?? string.Empty),
                    episode_count = project.EpisodeCount,
                    total_minutes = project.TotalMinutes
                }
            }
        });

        var template = string.IsNullOrWhiteSpace(batchPrompt) ? DefaultAiTextBatchPrompt : batchPrompt;
        if (!template.Contains("{items_json}", StringComparison.Ordinal))
        {
            template += "\n\n输入项目：\n{items_json}";
        }

        return template.Replace("{items_json}", itemsJson, StringComparison.Ordinal);
    }

    private static string BuildOutputText(ProjectInfo project, string title, string tagline, string synopsis, string shortTitle, string tags)
    {
        var originalTitleLine = string.IsNullOrWhiteSpace(project.OriginalTitle)
            ? string.Empty
            : $"原剧名: {project.OriginalTitle}\n";

        return
            $"{originalTitleLine}" +
            $"新剧名: {title}\n" +
            $"推荐语: {tagline}\n" +
            $"简介: {synopsis}\n" +
            $"短标题: {shortTitle}\n" +
            $"标签: {tags}\n" +
            $"时长: {project.TotalMinutes} 分钟\n" +
            $"集数: {project.EpisodeCount}\n" +
            $"成本: {project.CostAmountWan:0.####} 万元\n" +
            $"制作公司: {project.CompanyName}\n";
    }

    private async Task<NormalizedRewrite> RewriteWithRetryAsync(
        string endpoint,
        string modelId,
        string apiKey,
        string batchPrompt,
        string retryPromptTemplate,
        ProjectInfo project,
        string canonicalOriginalTitle,
        CancellationToken cancellationToken)
    {
        string? previousBadTitle = null;
        string? previousBadShortTitle = null;
        string? lastFailureMessage = null;

        for (var attempt = 1; attempt <= MaxAiAttempts; attempt++)
        {
            var prompt = attempt == 1
                ? BuildPrompt(project, batchPrompt)
                : BuildRetryPrompt(project, batchPrompt, retryPromptTemplate, previousBadTitle, previousBadShortTitle);

            var rewrite = await RequestRewritePayloadAsync(
                endpoint,
                modelId,
                apiKey,
                prompt,
                cancellationToken);

            NormalizedRewrite normalized;
            try
            {
                normalized = NormalizeRewrite(project, canonicalOriginalTitle, rewrite);
            }
            catch (InvalidOperationException ex)
            {
                previousBadTitle = FirstNonEmpty(rewrite.Title, rewrite.NewTitle, rewrite.新剧名, rewrite.剧名);
                previousBadShortTitle = FirstNonEmpty(rewrite.ShortTitle, rewrite.Short_Title, rewrite.短标题);
                lastFailureMessage = ex.Message;
                continue;
            }

            if (IsRewriteQualityAcceptable(canonicalOriginalTitle, normalized))
            {
                return normalized;
            }

            previousBadTitle = normalized.Title;
            previousBadShortTitle = normalized.ShortTitle;
            lastFailureMessage = "生成结果未通过质量校验。";
        }

        throw new InvalidOperationException(
            $"改写结果生成失败，已重试 {MaxAiAttempts} 次仍未通过。最近一次错误：{lastFailureMessage ?? "未知错误"}；最近一次新剧名：{previousBadTitle ?? "无"}；最近一次短标题：{previousBadShortTitle ?? "无"}");
    }

    private async Task<RewritePayload> RequestRewritePayloadAsync(
        string endpoint,
        string modelId,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = modelId,
            temperature = 0.8,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"改写接口请求失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {responseText}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("无法解析改写接口响应。");

        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("改写接口未返回内容。");
        }

        var jsonText = ExtractJsonObject(content);
        var payloadText = ExtractRewritePayloadJson(jsonText);
        return JsonSerializer.Deserialize<RewritePayload>(payloadText, JsonOptions)
            ?? throw new InvalidOperationException("无法解析改写结果 JSON。");
    }

    private static NormalizedRewrite NormalizeRewrite(ProjectInfo project, string canonicalOriginalTitle, RewritePayload rewrite)
    {
        var rawTitle = FirstNonEmpty(rewrite.Title, rewrite.NewTitle, rewrite.新剧名, rewrite.剧名)
            ?? throw new InvalidOperationException("改写结果缺少新剧名。");
        var title = NormalizeRewrittenTitle(rawTitle);
        var rawTagline = FirstNonEmpty(rewrite.Tagline, rewrite.Recommendation, rewrite.推荐语)
            ?? throw new InvalidOperationException("改写结果缺少推荐语。");
        var tagline = NormalizeGeneratedText(project, canonicalOriginalTitle, title, rawTitle, rawTagline);
        if (string.IsNullOrWhiteSpace(tagline))
        {
            throw new InvalidOperationException("改写结果缺少推荐语。");
        }

        var rawSynopsis = FirstNonEmpty(rewrite.Synopsis, rewrite.Description, rewrite.简介)
            ?? throw new InvalidOperationException("改写结果缺少简介。");
        var synopsis = NormalizeGeneratedText(project, canonicalOriginalTitle, title, rawTitle, rawSynopsis);
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            throw new InvalidOperationException("改写结果缺少简介。");
        }

        var shortTitleSource = FirstNonEmpty(rewrite.ShortTitle, rewrite.Short_Title, rewrite.短标题);
        var shortTitle = NormalizeShortTitle(canonicalOriginalTitle, title, shortTitleSource);
        if (string.IsNullOrWhiteSpace(shortTitle))
        {
            throw new InvalidOperationException("改写结果缺少短标题。");
        }

        var rawTags = rewrite.Tags
            ?? rewrite.Tag_List
            ?? rewrite.标签
            ?? [];
        var tags = NormalizeTags(canonicalOriginalTitle, title, rawTags);
        if (string.IsNullOrWhiteSpace(tags))
        {
            throw new InvalidOperationException("改写结果缺少标签。");
        }

        return new NormalizedRewrite(title, tagline, synopsis, shortTitle, tags);
    }

    private static string BuildRetryPrompt(
        ProjectInfo project,
        string batchPrompt,
        string retryPromptTemplate,
        string? previousBadTitle,
        string? previousBadShortTitle)
    {
        var basePrompt = BuildPrompt(project, batchPrompt);
        var retryHint = (string.IsNullOrWhiteSpace(retryPromptTemplate) ? DefaultAiTextRetryPrompt : retryPromptTemplate)
            .Replace("{previous_bad_title}", previousBadTitle ?? "无", StringComparison.Ordinal)
            .Replace("{previous_bad_short_title}", previousBadShortTitle ?? "无", StringComparison.Ordinal);
        return basePrompt + retryHint;
    }

    private static bool IsRewriteQualityAcceptable(string canonicalOriginalTitle, NormalizedRewrite rewrite)
    {
        if (TitlesEqual(rewrite.Title, canonicalOriginalTitle) ||
            TitlesTooSimilar(rewrite.Title, canonicalOriginalTitle) ||
            IsLazyRetitle(rewrite.Title, canonicalOriginalTitle))
        {
            return false;
        }

        if (TitlesEqual(rewrite.ShortTitle, rewrite.Title) ||
            TitlesTooSimilar(rewrite.ShortTitle, rewrite.Title) ||
            TitlesEqual(rewrite.ShortTitle, canonicalOriginalTitle) ||
            IsWeakGenericShortTitle(rewrite.ShortTitle))
        {
            return false;
        }

        if (ProjectInfoTextNormalizer.NormalizeTags(rewrite.Tags).Count < 2)
        {
            return false;
        }

        return true;
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

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
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
        return value
            .Replace("《", string.Empty, StringComparison.Ordinal)
            .Replace("》", string.Empty, StringComparison.Ordinal)
            .Trim();
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

    private static string NormalizeShortTitle(string canonicalOriginalTitle, string title, string? shortTitleSource)
    {
        var cleaned = ProjectInfoTextNormalizer.SanitizeShortTitle(shortTitleSource, 15);
        if (string.IsNullOrWhiteSpace(cleaned) ||
            TitlesEqual(cleaned, title) ||
            TitlesTooSimilar(cleaned, title) ||
            TitlesEqual(cleaned, canonicalOriginalTitle) ||
            IsWeakGenericShortTitle(cleaned))
        {
            return string.Empty;
        }

        return ProjectInfoTextNormalizer.SanitizeShortTitle(cleaned, 15);
    }

    private static bool IsWeakGenericShortTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var cleaned = NormalizeTitle(value);
        return cleaned.Contains("高能短剧", StringComparison.Ordinal) ||
               cleaned.Contains("爆款短剧", StringComparison.Ordinal) ||
               cleaned.Contains("热门短剧", StringComparison.Ordinal) ||
               cleaned == "高能逆袭";
    }

    private static string BuildFallbackShortTitle(ProjectInfo project, string canonicalOriginalTitle, string title, string synopsis)
    {
        var candidates = BuildFallbackTagCandidates(title, synopsis).ToArray();
        var strongPairs = new[]
        {
            ("萌娃", "救父", "萌娃救父"),
            ("顽娃", "救父", "顽娃救父"),
            ("重生", "逆袭", "重生逆袭"),
            ("大院", "娇妻", "大院娇妻"),
            ("豪门", "认亲", "豪门认亲"),
            ("离婚", "反杀", "离婚反杀"),
            ("前夫", "追妻", "前夫追妻"),
            ("替身", "逆袭", "替身逆袭"),
            ("逼婚", "反杀", "逼婚反杀"),
            ("萌宝", "复仇", "萌宝复仇")
        };

        foreach (var (first, second, result) in strongPairs)
        {
            if (candidates.Contains(first, StringComparer.Ordinal) &&
                candidates.Contains(second, StringComparer.Ordinal))
            {
                return result;
            }
        }

        if (candidates.Length >= 2)
        {
            var combined = ProjectInfoTextNormalizer.SanitizeShortTitle(candidates[0] + candidates[1], 15);
            if (!string.IsNullOrWhiteSpace(combined) &&
                !TitlesTooSimilar(combined, title) &&
                !TitlesTooSimilar(combined, canonicalOriginalTitle))
            {
                return combined;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Length >= 4 &&
                !TitlesTooSimilar(candidate, title) &&
                !TitlesTooSimilar(candidate, canonicalOriginalTitle))
            {
                return candidate;
            }
        }

        var titleCore = NormalizeTitle(title);
        return titleCore.Length > 8
            ? titleCore[..8]
            : "逆袭改命";
    }

    private static string NormalizeTags(string canonicalOriginalTitle, string title, IEnumerable<string?> rawTags)
    {
        var filtered = ProjectInfoTextNormalizer.NormalizeTags(rawTags)
            .Where(tag => !TitlesEqual(tag, title) &&
                          !TitlesTooSimilar(tag, title) &&
                          !TitlesEqual(tag, canonicalOriginalTitle) &&
                          !TitlesTooSimilar(tag, canonicalOriginalTitle))
            .ToList();

        return ProjectInfoTextNormalizer.FormatTags(filtered);
    }

    private static IEnumerable<string> BuildFallbackTagCandidates(string title, string synopsis)
    {
        var text = $"{title} {synopsis}";
        var candidates = new List<string>();

        void AddIfContains(string tag, params string[] needles)
        {
            if (needles.Any(needle => text.Contains(needle, StringComparison.Ordinal)) &&
                !candidates.Contains(tag, StringComparer.Ordinal))
            {
                candidates.Add(tag);
            }
        }

        AddIfContains("重生", "重生", "再活一世", "重来一世", "重来一回");
        AddIfContains("逆袭", "逆袭", "翻身", "改命", "改写命运");
        AddIfContains("复仇", "复仇", "报复", "反杀");
        AddIfContains("打脸", "打脸", "反击", "手撕");
        AddIfContains("前夫", "前夫");
        AddIfContains("追妻", "追妻", "追回", "回头");
        AddIfContains("豪门", "豪门", "千金", "总裁");
        AddIfContains("萌娃", "萌娃", "顽娃", "孩子", "娃");
        AddIfContains("救父", "救父", "救爹", "救爸");
        AddIfContains("亲情", "父亲", "母亲", "爷爷", "女儿", "儿子", "亲情");
        AddIfContains("婚姻", "婚姻", "离婚", "婚礼", "结婚", "婚约");
        AddIfContains("虐渣", "渣男", "渣女", "净身出户", "背叛");
        AddIfContains("大院", "大院");
        AddIfContains("娇妻", "娇妻");
        AddIfContains("年代", "九零", "八零", "七零", "年代");
        AddIfContains("替身", "替身");
        AddIfContains("认亲", "认亲");
        AddIfContains("宅斗", "侯府", "王府", "后宅", "宅斗");
        AddIfContains("婆媳", "婆婆", "婆媳", "媳妇");
        AddIfContains("逼婚", "逼婚", "婚约");
        AddIfContains("火葬场", "火葬场");

        if (candidates.Count == 0)
        {
            candidates.AddRange(["逆袭", "高能", "反转"]);
        }

        return candidates;
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
