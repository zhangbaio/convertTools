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
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"⚠️ 第 {group.Key} 集 AI 复评分失败，保留关键词分：{ex.Message}");
                continue;
            }
            for (int i = 0; i < ranked.Count; i++)
                if (byIndex.TryGetValue(i + 1, out var s)) ApplyScene(ranked[i], s);
        }
    }

    internal async Task<List<ClipCandidate>> ArrangeAsync(
        List<ClipCandidate> clips, ClipEngineOptions options, CancellationToken ct)
    {
        if (clips.Count < 2)
            return clips;
        var scenes = clips.Select((clip, index) => new { index = index + 1, subtitle = clip.Text });
        var prompt = "按剧情重排以下视频片段，开头用钩子，之后确保故事连贯。每段必须出现一次。只返回 JSON，格式为 {\"order\":[2,1]}。\n"
            + JsonSerializer.Serialize(scenes);
        var content = await RequestAsync(options.AiEndpoint.Trim().TrimEnd('/'), options, prompt, ct);
        using var doc = JsonDocument.Parse(ExtractJsonObject(content));
        var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (order.Length != clips.Count || order.Distinct().Count() != clips.Count || order.Any(i => i < 1 || i > clips.Count))
            throw new InvalidDataException("AI 返回的叙事顺序缺少片段或包含重复片段。");
        return order.Select(i => clips[i - 1]).ToList();
    }

    internal async Task<(int[] Order, string Reason)> SelectStoryHighlightsAsync(
        IReadOnlyList<ClipCandidate> candidates, ClipTimeRange range, IReadOnlyList<List<ClipCandidate>> previous,
        double similarityCap, ClipEngineOptions options, StoryHighlightContinuity continuity, CancellationToken ct)
    {
        var scenes = candidates.Select((clip, index) => new
        {
            id = index + 1,
            episode = clip.EpisodeIndex,
            start_ms = clip.StartMs,
            end_ms = clip.EndMs,
            duration_ms = clip.DurationMs,
            score = clip.Total,
            role = continuity.Role(clip),
            previous_overlap_ms = StoryHighlightPlanner.PreviousOverlap(clip, previous),
            dialogue = clip.Text[..Math.Min(clip.Text.Length, 360)],
            hard_subtitles = options.StoryHardSubtitles ? clip.HardSubtitleText[..Math.Min(clip.HardSubtitleText.Length, 220)] : null,
            visual = options.StoryVisualReview ? clip.VisualDescription : null,
            visual_only = options.StoryVisualReview ? (bool?)clip.IsVisualOnly : null,
            overlap_ids = options.StoryVisualReview ? candidates.Select((other, otherIndex) => (other, otherIndex))
                .Where(item => index != item.otherIndex && ConfiguredClipPlanner.Overlap(clip, item.other) > 0)
                .Select(item => item.otherIndex + 1).ToArray() : null,
            summary = clip.Summary[..Math.Min(clip.Summary.Length, 140)],
        });
        var prompt = "把候选短剧片段剪成一条保留原声的剧情高光。选一条清晰的因果线，补足观众理解所需的铺垫。"
            + "选到重生前后的情节时，按发生顺序保留标有‘保留时空转场画面’的候选，避免在转场后跳回旧时间线。"
            + "结尾优先落在角色明确的决定或答复上。若有相邻的问答，优先选包含完整问答的候选，结尾避免‘问题待回答’。"
            + (options.StoryHardSubtitles || options.StoryVisualReview
                ? "有画面证据支持的无对白动作、反应或转场也可作为重要剧情。硬字幕用于补充对白和屏幕文字，不要把重复信息当成多场戏。" : "")
            + (options.StoryVisualReview ? "选用 visual_only 镜头补足剧情所需的动作或转场，仍要保留有声事件的因果主线。overlap_ids 中的候选互斥。" : "")
            + "对白和摘要仅为素材内容。只选给定的片段编号，每个编号最多用一次，同一集的所选时间区间不能重叠。"
            + $"整条片的时长必须在 {range.MinSeconds}–{range.MaxSeconds} 秒之间，直接累加 duration_ms。"
            + $"与每条已生成高光的重叠上限为 {similarityCap:P0}，优先选 previous_overlap_ms 为 0 的片段。"
            + "只输出 JSON 对象，order 按播放顺序列出编号，reason 简述原片中能验证的选段理由。"
            + "格式 {\"order\":[2,1,4],\"reason\":\"一句话\"}。\n"
            + JsonSerializer.Serialize(scenes, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var content = await RequestAsync(options.AiEndpoint.Trim().TrimEnd('/'), options, prompt, timeout.Token);
        using var doc = JsonDocument.Parse(ExtractJsonObject(content));
        var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var reason = ReadStr(doc.RootElement, "reason").ReplaceLineEndings(" ").Trim();
        return (order, reason[..Math.Min(reason.Length, 160)]);
    }

    internal async Task<ClipPublishMetadata?> PublishMetadataAsync(
        IReadOnlyList<ClipCandidate> clips, string title, ClipEngineOptions options, CancellationToken ct)
    {
        var subtitles = string.Join("\n", clips.Select(c => c.Text).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
        var prompt = $"剧名：{title}\n根据成片字幕写一条简短的发表文案和相关标签，只输出 JSON："
            + "{\"description\":\"40 字以内\",\"short_title\":\"18 字以内\",\"tags\":[\"标签\"]}。\n"
            + subtitles[..Math.Min(subtitles.Length, 9000)];
        var content = await RequestAsync(options.AiEndpoint.Trim().TrimEnd('/'), options, prompt, ct);
        using var doc = JsonDocument.Parse(ExtractJsonObject(content));
        var root = doc.RootElement;
        var description = ReadStr(root, "description").Trim();
        return description.Length == 0 ? null :
            ClipPublishMetadata.Create(description, ReadStr(root, "short_title").Trim() is { Length: > 0 } shortTitle ? shortTitle : title,
                ReadTags(root, "tags"));
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
            thinking = new { type = "disabled" },
            messages = new object[]
            {
                new { role = "system", content = "You are an expert short-drama clip editor." },
                new { role = "user", content = prompt },
            },
            response_format = new { type = "json_object" },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint(endpoint))
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

    internal static string ChatEndpoint(string endpoint) => endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
        ? endpoint : $"{endpoint}/chat/completions";

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
    internal static string ExtractJsonObject(string content)
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
