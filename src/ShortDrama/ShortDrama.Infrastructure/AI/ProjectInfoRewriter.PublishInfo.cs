using Microsoft.Extensions.Logging;
using ShortDrama.Core.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.AI;

/// <summary>
/// TikTok 发布信息：一个提示词、一次请求生成新剧名、简介、题材、目标观众和输出语言。不生成推荐语。
/// </summary>
public sealed partial class ProjectInfoRewriter
{
    private static readonly string[] AudienceFieldKeys = ["目标观众", "TikTok目标观众", "TikTok 目标观众", "目标受众"];
    private static readonly string[] ThemeFieldKeys = ["题材类型", "TikTok题材类型", "TikTok 题材类型", "题材"];
    private static readonly string[] PublishInfoExtraKeys = ["目标观众", "题材类型"];

    private static readonly JsonSerializerOptions PublishPromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record PublishInfoContext(
        string Endpoint,
        string ModelId,
        string ApiKey,
        string SystemPrompt,
        int TimeoutSeconds,
        double Temperature,
        bool RewriteSynopsis,
        bool FollowSourceLanguage,
        IReadOnlyList<string> ThemeOptions,
        int ThemeCount,
        bool AskAudience);

    private async Task<ProjectInfoRewriteResult> RewritePublishInfoAsync(
        PublishInfoContext context,
        ProjectInfo project,
        string canonicalOriginalTitle,
        ProjectInfoRewriteRequest request,
        CancellationToken cancellationToken)
    {
        if (File.Exists(request.OutputFilePath) && !request.Overwrite)
            throw new InvalidOperationException($"输出文件已存在: {request.OutputFilePath}");

        var outputDirectory = Path.GetDirectoryName(request.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var preserved = ReadPreservedFields(request.OutputFilePath)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var existingAudience = NormalizePublishAudience(FirstPreservedValue(preserved, AudienceFieldKeys));
        var existingThemes = MatchThemes(
            SplitThemeTokens(FirstPreservedValue(preserved, ThemeFieldKeys)),
            context.ThemeOptions,
            context.ThemeCount);
        var askAudience = context.AskAudience && existingAudience.Length == 0;
        var themeCount = Math.Max(0, context.ThemeCount - existingThemes.Count);
        var knownLanguage = ResolveKnownPublishLanguage(context.FollowSourceLanguage, project, canonicalOriginalTitle);

        string? retryReason = null;
        string? previousTitle = null;
        NormalizedRewrite? lastNormalized = null;
        RewritePayload? lastPayload = null;
        NormalizedRewrite? accepted = null;
        var language = knownLanguage ?? RewriteSourceLanguage.English;

        for (var attempt = 1; attempt <= MaxAiAttempts; attempt++)
        {
            var prompt = BuildPublishInfoPrompt(
                project,
                canonicalOriginalTitle,
                request,
                context,
                knownLanguage,
                themeCount,
                existingThemes,
                askAudience,
                retryReason,
                previousTitle);
            var payload = await RequestRewritePayloadAsync(
                context.Endpoint,
                context.ModelId,
                context.ApiKey,
                context.SystemPrompt,
                prompt,
                context.TimeoutSeconds,
                context.Temperature,
                cancellationToken);
            lastPayload = payload;
            var rawTitle = FirstNonEmpty(payload.Title, payload.NewTitle, payload.New_Title, payload.新剧名, payload.剧名);

            if (knownLanguage is null)
            {
                var reported = (payload.Language ?? string.Empty).Trim();
                if (reported.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "改写失败：原剧名和原简介的语言不在允许范围内。允许中文、英语、印尼语、葡语、日语、泰语、西语、韩语、土耳其语、印地语。");
                }

                if (!RewriteSourceLanguage.TryNormalize(reported, out var detected))
                {
                    retryReason = $"language 返回「{(reported.Length == 0 ? "空" : reported)}」，必须是 zh、en、id、pt、ja、th、es、ko、tr、hi 之一";
                    previousTitle = rawTitle;
                    continue;
                }

                knownLanguage = detected;
                language = detected;
                RewriteSourceLanguage.SaveCached(project.ProjectDir, detected);
                _logger.LogInformation("源语言识别为{Language}", RewriteSourceLanguage.DisplayName(detected));
            }

            NormalizedRewrite normalized;
            try
            {
                normalized = NormalizePublishRewrite(project, canonicalOriginalTitle, payload, context.RewriteSynopsis);
            }
            catch (InvalidOperationException ex)
            {
                retryReason = ex.Message;
                previousTitle = rawTitle;
                continue;
            }

            var issues = GetRewriteQualityIssues(
                    canonicalOriginalTitle,
                    normalized,
                    request,
                    context.RewriteSynopsis,
                    language,
                    checkTagline: false)
                .ToList();
            var synopsisMax = RewriteSourceLanguage.GetLimits(language).SynopsisMax;
            if (context.RewriteSynopsis && normalized.Synopsis.Length > synopsisMax)
                issues.Add($"简介超过 {synopsisMax} 个字符，当前 {normalized.Synopsis.Length} 个字符");

            if (issues.Count == 0)
            {
                accepted = normalized;
                break;
            }

            lastNormalized = normalized;
            previousTitle = normalized.Title;
            retryReason = string.Join("；", issues);
        }

        if (accepted is null)
        {
            var fallback = TryBuildRecentTitleFallback(
                project,
                canonicalOriginalTitle,
                request,
                lastNormalized,
                previousTitle,
                context.RewriteSynopsis,
                language);
            if (fallback is null)
            {
                throw new InvalidOperationException(
                    $"改写结果生成失败，已重试 {MaxAiAttempts} 次仍未通过。最近一次错误：{retryReason ?? "未知错误"}；最近一次新剧名：{previousTitle ?? "无"}");
            }

            _logger.LogWarning(
                "AI publish info did not pass all validation after {Attempts} attempts, using recent generated title fallback. Last error: {Error}; title: {Title}",
                MaxAiAttempts,
                retryReason ?? "未知错误",
                fallback.Title);
            accepted = fallback with { Tagline = string.Empty };
        }

        var themes = existingThemes
            .Concat(MatchThemes(PayloadThemeTokens(lastPayload), context.ThemeOptions, themeCount))
            .Distinct(StringComparer.Ordinal)
            .Take(context.ThemeCount)
            .ToList();
        var audience = existingAudience.Length > 0
            ? existingAudience
            : askAudience
                ? NormalizePublishAudience(FirstNonEmpty(lastPayload?.Audience, lastPayload?.Target_Audience))
                : string.Empty;
        if (themes.Count > 0)
            preserved["题材类型"] = string.Join("、", themes);
        if (audience.Length > 0)
            preserved["目标观众"] = audience;

        await File.WriteAllTextAsync(
            request.OutputFilePath,
            BuildOutputText(
                project with { OriginalTitle = canonicalOriginalTitle },
                accepted.Title,
                string.Empty,
                accepted.Synopsis,
                preserved,
                PublishInfoExtraKeys),
            Encoding.UTF8,
            cancellationToken);

        _logger.LogInformation(
            "Rewrote publish info to {Path}; themes: {Themes}; audience: {Audience}",
            request.OutputFilePath,
            string.Join("、", themes),
            audience);
        return new ProjectInfoRewriteResult(
            request.OutputFilePath,
            accepted.Title,
            string.Empty,
            accepted.Synopsis,
            string.Empty,
            string.Empty);
    }

