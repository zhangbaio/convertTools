using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>一段解说脚本行（LLM 产出）。</summary>
public sealed class NarrationLine
{
    public int Index { get; set; }
    public bool KeepOriginal { get; set; }
    public string Narration { get; set; } = "";
    public string Delivery { get; set; } = "build";
    public string Pace { get; set; } = "medium";
    public string Emotion { get; set; } = "narrator";
    public int PauseMs { get; set; }
}

/// <summary>解说选段：按集时长配额覆盖各集(每集保底1)取前 50%，按播放顺序返回。移植自 CommentaryClipMode.select。</summary>
public static class Commentary
{
    public static List<ClipCandidate> Select(IReadOnlyList<ClipCandidate> all, IReadOnlyDictionary<int, double> epDur, Action<string>? log)
    {
        if (all.Count == 0) return new List<ClipCandidate>();
        int totalKeep = Math.Max(1, (int)Math.Round(all.Count * 0.5));
        var byEp = all.GroupBy(c => c.EpisodeIndex).ToDictionary(g => g.Key, g => g.ToList());

        List<ClipCandidate> chosen;
        if (epDur is { Count: > 0 } && byEp.Count > 1)
        {
            var quota = HighlightPlanner.AllocateEpisodeQuota(byEp, epDur, totalKeep);
            chosen = new List<ClipCandidate>();
            foreach (var (ep, items) in byEp)
            {
                int k = quota.TryGetValue(ep, out var q) ? q : 0;
                if (k > 0) chosen.AddRange(items.OrderByDescending(c => c.Total).Take(k));
            }
        }
        else
        {
            chosen = all.OrderByDescending(c => c.Total).Take(totalKeep).ToList();
        }
        chosen = chosen.OrderBy(c => c.EpisodeIndex).ThenBy(c => c.StartMs).ToList();
        log?.Invoke($"🎙️ 解说选段：{chosen.Count} 段（按播放顺序）");
        return chosen;
    }
}

