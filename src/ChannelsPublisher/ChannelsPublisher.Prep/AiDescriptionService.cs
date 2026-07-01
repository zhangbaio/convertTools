using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Prep;

/// <summary>AI 视频描述生成（OpenAI 兼容 chat/completions，如豆包 ark）。
/// 移植自 Python publish_ai_description.generator：system+user 消息、json_object、取 description。</summary>
public sealed class AiDescriptionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> GenerateAsync(PrepConfig cfg, string title, string baseDescription, CancellationToken ct)
    {
        if (!cfg.AiEnabled || string.IsNullOrWhiteSpace(cfg.AiEndpoint) ||
            string.IsNullOrWhiteSpace(cfg.AiApiKey) || string.IsNullOrWhiteSpace(cfg.AiModel))
            return baseDescription;

        var payload = new
        {
            model = cfg.AiModel,
            temperature = 0.7,
            messages = new object[]
            {
                new { role = "system", content = "你是短视频发布文案助手，擅长把短剧简介与既有文案改写成新的发表描述。" },
                new { role = "user", content = BuildPrompt(title, baseDescription) },
            },
            response_format = new { type = "json_object" },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.AiEndpoint.TrimEnd('/') + "/chat/completions");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.AiApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"AI 描述失败 {(int)resp.StatusCode}：{body}");

        var content = ExtractChatContent(body);
        var desc = ExtractDescription(content);
        return string.IsNullOrWhiteSpace(desc) ? baseDescription : desc.Trim();
    }

    private static string BuildPrompt(string title, string baseDescription)
        => $"剧名：{title}\n原文案：{baseDescription}\n" +
           "请改写成一段新的视频号发表描述（口语化、吸引人，可含少量话题标签），" +
           "只返回 JSON：{\"description\": \"...\"}";

    private static string ExtractChatContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static string ExtractDescription(string content)
    {
        // content 可能直接是 JSON，也可能包在 ```json ... ``` 里
        var text = content.Trim();
        int lb = text.IndexOf('{'), rb = text.LastIndexOf('}');
        if (lb >= 0 && rb > lb) text = text.Substring(lb, rb - lb + 1);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("description", out var d)) return d.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("caption", out var c)) return c.GetString() ?? "";
        }
        catch { /* 非 JSON → 原样返回内容 */ }
        return content.Trim();
    }
}
