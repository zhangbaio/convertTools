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
        if (context.RewriteSynopsis && string.IsNullOrWhiteSpace(originalSynopsis))
            throw new InvalidOperationException("简介改写需要原简介，请先补充原始资料。");

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
            output_language = context.FollowSourceLanguage ? "auto" : RewriteSourceLanguage.Chinese,
            rewrite_synopsis = context.RewriteSynopsis,
            forbidden_titles = UniqueTexts(request.ForbiddenTitles),
            forbidden_synopses = context.RewriteSynopsis ? UniqueTexts(request.ForbiddenSynopses) : Array.Empty<string>(),
            occupied_exact_titles = UniqueTexts(request.OccupiedExactTitles),
            rewrite_variant_key = request.RewriteVariantKey,
            theme_options = context.ThemeOptions,
            theme_count = themeCount,
            existing_themes = existingThemes,
            ask_audience = askAudience,
        }, PublishPromptJsonOptions);

        var generated = await RequestUnifiedPublishInfoAsync(context, input, ct);
        var synopsis = context.RewriteSynopsis ? AsSingleLine(generated.Description) : PreserveSourceSynopsis(project);
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

        if (context.FollowSourceLanguage && RewriteSourceLanguage.TryNormalize(generated.Language, out var language))
            RewriteSourceLanguage.SaveCached(project.ProjectDir, language);

        _logger.LogInformation("Rewrote publish info using the configured prompt to {Path}", request.OutputFilePath);
        return new ProjectInfoRewriteResult(request.OutputFilePath, generated.Title, string.Empty, synopsis, string.Empty, string.Empty);
    }

    private async Task<UnifiedPublishInfo> RequestUnifiedPublishInfoAsync(
        PublishInfoContext context, string input, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var payload = await RequestRewritePayloadAsync(
                    context.Endpoint, context.ModelId, context.ApiKey, context.SystemPrompt, input,
                    context.TimeoutSeconds, context.Temperature, ct, requireJsonObject: true);
                if (string.Equals(payload.Language?.Trim(), "unsupported", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型报告原文语种不受支持。");

                var title = FirstNonEmpty(payload.Title, payload.NewTitle, payload.New_Title, payload.新剧名, payload.剧名)
                    ?? throw new InvalidOperationException("改写结果缺少新剧名。");
                var description = FirstNonEmpty(payload.Description, payload.Synopsis, payload.简介) ?? string.Empty;
                if (context.RewriteSynopsis && description.Length == 0)
                    throw new InvalidOperationException("改写结果缺少简介。");

                return new UnifiedPublishInfo(AsSingleLine(title), description,
                    ReadUnifiedThemes(payload.Themes ?? payload.Genres),
                    FirstNonEmpty(payload.Audience, payload.Target_Audience) ?? string.Empty, payload.Language);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is InvalidOperationException or JsonException or HttpRequestException or IOException or OperationCanceledException)
            {
                var reason = ex is OperationCanceledException ? "改写请求超时。" : ex.Message;
                if (attempt == maxAttempts)
                    throw new InvalidOperationException($"使用同一提示词重试一次后仍然失败：{reason}", ex);

                _logger.LogWarning("Publish info request failed. Retrying with the same prompt and input: {Reason}", reason);
            }
        }
    }

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
}
