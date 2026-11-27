using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.AI;

public sealed partial class ProjectInfoRewriter
{
    private async Task<ProjectInfoRewriteResult> RewriteWithUnifiedPublishPromptAsync(
        PublishInfoContext context,
        ProjectInfo project,
        string originalTitle,
        ProjectInfoRewriteRequest request,
        CancellationToken ct)
    {
        if (File.Exists(request.OutputFilePath) && !request.Overwrite)
            throw new InvalidOperationException($"输出文件已存在: {request.OutputFilePath}");
        if (string.IsNullOrWhiteSpace(context.SystemPrompt))
            throw new InvalidOperationException("请先配置发布信息提示词。");

        var originalSynopsis = request.OriginalSynopsis ?? project.Synopsis ?? string.Empty;
        var useEnglish = request.UseEnglishPublishText;
        var generateSynopsis = context.RewriteSynopsis || useEnglish;
        if (string.IsNullOrWhiteSpace(originalSynopsis) && (generateSynopsis || request.RestoreOriginalSynopsis))
            throw new InvalidOperationException(useEnglish
                ? "生成英文简介需要原简介，请先补充原始资料。"
                : "当前操作需要原简介，请先补充原始资料。");

        var preserved = ReadPreservedFields(request.OutputFilePath)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var existingAudience = FirstPreservedValue(preserved, AudienceFieldKeys);
        var existingThemes = SplitThemeTokens(FirstPreservedValue(preserved, ThemeFieldKeys))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var askAudience = context.AskAudience && existingAudience.Length == 0;
        var themeCount = Math.Max(0, context.ThemeCount - existingThemes.Length);
        var input = JsonSerializer.Serialize(new
        {
            original_title = originalTitle,
            current_title = project.Title,
            synopsis = originalSynopsis,
            episode_count = project.EpisodeCount,
            output_language = useEnglish
                ? RewriteSourceLanguage.English
                : context.FollowSourceLanguage ? "auto" : RewriteSourceLanguage.Chinese,
            rewrite_synopsis = generateSynopsis,
            forbidden_titles = UniqueTexts(request.ForbiddenTitles),
            forbidden_synopses = context.RewriteSynopsis ? UniqueTexts(request.ForbiddenSynopses) : Array.Empty<string>(),
            occupied_exact_titles = UniqueTexts(request.OccupiedExactTitles),
            rewrite_variant_key = request.RewriteVariantKey,
            theme_options = context.ThemeOptions,
            theme_count = themeCount,
            existing_themes = existingThemes,
            ask_audience = askAudience,
        }, PublishPromptJsonOptions);

        var prompt = useEnglish ? BuildEnglishPublishPrompt(context) : context.SystemPrompt;
        var generated = await RequestUnifiedPublishInfoAsync(context, prompt, input, generateSynopsis, useEnglish, ct);
        var synopsis = generateSynopsis
            ? AsSingleLine(generated.Description)
            : request.RestoreOriginalSynopsis ? AsSingleLine(originalSynopsis) : PreserveSourceSynopsis(project);
        if (themeCount > 0 && generated.Themes.Count > 0)
            preserved["题材类型"] = string.Join("、", existingThemes.Concat(generated.Themes));
        else if (existingThemes.Length > 0)
            preserved["题材类型"] = string.Join("、", existingThemes);
        if (askAudience && generated.Audience.Length > 0)
            preserved["目标观众"] = AsSingleLine(generated.Audience);
        else if (existingAudience.Length > 0)
            preserved["目标观众"] = existingAudience;

        var directory = Path.GetDirectoryName(request.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(request.OutputFilePath,
            BuildOutputText(project with { OriginalTitle = originalTitle }, generated.Title, string.Empty, synopsis,
                preserved, PublishInfoExtraKeys, request.OriginalSynopsis), Encoding.UTF8, ct);

        if (!useEnglish && context.FollowSourceLanguage && RewriteSourceLanguage.TryNormalize(generated.Language, out var language))
            RewriteSourceLanguage.SaveCached(project.ProjectDir, language);

        _logger.LogInformation("Rewrote publish info using the configured prompt to {Path}", request.OutputFilePath);
        return new ProjectInfoRewriteResult(request.OutputFilePath, generated.Title, string.Empty, synopsis, string.Empty, string.Empty);
    }

    private static string BuildEnglishPublishPrompt(PublishInfoContext context)
    {
        var limits = RewriteSourceLanguage.GetLimits(RewriteSourceLanguage.English);
        var synopsisInstruction = context.RewriteSynopsis
            ? "description 用自然英文轻度改写原简介，保留人物、情节和事实。"
            : "description 忠实翻译原简介，保留原有叙述顺序、语气和事实。";
        return context.SystemPrompt + "\n\n" + $"""
            【本次英文发布文案要求】
            title 和 description 必须使用英文，language 返回 en。
            title 生成有区别的英文新剧名，使用 {limits.TitleMin}–{limits.TitleMax} 个字符。
            {synopsisInstruction}
            人名和地名使用已有英文写法或罗马字转写。英文简介按自然译文长度撰写，最多 {limits.SynopsisMax} 个字符，忽略前文按原文字数或词数计算的比例。
            原简介较长时，压缩次要细节，为字符上限留出余量。返回前检查 title 和 description 的长度。
            """;
    }

    private async Task<UnifiedPublishInfo> RequestUnifiedPublishInfoAsync(
        PublishInfoContext context, string prompt, string input, bool generateSynopsis, bool useEnglish, CancellationToken ct)
    {
        const int maxAttempts = 2;
        EnglishPublishRepair? repair = null;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var requestPrompt = repair is null ? prompt : prompt + "\n\n" + """
                    【英文文案修正】
                    本段优先于前面的返回字段要求。repair_fields 包含本次要修正的 JSON 字段及原因。只返回这些字段，合格字段由程序保留。
                    用 original_request 核对原剧情和剧名限制，根据 previous_candidate 修正对应字段。
                    简介超长时，用简洁的英文完整句子压缩次要细节，保留人物、主要情节和事实，并为上限留出余量。
                    """;
                var requestInput = repair is null ? input : JsonSerializer.Serialize(new
                {
                    original_request = JsonSerializer.Deserialize<JsonElement>(input),
                    previous_candidate = new { title = repair.Draft.Title, description = repair.Draft.Description },
                    repair_fields = repair.Fields,
                }, PublishPromptJsonOptions);
                var payload = await RequestRewritePayloadAsync(
                    context.Endpoint, context.ModelId, context.ApiKey, requestPrompt, requestInput,
                    context.TimeoutSeconds, context.Temperature, ct, requireJsonObject: true);
                if (!useEnglish && string.Equals(payload.Language?.Trim(), "unsupported", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型报告原文语种不受支持。");

                var title = AsSingleLine(FirstNonEmpty(payload.Title, payload.NewTitle, payload.New_Title,
                    payload.新剧名, payload.剧名) ?? string.Empty);
                var description = AsSingleLine(FirstNonEmpty(payload.Description, payload.Synopsis, payload.简介) ?? string.Empty);
                if (!useEnglish && title.Length == 0)
                    throw new InvalidOperationException("改写结果缺少新剧名。");
                if (!useEnglish && generateSynopsis && description.Length == 0)
                    throw new InvalidOperationException("改写结果缺少简介。");

                var result = repair is null
                    ? new UnifiedPublishInfo(title, description, ReadUnifiedThemes(payload.Themes ?? payload.Genres),
                        FirstNonEmpty(payload.Audience, payload.Target_Audience) ?? string.Empty, payload.Language)
                    : repair.Draft with
                    {
                        Title = repair.Fields.ContainsKey("title") ? title : repair.Draft.Title,
                        Description = repair.Fields.ContainsKey("description") ? description : repair.Draft.Description,
                        Language = repair.Fields.ContainsKey("language") ? payload.Language : repair.Draft.Language,
                    };
                if (!useEnglish)
                    return result;

                var fields = GetEnglishPublishErrors(result);
                if (fields.Count == 0)
                    return result;

                var failure = string.Join("。", fields.Values);
                if (attempt == maxAttempts)
                    throw new InvalidOperationException(failure);

                repair = new EnglishPublishRepair(result, fields);
                _logger.LogWarning("英文发布文案校验失败，将修正未通过的字段。{Reason}", failure);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is InvalidOperationException or JsonException or HttpRequestException or IOException or OperationCanceledException)
            {
                var reason = ex is OperationCanceledException ? "改写请求超时。" : ex.Message;
                if (attempt == maxAttempts)
                {
                    var retryLabel = repair is null ? "使用同一提示词重试" : "修正不合规字段";
                    throw new InvalidOperationException($"{retryLabel}一次后仍然失败：{reason}", ex);
                }

                _logger.LogWarning("Publish info request failed. Retrying with the same prompt and input: {Reason}", reason);
            }
        }
    }

    private static Dictionary<string, string> GetEnglishPublishErrors(UnifiedPublishInfo candidate)
    {
        var limits = RewriteSourceLanguage.GetLimits(RewriteSourceLanguage.English);
        var fields = new Dictionary<string, string>();
        if (candidate.Title.Length < limits.TitleMin || candidate.Title.Length > limits.TitleMax)
            fields["title"] = $"英文新剧名 {candidate.Title.Length} 个字符，要求 {limits.TitleMin}–{limits.TitleMax} 个字符";
        else if (!HasOnlyLatinLetters(candidate.Title))
            fields["title"] = "英文新剧名须使用英文";

        if (candidate.Description.Length > limits.SynopsisMax)
            fields["description"] = $"英文简介 {candidate.Description.Length} 个字符，上限 {limits.SynopsisMax} 个字符";
        else if (candidate.Description.Length == 0)
            fields["description"] = "英文简介为空，请根据原简介生成英文简介";
        else if (!HasOnlyLatinLetters(candidate.Description))
            fields["description"] = "英文简介须使用英文";

        if (!string.Equals(candidate.Language?.Trim(), RewriteSourceLanguage.English, StringComparison.OrdinalIgnoreCase))
            fields["language"] = "文案语种字段 language 须返回 en";

        return fields;
    }

    private static bool HasOnlyLatinLetters(string text) =>
        text.Any(char.IsAsciiLetter) &&
        text.All(ch => !char.IsLetter(ch) || char.IsAsciiLetter(ch) ||
            ch is >= '\u00C0' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF');

    private static IReadOnlyList<string> ReadUnifiedThemes(JsonElement? themes)
    {
        if (themes is not { } value || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return SplitThemeTokens(value.GetString()).ToArray();
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException("改写结果的 themes 必须是字符串数组。");

        return value.EnumerateArray().Select(item => AsSingleLine(item.GetString() ?? string.Empty))
            .Where(item => item.Length != 0).ToArray();
    }

    private static string AsSingleLine(string text) => string.Join(' ',
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record UnifiedPublishInfo(string Title, string Description, IReadOnlyList<string> Themes, string Audience, string? Language);
    private sealed record EnglishPublishRepair(UnifiedPublishInfo Draft, IReadOnlyDictionary<string, string> Fields);
}
