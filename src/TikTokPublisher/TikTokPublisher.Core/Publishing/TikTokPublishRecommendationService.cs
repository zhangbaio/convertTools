using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

public sealed class TikTokProjectPayload
{
    public string SourceProjectDir { get; init; } = "";
    public string WorkflowProjectDir { get; init; } = "";
    public string Title { get; init; } = "";
    public string OriginalTitle { get; init; } = "";
    public string Description { get; init; } = "";
    public int EpisodeCount { get; init; } = 1;
    public string TargetAudience { get; init; } = "";
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
}

/// <summary>对齐 Python <c>ai_recommendation_service.py</c>。</summary>
public static class TikTokPublishRecommendationService
{
    private static readonly string[] TargetAudienceModes = ["female", "male", "ai_recommend"];

    private static readonly Regex JsonObjectPattern = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    public static async Task<TikTokPublishRecommendation> BuildRecommendationAsync(
        TikTokProjectPayload payload,
        ClientSettings settings,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var maxCount = TikTokPublishOptions.NormalizeGenreCount(options.GenreCount);
        var targetMode = NormalizeTargetAudienceMode(options.TargetAudienceMode);

        string aiError = "";
        Dictionary<string, JsonElement>? recommendation = null;
        if (string.Equals(targetMode, "ai_recommend", StringComparison.Ordinal))
        {
            try
            {
                recommendation = await RequestAiRecommendationAsync(payload, settings, options, ct);
            }
            catch (Exception ex)
            {
                aiError = ex.Message;
            }
        }

        string targetAudience;
        if (!string.IsNullOrWhiteSpace(payload.TargetAudience))
        {
            targetAudience = payload.TargetAudience;
            log?.Invoke($"TikTok 目标观众使用短剧信息：{TargetAudienceDisplayText(targetAudience)}");
        }
        else if (targetMode is "female" or "male")
        {
            targetAudience = targetMode;
        }
        else
        {
            JsonElement targetElement = default;
            if (recommendation?.TryGetValue("target_audience", out targetElement) == true)
                targetAudience = NormalizeTargetAudience(targetElement);
            else
                targetAudience = "";
            if (string.IsNullOrWhiteSpace(targetAudience))
            {
                log?.Invoke(string.IsNullOrWhiteSpace(aiError)
                    ? "TikTok 目标观众 AI 推荐失败，使用本地规则：AI 返回为空或不在男/女范围内"
                    : $"TikTok 目标观众 AI 推荐失败，使用本地规则: {aiError}");
                targetAudience = HeuristicTargetAudience(payload);
            }
        }

        List<string> genres;
        if (payload.Genres.Count > 0)
        {
            genres = payload.Genres.Take(maxCount).ToList();
            log?.Invoke($"TikTok 题材类型使用短剧信息：{string.Join("、", genres)}");
        }
        else
        {
            JsonElement genreElement = default;
            recommendation?.TryGetValue("genres", out genreElement);
            genres = recommendation is not null && genreElement.ValueKind != JsonValueKind.Undefined
                ? NormalizeGenres(genreElement, maxCount, TikTokPublishConstants.GenreOptions)
                : new List<string>();
            if (genres.Count == 0)
            {
                log?.Invoke(string.IsNullOrWhiteSpace(aiError)
                    ? "TikTok 题材类型 AI 推荐失败，使用本地规则：AI 返回为空或不在真实题材候选内"
                    : $"TikTok 题材类型 AI 推荐失败，使用本地规则: {aiError}");
                genres = HeuristicGenres(payload, maxCount, TikTokPublishConstants.GenreOptions);
            }
        }

        return new TikTokPublishRecommendation
        {
            TargetAudience = targetAudience,
            Genres = genres,
        };
    }

    public static string TargetAudienceDisplayText(string targetAudience) =>
        string.Equals(targetAudience, "male", StringComparison.OrdinalIgnoreCase) ? "男" : "女";

    private static string NormalizeTargetAudienceMode(string? mode)
    {
        var normalized = (mode ?? "ai_recommend").Trim();
        return TargetAudienceModes.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : "ai_recommend";
    }