/// <summary>LLM 解说脚本生成 + 旁白比例校准 + TTS 语速换算。移植自 material_clip commentary_prompting/generation/render_tts。
/// v1：单批请求（不分批/并发/缓存）。</summary>
public sealed class CommentaryScripter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public async Task<List<NarrationLine>> BuildAsync(List<ClipCandidate> segs, ClipEngineOptions opts, string titleHint, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.AiEndpoint) || string.IsNullOrWhiteSpace(opts.AiApiKey) || string.IsNullOrWhiteSpace(opts.AiModel))
            throw new Exception("未配置 AI 文本接口（解说需 LLM 生成脚本）");

        var endpoint = opts.AiEndpoint.Trim().TrimEnd('/');
        int keepPct = Math.Clamp((int)Math.Round(100 - opts.CommentaryNarrationRatio), 0, 100);
        var content = await RequestAsync(endpoint, opts, BuildPrompt(titleHint, segs, keepPct), ct);
        var lines = ParseLines(content, segs.Count);
        EnforceRatio(lines, segs, opts.CommentaryNarrationRatio);
        log?.Invoke($"📝 解说脚本：{lines.Count} 段（旁白 {lines.Count(l => !l.KeepOriginal)} / 原声 {lines.Count(l => l.KeepOriginal)}）");
        return lines;
    }

    // delivery/pace/emotion + 风格强度 → TTS 语速比（0.78~1.25）。移植自 commentary_render_tts。
    public static double SpeedRatio(NarrationLine line, ClipEngineOptions opts)
    {
        double styleFactor = (opts.CommentaryStyleStrength ?? "standard").Trim().ToLowerInvariant() switch
        {
            "subtle" => 0.82, "strong" => 1.22, _ => 1.0,
        };
        double pace = line.Pace switch { "slow" => -0.08, "fast" => 0.08, _ => 0.0 };
        double delivery = line.Delivery switch
        {
            "hook" => 0.04, "turn" => 0.03, "climax" => 0.06, "suspense" => -0.06, "release" => -0.03, _ => 0.0,
        };
        double emo = line.Emotion switch
        {
            "narrator_immersive" => -0.01, "serious" => -0.02, "happy" => 0.01, "sad" => -0.04, "angry" => 0.02, _ => 0.0,
        };
        double baseSpeed = opts.TtsSpeedRatio <= 0 ? 1.0 : opts.TtsSpeedRatio;
        return Math.Clamp(baseSpeed + (pace + delivery + emo) * styleFactor, 0.78, 1.25);
    }

    private static string BuildPrompt(string titleHint, List<ClipCandidate> segs, int keepPct)
    {
        var clips = segs.Select((c, i) => new { index = i + 1, subtitle = c.Text }).ToArray();
        return
            $"剧名/主题：{titleHint}\n\n" +
            "下面是按播放顺序排列的若干片段（含原字幕）。请为每个片段写一句到几句口播解说，整体连贯成一条短剧解说：" +
            "第 1 段开头要抓人，最后一段收尾留悬念；语言口语化、信息密度高，不要逐字复述字幕，而是讲清楚冲突/人物关系/转折。" +
            "每段解说尽量控制在 1~2 句；单句尽量短，优先 8~22 个字。" +
            $"本条整体走「旁白为主」：关键段(keep_original)只占约 {keepPct}%。" +
            "无论 keep_original 是 true 还是 false，每段都要写出 narration 解说文案。" +
            "严格输出 JSON：{\"lines\":[{\"index\":<序号>,\"keep_original\":<true/false>,\"narration\":\"<文案>\"," +
            "\"delivery\":\"<hook/build/turn/climax/suspense/release>\",\"pace\":\"<slow/medium/fast>\"," +
            "\"emotion\":\"<narrator/narrator_immersive/serious/happy/sad/angry>\",\"pause_ms\":<0-350>}]}，" +
            "lines 数量与片段数一致、index 一一对应。\n\n片段列表：\n" + JsonSerializer.Serialize(clips);
    }

    private async Task<string> RequestAsync(string endpoint, ClipEngineOptions opts, string prompt, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = opts.AiModel,
            temperature = 0.55,
            messages = new object[]
            {
                new { role = "system", content = "你是资深短剧解说口播作者，只输出 JSON。语言口语、短句、有停顿感、开头抓人、结尾留悬念。" },
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
        if (!resp.IsSuccessStatusCode) throw new Exception($"解说 LLM HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static List<NarrationLine> ParseLines(string content, int count)
    {
        var byIndex = new Dictionary<int, NarrationLine>();
        using var doc = JsonDocument.Parse(ExtractJson(content));
        var root = doc.RootElement;
        JsonElement arr = default;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array) arr = l;
        else if (root.ValueKind == JsonValueKind.Array) arr = root;

        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int idx = e.TryGetProperty("index", out var ie) && ie.TryGetInt32(out var iv) ? iv : 0;
                if (idx <= 0) continue;
                byIndex[idx] = new NarrationLine
                {
                    Index = idx,
                    KeepOriginal = e.TryGetProperty("keep_original", out var k) && k.ValueKind == JsonValueKind.True,
                    Narration = Str(e, "narration"),
                    Delivery = Str(e, "delivery", "build"),
                    Pace = Str(e, "pace", "medium"),
                    Emotion = Str(e, "emotion", "narrator"),
                    PauseMs = e.TryGetProperty("pause_ms", out var p) && p.TryGetInt32(out var pv) ? Math.Clamp(pv, 0, 350) : 0,
                };
            }

        var result = new List<NarrationLine>();
        for (int i = 1; i <= count; i++)
            result.Add(byIndex.TryGetValue(i, out var nl) ? nl : new NarrationLine { Index = i, KeepOriginal = true });
        return result;
    }

    // 校准旁白占比：原声段过多→分数最低者翻解说；过少→分数最高解说翻原声。
    private static void EnforceRatio(List<NarrationLine> lines, List<ClipCandidate> segs, double narrationRatio)
    {
        int total = lines.Count;
        int targetKeep = (int)Math.Round((1.0 - Math.Clamp(narrationRatio, 0, 100) / 100.0) * total);
        int curKeep = lines.Count(l => l.KeepOriginal);
        double Score(NarrationLine l) => (l.Index - 1 >= 0 && l.Index - 1 < segs.Count) ? segs[l.Index - 1].Total : 0;

        if (curKeep > targetKeep)
            foreach (var l in lines.Where(l => l.KeepOriginal).OrderBy(Score).Take(curKeep - targetKeep))
            { if (!string.IsNullOrWhiteSpace(l.Narration)) l.KeepOriginal = false; }
        else if (curKeep < targetKeep)
            foreach (var l in lines.Where(l => !l.KeepOriginal).OrderByDescending(Score).Take(targetKeep - curKeep))
                l.KeepOriginal = true;
    }

    private static string Str(JsonElement e, string name, string def = "")
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;

    private static string ExtractJson(string content)
    {
        var t = (content ?? "").Trim();
        if (t.StartsWith("```"))
        {
            int nl = t.IndexOf('\n'); if (nl >= 0) t = t[(nl + 1)..];
            int f = t.LastIndexOf("```", StringComparison.Ordinal); if (f >= 0) t = t[..f];
            t = t.Trim();
        }
        int os = t.IndexOf('{'), oe = t.LastIndexOf('}'); if (os >= 0 && oe > os) return t[os..(oe + 1)];
        int a = t.IndexOf('['), ae = t.LastIndexOf(']'); if (a >= 0 && ae > a) return t[a..(ae + 1)];
        return t;
    }
}
