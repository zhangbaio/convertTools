using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>纯 C# 高光剪辑引擎（Phase 1，零 Python）：抽音 → 火山 ASR → 候选 → 关键词打分 →
/// 50%+集配额选段 → 装箱 → ffmpeg 竖屏渲染 + concat。产物写 &lt;project&gt;/素材剪辑输出/高光/，供 material_clips 消费。
/// 未含：音频能量/镜头密度/LLM 复评分/解说 TTS（后续分期）。</summary>
public sealed class HighlightClipEngine
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
            var outDir = Path.Combine(projectDir, "素材剪辑输出", "高光");
            Directory.CreateDirectory(outDir);

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
                all.AddRange(cands);
                log?.Invoke($"  字幕 {segs.Count} 句 → 候选 {cands.Count} 段");
            }

            if (all.Count == 0)
                return new ClipEngineResult(false, Array.Empty<string>(), "无候选片段（ASR 无字幕或视频过短）");

            var selected = HighlightPlanner.Select(all, epDur);
            int minMs = Math.Max(1, opts.ClipMinSeconds) * 1000;
            int maxMs = Math.Max(opts.ClipMinSeconds, opts.ClipMaxSeconds) * 1000;
            var bins = HighlightPlanner.PlanRangeBins(selected, opts.ClipCount, minMs, maxMs);
            log?.Invoke($"选段 {selected.Count} 段 → 装箱 {bins.Count} 条短片");

            var basename = SafeName(Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var outputs = new List<string>();
            for (int i = 0; i < bins.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var outPath = Path.Combine(outDir, bins.Count <= 1 ? $"{basename}-高光.mp4" : $"{basename}-高光-{i + 1}.mp4");
                log?.Invoke($"渲染 {i + 1}/{bins.Count}：{Path.GetFileName(outPath)}（{bins[i].Count} 段）");
                await _renderer.RenderAsync(bins[i], outPath, opts, ct);
                WriteSidecar(outPath, bins[i]);
                outputs.Add(outPath);
            }
            return new ClipEngineResult(true, outputs, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ClipEngineResult(false, Array.Empty<string>(), $"{ex.GetType().Name}: {ex.Message}"); }
    }

    // 简版发表元数据（.publish.json）：描述取分最高片段文本首段，标签留空（LLM 文案后续分期接）。
    private static void WriteSidecar(string videoPath, IReadOnlyList<ClipCandidate> bin)
    {
        var top = bin.OrderByDescending(c => c.Total).FirstOrDefault();
        var desc = (top?.Text ?? "").Trim();
        if (desc.Length > 40) desc = desc[..40];
        var json = JsonSerializer.Serialize(new { description = desc, tags = Array.Empty<string>(), short_title = "", caption = desc });
        try { File.WriteAllText(Path.ChangeExtension(videoPath, ".publish.json"), json); } catch { /* 忽略 */ }
    }

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "剧集" : name;
    }
}
