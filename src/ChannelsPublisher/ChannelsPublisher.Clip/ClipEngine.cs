using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>纯 C# 剪辑引擎（零 Python）：抽音→火山 ASR→候选→关键词打分→音频能量→LLM 复评分→信号加权（共享一次），
/// 再按每个模式(highlight/mashup)选段→装箱→ffmpeg 渲染。产物写 &lt;project&gt;/素材剪辑输出/&lt;模式&gt;/ + .publish.json。
/// 切片/解说/本地 ASR 为后续分期。</summary>
public sealed class ClipEngine
{
    private readonly VolcengineAsrClient _asr = new();
    private readonly HighlightRenderer _renderer = new();

    public async Task<ClipEngineResult> GenerateAsync(
        string projectDir,
        IReadOnlyList<EpisodeSource> episodes,
        ClipEngineOptions opts,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            // —— 共享分析：候选 + 音频能量 + LLM + 信号加权（只做一次，各模式复用）——
            var all = new List<ClipCandidate>();
            var epDur = new Dictionary<int, double>();
            foreach (var ep in episodes)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"[{ep.EpisodeIndex}] 抽音 + ASR：{Path.GetFileName(ep.VideoPath)}");
                double durSec;
                try { durSec = await Ffmpeg.ProbeDurationSecondsAsync(opts.FfprobePath, ep.VideoPath, ct); }
                catch (Exception ex) { log?.Invoke($"  ⚠ 跳过（读不到时长）：{ex.Message}"); continue; }
                epDur[ep.EpisodeIndex] = durSec;

                var wav = Path.Combine(Path.GetTempPath(), $"clip-asr-{Guid.NewGuid():N}.wav");
                List<SubtitleSegment> segs;
                try
                {
                    await Ffmpeg.ExtractAudioAsync(opts.FfmpegPath, ep.VideoPath, wav, ct);
                    segs = await _asr.TranscribeAsync(wav, opts, log, ct);
                }
                finally { try { File.Delete(wav); } catch { /* 忽略 */ } }

                var cands = CandidateBuilder.Build(ep, segs, (int)Math.Round(durSec * 1000));
                if (opts.AudioEnergy && cands.Count > 0)
                {
                    var n = await AudioEnergy.ApplyAsync(cands, opts.FfmpegPath, ep.VideoPath, log, ct);
                    if (n > 0) log?.Invoke($"  音频能量：{n} 段已按响度评分");
                }
                all.AddRange(cands);
                log?.Invoke($"  字幕 {segs.Count} 句 → 候选 {cands.Count} 段");
            }

            if (all.Count == 0)
                return new ClipEngineResult(false, Array.Empty<string>(), "无候选片段（ASR 无字幕或视频过短）");

            if (opts.EnableLlmScore && !string.IsNullOrWhiteSpace(opts.AiEndpoint))
            {
                try { await new LlmScorer().ApplyAsync(all, opts, log, ct); }
                catch (Exception ex) { log?.Invoke($"⚠️ AI 复评分整体跳过：{ex.Message}"); }
            }
            SignalWeights.Apply(all);

            // —— 逐模式选段 → 装箱 → 渲染 ——
            var basename = SafeName(Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            int minMs = Math.Max(1, opts.ClipMinSeconds) * 1000;
            int maxMs = Math.Max(opts.ClipMinSeconds, opts.ClipMaxSeconds) * 1000;
            var modes = opts.Modes is { Count: > 0 } ? opts.Modes : new List<string> { "highlight" };

            var outputs = new List<string>();
            foreach (var modeRaw in modes)
            {
                ct.ThrowIfCancellationRequested();
                var mode = (modeRaw ?? "").Trim().ToLowerInvariant();
                List<ClipCandidate> ordered;
                bool preserveOrder;
                string folder, label;
                switch (mode)
                {
                    case "highlight":
                        ordered = HighlightPlanner.Select(all, epDur);
                        preserveOrder = false; folder = "高光"; label = "高光";
                        break;
                    case "mashup":
                        ordered = Mashup.Select(all, log);
                        preserveOrder = true; folder = "混剪"; label = "混剪";
                        break;
                    default:
                        log?.Invoke($"⚠️ 模式「{modeRaw}」本期未实现（当前支持 高光/混剪），跳过");
                        continue;
                }

                var bins = HighlightPlanner.PlanRangeBins(ordered, opts.ClipCount, minMs, maxMs, preserveOrder);
                var outDir = Path.Combine(projectDir, "素材剪辑输出", folder);
                Directory.CreateDirectory(outDir);
                log?.Invoke($"[{label}] 选段 {ordered.Count} 段 → 装箱 {bins.Count} 条短片");
                for (int i = 0; i < bins.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var outPath = Path.Combine(outDir, bins.Count <= 1 ? $"{basename}-{label}.mp4" : $"{basename}-{label}-{i + 1}.mp4");
                    log?.Invoke($"[{label}] 渲染 {i + 1}/{bins.Count}：{Path.GetFileName(outPath)}（{bins[i].Count} 段）");
                    await _renderer.RenderAsync(bins[i], outPath, opts, ct);
                    WriteSidecar(outPath, bins[i]);
                    outputs.Add(outPath);
                }
            }
            return new ClipEngineResult(true, outputs, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ClipEngineResult(false, Array.Empty<string>(), $"{ex.GetType().Name}: {ex.Message}"); }
    }

    // 发表元数据（.publish.json）：优先 LLM 推荐语/短标题/标签，否则回退候选文本。
    private static void WriteSidecar(string videoPath, IReadOnlyList<ClipCandidate> bin)
    {
        var top = bin.OrderByDescending(c => c.Total).FirstOrDefault();
        var desc = FirstNonEmpty(top?.RecommendReason, top?.Summary, top?.Text);
        if (desc.Length > 40) desc = desc[..40];
        var shortTitle = (top?.Title ?? "").Trim();
        var tags = top?.Tags ?? new List<string>();
        var caption = tags.Count > 0 ? $"{desc} {string.Join(" ", tags.Select(t => "#" + t))}" : desc;
        var json = JsonSerializer.Serialize(new { description = desc, tags, short_title = shortTitle, caption });
        try { File.WriteAllText(Path.ChangeExtension(videoPath, ".publish.json"), json); } catch { /* 忽略 */ }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "剧集" : name;
    }
}
