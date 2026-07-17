using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>LLM 复评分：整集批量请求 OpenAI 兼容接口，重打 4 维分并与关键词分 25/75 融合、重算 total，
/// 同时回填 summary/title/recommend_reason/tags 供发表文案。移植自 material_clip/llm_scoring.py。
/// 单集失败降级保留关键词分，不中断整体。</summary>
public sealed class LlmScorer
{
    private const int MaxPerEpisode = 8;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task ApplyAsync(IReadOnlyList<ClipCandidate> candidates, ClipEngineOptions opts, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.AiEndpoint) || string.IsNullOrWhiteSpace(opts.AiApiKey) || string.IsNullOrWhiteSpace(opts.AiModel))
            throw new Exception("未配置 AI 文本接口（endpoint/apiKey/model）");

        var endpoint = opts.AiEndpoint.Trim().TrimEnd('/');
        foreach (var group in candidates.GroupBy(c => c.EpisodeIndex))
        {
            ct.ThrowIfCancellationRequested();
            var ranked = group.OrderByDescending(c => c.Total).Take(MaxPerEpisode).ToList();
            if (ranked.Count == 0) continue;
            log?.Invoke($"🧠 AI 复评分：第 {group.Key} 集 {ranked.Count} 个候选（批量）");

            Dictionary<int, SceneScore> byIndex;
            try
            {
                var content = await RequestAsync(endpoint, opts, BuildPrompt(group.Key, ranked), ct);
                byIndex = ParseScenes(content);
            }
            catch (Exception ex)
            {
                log?.Invoke($"⚠️ 第 {group.Key} 集 AI 复评分失败，保留关键词分：{ex.Message}");
                continue;
            }
            for (int i = 0; i < ranked.Count; i++)
                if (byIndex.TryGetValue(i + 1, out var s)) ApplyScene(ranked[i], s);
        }
    }

    private static string BuildPrompt(int episode, List<ClipCandidate> items)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            var c = items[i];
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(Srt.ToClock(c.StartMs)).Append(" --> ").Append(Srt.ToClock(c.EndMs))
              .Append('\n').Append(c.Text);
            if (i < items.Count - 1) sb.Append("\n\n");
        }
        return
            $"你是短剧投流剪辑师。下面是某剧第 {episode} 集的 {items.Count} 个候选场景的字幕（带编号和时间戳）。" +
            "请为【每个】场景打分，输出严格 JSON 对象，形如 " +
            "{\"scenes\":[{\"index\":1,\"conflict\":8,\"twist\":7,\"emotion\":6,\"cliffhanger\":9," +
            "\"summary\":\"<一句话>\",\"title\":\"<18字内>\",\"recommend_reason\":\"<一句话>\",\"tags\":[\"<标签>\"]}]}。" +
            "评分均 0-10：conflict=吵架/打斗/对峙强度；twist=身份揭示/剧情突变；" +
            "emotion=哭/吼/告白/亲密；cliffhanger=结尾留白/疑问/中断。" +
            "必须为每个编号都返回一项，index 与场景编号一致。\n\n" +
            $"场景：\n{sb}";
    }

    private async Task<string> RequestAsync(string endpoint, ClipEngineOptions opts, string prompt, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = opts.AiModel,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = "You are an expert short-drama clip editor." },
                new { role = "user", content = prompt },
            },
            response_format = new { type = "json_object" },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opts.AiApiKey}");
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new Exception($"AI HTTP {(int)resp.StatusCode}: {Trim(text)}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static void ApplyScene(ClipCandidate c, SceneScore s)
    {
        c.Conflict = Blend(c.Conflict, s.Conflict);
        c.Twist = Blend(c.Twist, s.Twist);
        c.Emotion = Blend(c.Emotion, s.Emotion);
        c.Cliffhanger = Blend(c.Cliffhanger, s.Cliffhanger);
        if (!string.IsNullOrWhiteSpace(s.Summary)) c.Summary = s.Summary.Trim();
        if (!string.IsNullOrWhiteSpace(s.Title)) { var t = s.Title.Trim(); c.Title = t.Length > 24 ? t[..24] : t; }
        if (!string.IsNullOrWhiteSpace(s.RecommendReason)) c.RecommendReason = s.RecommendReason.Trim();
        if (s.Tags.Count > 0) c.Tags = s.Tags;
        c.Total = Math.Round(0.4 * c.Conflict + 0.25 * c.Twist + 0.25 * c.Emotion + 0.10 * c.Cliffhanger, 2);
    }

    // LLM 主评分(0.75) + 关键词分先验(0.25)，四舍五入到整数；ai 缺失则保留原分。
    private static double Blend(double baseScore, int? ai)
    {
        if (ai is null) return baseScore;
        int v = Math.Max(0, Math.Min(10, ai.Value));
        return Math.Round(baseScore * 0.25 + v * 0.75);
    }

    private static Dictionary<int, SceneScore> ParseScenes(string content)
    {
        var map = new Dictionary<int, SceneScore>();
        using var doc = JsonDocument.Parse(ExtractJsonObject(content));
        var root = doc.RootElement;
        JsonElement scenes;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("scenes", out var s) && s.ValueKind == JsonValueKind.Array)
            scenes = s;
        else if (root.ValueKind == JsonValueKind.Array)
            scenes = root;
        else return map;

        foreach (var e in scenes.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("index", out var idxEl)) continue;
            int idx;
            if (idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var iv)) idx = iv;
            else if (idxEl.ValueKind == JsonValueKind.String && int.TryParse(idxEl.GetString(), out var sv)) idx = sv;
            else continue;
            map[idx] = new SceneScore
            {
                Conflict = ReadInt(e, "conflict"),
                Twist = ReadInt(e, "twist"),
                Emotion = ReadInt(e, "emotion"),
                Cliffhanger = ReadInt(e, "cliffhanger"),
                Summary = ReadStr(e, "summary"),
                Title = ReadStr(e, "title"),
                RecommendReason = ReadStr(e, "recommend_reason"),
                Tags = ReadTags(e, "tags"),
            };
        }
        return map;
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out var i) ? i : (int)Math.Round(el.GetDouble());
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static string ReadStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static List<string> ReadTags(JsonElement obj, string name)
    {
        var list = new List<string>();
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
            foreach (var t in el.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String)
                {
                    var v = (t.GetString() ?? "").Trim().TrimStart('#');
                    if (v.Length > 0 && !list.Contains(v)) list.Add(v);
                }
        return list;
    }

    // 从可能带 ```json 围栏或前后文的内容里抽出 JSON 对象/数组。
    private static string ExtractJsonObject(string content)
    {
        var t = (content ?? "").Trim();
        if (t.StartsWith("```"))
        {
            int nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            int fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) t = t[..fence];
            t = t.Trim();
        }
        int objStart = t.IndexOf('{'), objEnd = t.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart) return t[objStart..(objEnd + 1)];
        int arrStart = t.IndexOf('['), arrEnd = t.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart) return t[arrStart..(arrEnd + 1)];
        return t;
    }

    private static string Trim(string s) { s = (s ?? "").Trim(); return s.Length <= 300 ? s : s[..300]; }

    private sealed class SceneScore
    {
        public int? Conflict { get; init; }
        public int? Twist { get; init; }
        public int? Emotion { get; init; }
        public int? Cliffhanger { get; init; }
        public string Summary { get; init; } = "";
        public string Title { get; init; } = "";
        public string RecommendReason { get; init; } = "";
        public List<string> Tags { get; init; } = new();
    }
}
