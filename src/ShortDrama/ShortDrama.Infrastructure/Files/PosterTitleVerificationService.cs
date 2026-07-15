using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShortDrama.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Files;

internal sealed class PosterTitleVerificationService
{
    private const string DefaultVerifyPrompt = """
请检查这张短剧海报标题区域中的主标题文字，并只返回 JSON。
目标标题：{title}
要求：
1. 识别海报中主标题的实际文字。
2. 判断是否与目标标题逐字完全一致。
3. 判断是否包含繁体字、异体字、错别字。
4. 判断字体是否清晰、规整、易读，还是明显手写体/艺术字/变形字。
5. 判断字体是否属于常见中文标题字风格；如果略带海报风格但不影响识别，也可以视为通过。
6. 判断描边和装饰是否克制、审核友好，而不是明显设计化。
7. 如果某个字虽然看起来接近目标字，但字形像异体字、艺术字、错字或不规范写法，也必须判定为不通过。
8. 识别结果必须尽量逐字；风格判断可以适度宽松，但字形正确性不能放宽。

JSON 格式：
{
  "detectedTitle": "识别到的标题",
  "matchesTarget": true,
  "containsTraditional": false,
  "containsVariant": false,
  "usesArtisticStyle": false,
  "isReadablePrintStyle": true,
  "looksLikeStandardSansTitle": true,
  "usesAggressiveDecorations": false,
  "reason": "一句简短说明"
}

只返回 JSON，不要解释，不要 Markdown。
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public PosterTitleVerificationService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<PosterTitleVerifyResult> VerifyAsync(
        IReadOnlyDictionary<string, string> config,
        string imagePath,
        string title,
        PosterTitleLayout layout,
        CancellationToken cancellationToken)
    {
        try
        {
            var cropPath = CreateTitleCrop(imagePath, layout);
            try
            {
                var endpoint = Require(config, "ChatModelEndpoint").TrimEnd('/');
                var modelId = Require(config, "ChatModelId");
                var apiKey = Require(config, "ChatModelApiKey");
                var prompt = RenderPrompt(
                    config.GetValueOrDefault("PosterVerifyPrompt") ?? DefaultVerifyPrompt,
                    title);
                var mediaType = GuessMediaType(Path.GetExtension(cropPath));
                var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(cropPath, cancellationToken));

                var payload = new
                {
                    model = modelId,
                    temperature = 0.0,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = prompt },
                                new
                                {
                                    type = "image_url",
                                    image_url = new { url = $"data:{mediaType};base64,{imageBase64}" },
                                },
                            },
                        },
                    },
                };

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
                var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return PosterTitleVerifyResult.Fail(
                        AiApiErrorMessage.Create("AI 海报标题校验接口", response.StatusCode, response.ReasonPhrase, responseText));
                }

                var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions);
                var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
                if (string.IsNullOrWhiteSpace(content))
                    return PosterTitleVerifyResult.Fail("标题校验未返回内容");

                var json = ExtractJsonObject(content);
                if (string.IsNullOrWhiteSpace(json))
                    return PosterTitleVerifyResult.Fail($"标题校验未返回合法 JSON：{content}");

                var result = JsonSerializer.Deserialize<PosterTitleVerifyPayload>(json, JsonOptions)
                    ?? new PosterTitleVerifyPayload();
                return NormalizeResult(result, title);
            }
            finally
            {
                TryDelete(cropPath);
            }
        }
        catch (Exception ex)
        {
            return PosterTitleVerifyResult.Fail($"标题校验接口失败：{ex.Message}");
        }
    }

    private static PosterTitleVerifyResult NormalizeResult(PosterTitleVerifyPayload result, string title)
    {
        var detectedTitle = NormalizeDetectedTitle(result.DetectedTitle);
        var matchesTarget = result.MatchesTarget == true && detectedTitle == title;
        if (result.ContainsTraditional == true)
            matchesTarget = false;
        if (result.ContainsVariant == true)
            matchesTarget = false;
        if (!string.IsNullOrWhiteSpace(detectedTitle) && detectedTitle != title)
            matchesTarget = false;
        if (matchesTarget && string.IsNullOrWhiteSpace(detectedTitle))
            matchesTarget = false;

        var reason = (result.Reason ?? "").Trim();
        if (!matchesTarget && string.IsNullOrWhiteSpace(reason))
        {
            reason = string.IsNullOrWhiteSpace(detectedTitle)
                ? "未检测到主标题文字"
                : $"识别标题为“{detectedTitle}”";
        }

        return new PosterTitleVerifyResult(matchesTarget, detectedTitle, reason);
    }

    private static string CreateTitleCrop(string imagePath, PosterTitleLayout layout)
    {
        using var source = Image.Load<Rgba32>(imagePath);
        var width = source.Width;
        var height = source.Height;
        var rx = Math.Max(0, (int)Math.Round(width * layout.X));
        var ry = Math.Max(0, (int)Math.Round(height * layout.Y));
        var rw = Math.Min(width - rx, (int)Math.Round(width * layout.Width));
        var rh = Math.Min(height - ry, (int)Math.Round(height * layout.Height));
        var padX = Math.Max(24, (int)Math.Round(rw * 0.2));
        var padY = Math.Max(32, (int)Math.Round(rh * 1.2));
        var left = Math.Max(0, rx - padX);
        var top = Math.Max(0, ry - padY);
        var right = Math.Min(width, rx + rw + padX);
        var bottom = Math.Min(height, ry + rh + padY);
        var cropRect = new Rectangle(left, top, right - left, bottom - top);
        using var cropped = source.CloneAs<Rgba32>();
        cropped.Mutate(ctx => ctx.Crop(cropRect));
        var cropPath = Path.Combine(Path.GetTempPath(), $"poster-crop-{Guid.NewGuid():N}.png");
        cropped.SaveAsPng(cropPath);
        return cropPath;
    }

    private static string NormalizeDetectedTitle(string? value)
    {
        var text = (value ?? "").Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "");
        foreach (var prefix in new[] { "标题：", "标题:", "主标题：", "主标题:" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..].Trim();
        }

        return text;
    }

    private static string RenderPrompt(string template, string title) =>
        template.Replace("{title}", title, StringComparison.Ordinal);

    private static string GuessMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    private static string Require(IReadOnlyDictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"配置缺少必填字段: {key}");

    private static string? ExtractJsonObject(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        return start < 0 || end <= start ? null : value[start..(end + 1)];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private sealed record ChatCompletionResponse(IReadOnlyList<Choice>? Choices);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content);

    private sealed class PosterTitleVerifyPayload
    {
        [JsonPropertyName("detectedTitle")]
        public string? DetectedTitle { get; set; }

        [JsonPropertyName("matchesTarget")]
        public bool? MatchesTarget { get; set; }

        [JsonPropertyName("containsTraditional")]
        public bool? ContainsTraditional { get; set; }

        [JsonPropertyName("containsVariant")]
        public bool? ContainsVariant { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}

internal readonly record struct PosterTitleVerifyResult(bool Ok, string DetectedTitle, string Reason)
{
    public static PosterTitleVerifyResult Fail(string reason) => new(false, "", reason);
}

internal static class PosterTitleVerifyModeHelper
{
    public static string Normalize(string? value)
    {
        var normalized = (value ?? "fallback_repaint").Trim().ToLowerInvariant();
        return normalized switch
        {
            "fallback" or "repaint" or "fallback_repaint" or "erase_repaint" => "fallback_repaint",
            "image2" or "image2_regenerate" or "regenerate_image2" or "ofox_image2" => "image2_regenerate",
            _ => normalized is "blocking" or "warn" or "fallback_repaint" or "image2_regenerate"
                ? normalized
                : "fallback_repaint",
        };
    }
}