    private static string? ResolveKnownPublishLanguage(
        bool followSourceLanguage,
        ProjectInfo project,
        string canonicalOriginalTitle)
    {
        if (!followSourceLanguage)
            return RewriteSourceLanguage.Chinese;

        var cached = RewriteSourceLanguage.ReadCached(project.ProjectDir);
        if (cached is not null)
            return cached;

        return RewriteSourceLanguage.IsClearlyChinese(canonicalOriginalTitle, project.Synopsis)
            ? RewriteSourceLanguage.Chinese
            : null;
    }

    private static NormalizedRewrite NormalizePublishRewrite(
        ProjectInfo project,
        string canonicalOriginalTitle,
        RewritePayload payload,
        bool rewriteSynopsis)
    {
        var rawTitle = FirstNonEmpty(payload.Title, payload.NewTitle, payload.New_Title, payload.新剧名, payload.剧名)
            ?? throw new InvalidOperationException("改写结果缺少新剧名。");
        var title = NormalizeRewrittenTitle(rawTitle);

        var synopsis = PreserveSourceSynopsis(project);
        if (rewriteSynopsis)
        {
            var rawSynopsis = FirstNonEmpty(payload.Description, payload.Synopsis, payload.简介)
                ?? throw new InvalidOperationException("改写结果缺少简介。");
            synopsis = FitSynopsisToAbsoluteMax(
                NormalizeGeneratedText(project, canonicalOriginalTitle, title, rawTitle, rawSynopsis));
            if (string.IsNullOrWhiteSpace(synopsis))
                throw new InvalidOperationException("改写结果缺少简介。");
        }

        return new NormalizedRewrite(title, string.Empty, synopsis, string.Empty, string.Empty);
    }

