using System.Globalization;

namespace ChannelsPublisher.Clip;

/// <summary>解说渲染：逐段（原声/解说配音）竖屏重编码后无损 concat 成一条解说成片。
/// v1 简化：不做原字幕 OCR 抹除与新字幕烧录（需 OCR，后续补）；解说段用配音替换原声，
/// 配音更长则慢放视频以放完旁白。</summary>
public sealed class CommentaryRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly VolcTtsClient _tts = new();

    public async Task<string?> RenderAsync(string projectDir, List<ClipCandidate> segs, List<NarrationLine> lines, ClipEngineOptions opts, string basename, Action<string>? log, CancellationToken ct)
    {
        var outDir = Path.Combine(projectDir, "素材剪辑输出", "解说");
        Directory.CreateDirectory(outDir);
        var tmpDir = Path.Combine(Path.GetTempPath(), "clip-comm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var (preset, crf) = ResolveEncode(opts.RenderSpeed);
        var segFiles = new List<string>();
        try
        {
            for (int i = 0; i < segs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = segs[i];
                var line = i < lines.Count ? lines[i] : new NarrationLine { KeepOriginal = true };
                string? voice = null;
                if (!line.KeepOriginal && !string.IsNullOrWhiteSpace(line.Narration))
                {
                    try
                    {
                        var mp3 = await _tts.SynthesizeAsync(line.Narration, CommentaryScripter.SpeedRatio(line, opts), opts, ct);
                        voice = Path.Combine(tmpDir, $"voice{i + 1:D3}.mp3");
                        await File.WriteAllBytesAsync(voice, mp3, ct);
                    }
                    catch (Exception ex) { log?.Invoke($"  ⚠ 第{i + 1}段 TTS 失败，改用原声：{ex.Message}"); voice = null; }
                }

                log?.Invoke($"[解说] 渲染 {i + 1}/{segs.Count}{(voice != null ? "（配音）" : "（原声）")}");
                var segOut = Path.Combine(tmpDir, $"seg{i + 1:D3}.mp4");
                await RenderSegmentAsync(c, voice, segOut, opts, preset, crf, ct);
                if (File.Exists(segOut) && new FileInfo(segOut).Length > 0) segFiles.Add(segOut);
            }
            if (segFiles.Count == 0) return null;

            var outPath = Path.Combine(outDir, $"{basename}-解说.mp4");
            if (File.Exists(outPath)) File.Delete(outPath);
            if (segFiles.Count == 1)
            {
                File.Copy(segFiles[0], outPath);
            }
            else
            {
                var listFile = Path.Combine(tmpDir, "concat.txt");
                await File.WriteAllTextAsync(listFile, string.Join("\n", segFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'")), ct);
                await Ffmpeg.RunAsync(opts.FfmpegPath, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", outPath,
                }, ct);
            }
            return outPath;
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略 */ } }
    }

    private async Task RenderSegmentAsync(ClipCandidate c, string? voice, string segOut, ClipEngineOptions o, string preset, int crf, CancellationToken ct)
    {
        int w = o.Width, h = o.Height;
        double start = Math.Max(0, (c.StartMs - 400) / 1000.0);
        double clipDur = Math.Max(0.5, (c.DurationMs + 800) / 1000.0);
        var vfBase = $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,fps=25,format=yuv420p";

        if (voice is null)
        {
            await Ffmpeg.RunAsync(o.FfmpegPath, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", start.ToString("0.###", Inv), "-t", clipDur.ToString("0.###", Inv), "-i", c.VideoPath,
                "-map", "0:v:0", "-map", "0:a?", "-vf", vfBase,
                "-af", "loudnorm=I=-16:TP=-1.5:LRA=11",
                "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", segOut,
            }, ct);
            return;
        }

        double ttsDur = 0;
        try { ttsDur = await Ffmpeg.ProbeDurationSecondsAsync(o.FfprobePath, voice, ct); } catch { /* 用 clipDur */ }
        double target = Math.Max(clipDur, ttsDur > 0 ? ttsDur : clipDur);
        double factor = clipDur > 0 ? target / clipDur : 1.0;   // ≥1 → 慢放视频放完旁白
        var vf = factor > 1.001 ? $"{vfBase},setpts={factor.ToString("0.####", Inv)}*PTS" : vfBase;

        await Ffmpeg.RunAsync(o.FfmpegPath, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString("0.###", Inv), "-t", clipDur.ToString("0.###", Inv), "-i", c.VideoPath,
            "-i", voice,
            "-filter_complex", $"[0:v]{vf}[v];[1:a]apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", target.ToString("0.###", Inv),
            "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", segOut,
        }, ct);
    }

    private static (string preset, int crf) ResolveEncode(string speed) => (speed ?? "fast").Trim().ToLowerInvariant() switch
    {
        "quality" => ("medium", 20),
        "balanced" => ("faster", 21),
        _ => ("veryfast", 22),
    };
}