    private static async Task<Dictionary<string, JsonElement>> RequestAiRecommendationAsync(
        TikTokProjectPayload payload,
        ClientSettings settings,
        TikTokPublishOptions options,
        CancellationToken ct)
    {
        var endpoint = (settings.AiTextEndpoint ?? "").Trim().TrimEnd('/');
        var apiKey = (settings.AiTextApiKey ?? "").Trim();
        var model = (settings.AiTextModel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("AI 文本接口/API Key/模型未配置");

        var maxCount = TikTokPublishOptions.NormalizeGenreCount(options.GenreCount);
        var prompt = JsonSerializer.Serialize(new
        {
            task = "为 TikTok Drama Center 新建剧集表单推荐目标观众和题材类型。",
            rules = new[]
            {
                "target_audience 只能返回 男 或 女。",
                $"genres 只能从 allowed_genres 中选择，返回 {maxCount} 个以内。",
                "只输出 JSON，不要解释。",
            },
            allowed_genres = TikTokPublishConstants.GenreOptions,
            series = new
            {
                title = payload.Title,
                original_title = payload.OriginalTitle,
                description = payload.Description,
                episode_count = payload.EpisodeCount,
            },
            output_schema = new { target_audience = "男|女", genres = new[] { "题材1", "题材2" } },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = "你是短剧发行表单助手，必须严格按候选项输出 JSON。" },
                    new { role = "user", content = prompt },
                },
            }),
            Encoding.UTF8,
            "application/json");

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.AiTextTimeoutSeconds, 10, 600)),
        };
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 接口请求失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {body}");

        using var doc = JsonDocument.Parse(body);
        var content = ExtractChatContent(doc.RootElement);
        using var parsedDoc = JsonDocument.Parse(content);
        if (parsedDoc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AI 返回不是 JSON 对象");

        return parsedDoc.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractChatContent(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AI 接口响应缺少 choices");

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message)) continue;
            if (message.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString() ?? ""
                    : contentElement.GetRawText();
                var match = JsonObjectPattern.Match(content);
                if (match.Success) return match.Value;
                if (!string.IsNullOrWhiteSpace(content)) return content;
            }
        }

        throw new InvalidOperationException("AI 接口未返回内容");
    }

    private static string NormalizeTargetAudience(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return "";

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            _ => value.GetRawText(),
        };
        text = text.Trim().ToLowerInvariant();
        if (text is "female" or "woman" or "women" or "girl" or "girls" or "f" or "女性" or "女")
            return "female";
        if (text is "male" or "man" or "men" or "boy" or "boys" or "m" or "男性" or "男")
            return "male";
        return "";
    }

    private static List<string> NormalizeGenres(JsonElement value, int maxCount, IReadOnlyList<string> genreOptions)
    {
        var rawItems = new List<string>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText();
                rawItems.AddRange(SplitGenreTokens(text));
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            rawItems.AddRange(SplitGenreTokens(value.GetString() ?? ""));
        }

        var allowed = genreOptions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.ToLowerInvariant(), item => item, StringComparer.Ordinal);

        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rawItems)
        {
            var text = item.Trim().Trim('#').Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (!allowed.TryGetValue(text.ToLowerInvariant(), out var normalized) && text.Length >= 2)
            {
                normalized = genreOptions.FirstOrDefault(genre =>
                    text.Contains(genre, StringComparison.Ordinal) ||
                    genre.Contains(text, StringComparison.Ordinal)) ?? "";
            }

            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized)) continue;
            selected.Add(normalized);
            if (selected.Count >= maxCount) break;
        }

        return selected;
    }

    private static IEnumerable<string> SplitGenreTokens(string text) =>
        Regex.Split(text, @"[#,\uFF0C\u3001/\\\s;；]+")
            .Where(part => !string.IsNullOrWhiteSpace(part));

    private static string HeuristicTargetAudience(TikTokProjectPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.TargetAudience))
            return payload.TargetAudience;

        var text = $"{payload.Title} {payload.Description}";
        string[] femaleMarkers = ["总裁", "豪门", "闪婚", "萌宝", "虐恋", "替身", "大女主", "千金", "团宠"];
        string[] maleMarkers = ["赘婿", "逆袭", "神医", "异能", "系统", "商战", "玄幻", "超级英雄"];
        var femaleScore = femaleMarkers.Count(marker => text.Contains(marker, StringComparison.Ordinal));
        var maleScore = maleMarkers.Count(marker => text.Contains(marker, StringComparison.Ordinal));
        return maleScore > femaleScore ? "male" : "female";
    }

    private static List<string> HeuristicGenres(
        TikTokProjectPayload payload,
        int maxCount,
        IReadOnlyList<string> genreOptions)
    {
        if (payload.Genres.Count > 0)
            return payload.Genres.Take(maxCount).ToList();

        var text = $"{payload.Title} {payload.Description} {string.Join(' ', payload.Genres)}";
        var selected = new List<string>();
        foreach (var genre in genreOptions)
        {
            if (!string.IsNullOrWhiteSpace(genre) && text.Contains(genre, StringComparison.Ordinal))
                selected.Add(genre);
            if (selected.Count >= maxCount) return selected;
        }

        foreach (var genre in new[] { "都市", "总裁", "逆袭", "豪门", "重生", "复仇" })
        {
            if (genreOptions.Contains(genre, StringComparer.Ordinal) && !selected.Contains(genre, StringComparer.Ordinal))
                selected.Add(genre);
            if (selected.Count >= maxCount) break;
        }

        foreach (var genre in genreOptions)
        {
            if (!selected.Contains(genre, StringComparer.Ordinal))
                selected.Add(genre);
            if (selected.Count >= maxCount) break;
        }

        return selected;
    }
}