    private static string BuildPublishInfoPrompt(
        ProjectInfo project,
        string canonicalOriginalTitle,
        ProjectInfoRewriteRequest request,
        PublishInfoContext context,
        string? knownLanguage,
        int themeCount,
        IReadOnlyList<string> existingThemes,
        bool askAudience,
        string? retryReason,
        string? previousTitle)
    {
        var limits = RewriteSourceLanguage.GetLimits(knownLanguage ?? RewriteSourceLanguage.English);
        var input = new
        {
            original_title = canonicalOriginalTitle,
            current_title = FirstNonEmpty(project.Title, canonicalOriginalTitle) ?? canonicalOriginalTitle,
            synopsis = FirstNonEmpty(project.Synopsis, "待补充简介。"),
            episode_count = project.EpisodeCount,
            rewrite_synopsis = context.RewriteSynopsis,
            target_synopsis_length = context.RewriteSynopsis ? Math.Max(0, request.TargetSynopsisLength) : 0,
            forbidden_titles = UniqueTexts(request.ForbiddenTitles),
            forbidden_synopses = context.RewriteSynopsis ? UniqueTexts(request.ForbiddenSynopses) : Array.Empty<string>(),
            occupied_exact_titles = UniqueTexts(request.OccupiedExactTitles),
            rewrite_variant_key = request.RewriteVariantKey ?? string.Empty,
            theme_options = context.ThemeOptions,
            theme_count = themeCount,
            existing_themes = existingThemes,
            ask_audience = askAudience,
            output_language = PublishLanguageRule(knownLanguage),
            length_rules = new
            {
                title = $"{limits.TitleMin}-{limits.TitleMax} 个字符",
                description = context.RewriteSynopsis
                    ? $"{limits.SynopsisMin}-{limits.SynopsisMax} 个字符，尽量接近 target_synopsis_length"
                    : "返回空字符串",
            },
            retry = retryReason is null
                ? null
                : new { reason = retryReason, previous_title = previousTitle ?? string.Empty },
        };

        return $$"""
            请为下面这部短剧生成 TikTok 发布信息。theme_count 为 0 时 themes 返回空数组；existing_themes 已经确定，不要重复返回。
            如果包含 retry，说明上一次结果不合格，必须修复 retry.reason 里的问题，并且不要再用 retry.previous_title。
            只返回一个 JSON 对象：{"title":"","description":"","themes":[],"audience":"","language":""}

            输入：
            {{JsonSerializer.Serialize(input, PublishPromptJsonOptions)}}
            """;
    }

    private static string PublishLanguageRule(string? knownLanguage)
    {
        if (knownLanguage is null)
        {
            return "跟随原文：先判断 original_title 和 synopsis 的语言（只能是 zh、en、id、pt、ja、th、es、ko、tr、hi），" +
                   "title 和 description 必须使用该语言，禁止翻译成中文，language 返回该代码；都不是时 language 返回 unsupported。";
        }

        if (RewriteSourceLanguage.IsChinese(knownLanguage))
            return "zh：title 和 description 使用简体中文，language 返回 zh。";

        return $"{knownLanguage}：title 和 description 必须使用{RewriteSourceLanguage.DisplayName(knownLanguage)}，" +
               $"禁止翻译成中文，不要套用中文短剧标题套路，language 返回 {knownLanguage}。";
    }

    private static IReadOnlyList<string> ParseThemeOptions(string? value) =>
        (value ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string FirstPreservedValue(IReadOnlyDictionary<string, string> fields, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static IEnumerable<string> PayloadThemeTokens(RewritePayload? payload)
    {
        if (payload is null)
            return [];

        var element = payload.Themes ?? payload.Genres;
        if (element is not { } value)
            return [];

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .SelectMany(item => SplitThemeTokens(item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())),
            JsonValueKind.String => SplitThemeTokens(value.GetString()),
            _ => Enumerable.Empty<string>(),
        };
    }

    private static IEnumerable<string> SplitThemeTokens(string? value) =>
        Regex.Split(value ?? string.Empty, @"[#,\uFF0C\u3001/\\\s;；|]+")
            .Where(static part => !string.IsNullOrWhiteSpace(part));

    internal static IReadOnlyList<string> MatchThemes(
        IEnumerable<string> tokens,
        IReadOnlyList<string> options,
        int maxCount)
    {
        var selected = new List<string>();
        if (maxCount <= 0 || options.Count == 0)
            return selected;

        foreach (var token in tokens)
        {
            var text = token.Trim().Trim('#', '"', '“', '”').Trim();
            if (text.Length == 0)
                continue;

            var match = options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (match is null && text.Length >= 2)
            {
                match = options.FirstOrDefault(option =>
                    text.Contains(option, StringComparison.OrdinalIgnoreCase) ||
                    option.Contains(text, StringComparison.OrdinalIgnoreCase));
            }

            if (match is null || selected.Contains(match, StringComparer.Ordinal))
                continue;

            selected.Add(match);
            if (selected.Count >= maxCount)
                break;
        }

        return selected;
    }

    internal static string NormalizePublishAudience(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text is "女" or "女性" or "女频" or "female" or "woman" or "women" or "f")
            return "女";
        if (text is "男" or "男性" or "男频" or "male" or "man" or "men" or "m")
            return "男";
        return string.Empty;
    }
}
