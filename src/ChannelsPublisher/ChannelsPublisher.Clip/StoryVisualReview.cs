using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

internal static class StoryVisualReview
{
    private const int BatchSize = 6;
    private const int MaxSilentShots = 12;
    private const int MaxDialogueShots = 6;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public static async Task<int> ApplyAsync(IReadOnlyList<ClipSourceAnalysis> sources, ClipEngineOptions options,
        Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.StoryVisionEndpoint) || string.IsNullOrWhiteSpace(options.StoryVisionModel))
        {
            log?.Invoke("画面复评未配置视觉接口，使用现有高光评分。");
            return 0;
        }

        var silent = sources.SelectMany(source => SilentShots(source).Take(2)).Take(MaxSilentShots).ToArray();
        var voiced = sources.SelectMany(source => source.Candidates.OrderByDescending(clip => clip.Total).Take(1)).Take(MaxDialogueShots);
        var nominations = silent.Concat(voiced).ToArray();
        var batches = nominations.Chunk(BatchSize).ToArray();
        var reviewed = 0;
        var admitted = 0;
        var folder = Path.Combine(Path.GetTempPath(), "clip-vision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var batch in batches)
            {
                try
                {
                    var scores = await ReviewBatchAsync(batch, folder, options, ct);
                    foreach (var (id, score, summary) in scores)
                    {
                        var clip = batch[id - 1];
                        reviewed++;
                        if (clip.IsVisualOnly)
                        {
                            if (score < 6 || summary.Length == 0) continue;
                            clip.Total = Math.Round(score * 0.65, 2);
                            clip.VisualDescription = summary;
                            sources.First(source => source.Source.VideoPath == clip.VideoPath).Candidates.Add(clip);
                            admitted++;
                        }
                        else
                        {
                            clip.Total = Math.Round(clip.Total * 0.75 + score * 0.25, 2);
                            clip.VisualDescription = summary;
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log?.Invoke(ex is OperationCanceledException ? "画面复评超时，保留已取得的评分。" : $"画面复评未完成，保留现有评分。{ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        log?.Invoke($"画面复评完成 {reviewed}/{nominations.Length} 段，纳入 {admitted} 段少对白或无对白镜头。");
        return reviewed;
    }

    internal static IEnumerable<ClipCandidate> SilentShots(ClipSourceAnalysis source)
    {
        var timeline = new HighlightCutTimeline(source);
        var cuts = source.ShotCuts.Prepend(0).Append(source.Media.DurationMs).Distinct().Order().ToArray();
        var anchors = source.Candidates.OrderByDescending(clip => clip.Total).Take(4).ToArray();
        var candidates = new List<(ClipCandidate Clip, bool ConnectsDialogue, int Distance)>();
        for (var index = 1; index < cuts.Length; index++)
        {
            var start = cuts[index - 1];
            var end = Math.Min(cuts[index], start + 8000);
            if (end - start < 1500 || source.Dialogue.Any(line =>
                line.Text.Count(char.IsLetterOrDigit) >= 3 && Math.Min(line.EndMs, end) - Math.Max(line.StartMs, start) > 600))
                continue;
            if (timeline.Fit(start, end, end - start, end - start, 1500) is not { } safe)
                continue;

            var distance = anchors.Select(anchor => Math.Max(0, Math.Max(safe.Start - anchor.EndMs, anchor.StartMs - safe.End)))
                .DefaultIfEmpty(0).Min();
            var connectsDialogue = source.Candidates.Any(clip => clip.Text.Length > 0 && clip.EndMs <= safe.Start && safe.Start - clip.EndMs <= 4000)
                && source.Candidates.Any(clip => clip.Text.Length > 0 && clip.StartMs >= safe.End && clip.StartMs - safe.End <= 4000);
            candidates.Add((new ClipCandidate
            {
                EpisodeIndex = source.Source.EpisodeIndex, VideoPath = source.Source.VideoPath,
                StartMs = safe.Start, EndMs = safe.End, IsVisualOnly = true,
            }, connectsDialogue, distance));
        }
        return candidates.OrderByDescending(item => item.ConnectsDialogue).ThenBy(item => item.Distance)
            .ThenByDescending(item => item.Clip.DurationMs).Select(item => item.Clip);
    }

    private static async Task<IReadOnlyList<(int Id, double Score, string Summary)>> ReviewBatchAsync(
        IReadOnlyList<ClipCandidate> batch, string folder, ClipEngineOptions options, CancellationToken ct)
    {
        var content = new List<object>
        {
            new { type = "text", text = "复评短剧候选。每段附两个按时间排序的原片画面。"
                + "只描述可看见的动作、表情、屏幕时间字样或镜头变化。无对白也可能是转场或情绪高光。"
                + "片头水印、黑屏和单纯空镜给低分。不可根据文本推断画面未出现的行为。"
                + "逐编号返回 JSON 对象：{\"scenes\":[{\"id\":1,\"score\":8,\"summary\":\"可核对的画面描述\"}]}。score 为 0–10。" },
        };
        for (var index = 0; index < batch.Count; index++)
        {
            var clip = batch[index];
            content.Add(new { type = "text", text = $"候选 {index + 1}，第 {clip.EpisodeIndex} 集。"
                + (clip.IsVisualOnly ? "少对白或无对白。" : "含对白，按画面复评。") });
            foreach (var fraction in new[] { 0.3, 0.72 })
            {
                var seconds = ((clip.StartMs + clip.DurationMs * fraction) / 1000).ToString("0.###", CultureInfo.InvariantCulture);
                var path = Path.Combine(folder, "sample.jpg");
                await Ffmpeg.RunAsync(options.FfmpegPath, ["-y", "-hide_banner", "-loglevel", "error", "-threads", "1", "-ss", seconds,
                    "-i", clip.VideoPath, "-an", "-sn", "-filter_threads", "1", "-frames:v", "1", "-vf",
                    "scale=460:700:force_original_aspect_ratio=decrease:force_divisible_by=2", "-q:v", "4", "-threads", "1", path], ct);
                var image = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct));
                content.Add(new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + image } });
            }
        }

        var endpoint = LlmScorer.ChatEndpoint(options.StoryVisionEndpoint.Trim().TrimEnd('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(options.StoryVisionApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StoryVisionApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = options.StoryVisionModel, temperature = 0.1, thinking = new { type = "disabled" },
            messages = new[] { new { role = "user", content } },
        }), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await Http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"视觉接口 HTTP {(int)response.StatusCode}。");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return Parse(text, batch.Count);
    }

    internal static IReadOnlyList<(int Id, double Score, string Summary)> Parse(string content, int count)
    {
        using var doc = JsonDocument.Parse(LlmScorer.ExtractJsonObject(content));
        var seen = new HashSet<int>();
        var scores = new List<(int Id, double Score, string Summary)>();
        foreach (var scene in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            if (scene.ValueKind != JsonValueKind.Object || !scene.TryGetProperty("id", out var idValue) ||
                idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var id) ||
                id < 1 || id > count || !scene.TryGetProperty("score", out var scoreValue) || scoreValue.ValueKind != JsonValueKind.Number ||
                !scoreValue.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 10 || !seen.Add(id))
                continue;
            var text = scene.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
                ? (summary.GetString() ?? "").ReplaceLineEndings(" ").Trim() : "";
            scores.Add((id, score, text[..Math.Min(text.Length, 160)]));
        }
        return scores;
    }
}
