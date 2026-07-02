using System.Globalization;
using System.Text;

namespace ChannelsPublisher.Clip;

/// <summary>解说渲染：逐段（原声/解说配音）竖屏重编码后无损 concat。
/// 解说段：配音替换原声（配音更长则慢放视频放完旁白）；BurnSubtitles 时抹除原字幕（底部模糊带）
/// 并烧录解说字幕（ffmpeg drawtext + CJK 字体）。抹除用固定底部带（Python OCR 失败时的同款兜底，
/// 对底部居中硬字幕稳妥）；精确逐视频 OCR 定位需重型依赖，暂不引入。</summary>
public sealed class CommentaryRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly VolcTtsClient _tts = new();
    private static readonly Lazy<string?> CjkFont = new(ResolveCjkFont);

    public async Task<string?> RenderAsync(string projectDir, List<ClipCandidate> segs, List<NarrationLine> lines, ClipEngineOptions opts, string basename, Action<string>? log, CancellationToken ct)
    {
        var outDir = Path.Combine(projectDir, "素材剪辑输出", "解说");
        Directory.CreateDirectory(outDir);
        var tmpDir = Path.Combine(Path.GetTempPath(), "clip-comm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var (preset, crf) = ResolveEncode(opts.RenderSpeed);
        bool burn = opts.BurnSubtitles && CjkFont.Value != null;
        if (opts.BurnSubtitles && CjkFont.Value == null) log?.Invoke("ℹ️ 未找到中文字体，跳过解说字幕烧录（仅配音）");
        // 字体拷进临时目录用裸文件名引用，规避 ffmpeg 滤镜里 Windows 盘符冒号的转义地狱（cwd=tmpDir）。
        string? fontBare = null;
        if (burn)
        {
            fontBare = "font" + Path.GetExtension(CjkFont.Value);
            try { File.Copy(CjkFont.Value!, Path.Combine(tmpDir, fontBare), overwrite: true); }
            catch { burn = false; log?.Invoke("ℹ️ 字体拷贝失败，跳过字幕烧录"); }
        }
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

                log?.Invoke($"[解说] 渲染 {i + 1}/{segs.Count}{(voice != null ? "（配音" + (burn ? "+字幕" : "") + "）" : "（原声）")}");
                var segOut = Path.Combine(tmpDir, $"seg{i + 1:D3}.mp4");
                var capText = burn && voice != null ? WrapCaption(line.Narration) : null;
                string? capBare = null;
                if (!string.IsNullOrEmpty(capText))
                {
                    capBare = $"cap{i + 1:D3}.txt";
                    await File.WriteAllTextAsync(Path.Combine(tmpDir, capBare), capText, new UTF8Encoding(false), ct);
                }
                await RenderSegmentAsync(c, voice, capBare, fontBare, tmpDir, segOut, opts, preset, crf, ct);
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

    private async Task RenderSegmentAsync(ClipCandidate c, string? voice, string? capBare, string? fontBare, string workDir, string segOut, ClipEngineOptions o, string preset, int crf, CancellationToken ct)
    {
        int w = o.Width, h = o.Height;
        double start = Math.Max(0, (c.StartMs - 400) / 1000.0);
        double clipDur = Math.Max(0.5, (c.DurationMs + 800) / 1000.0);
        var vfBase = $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,fps=25,format=yuv420p";

        // 原声段：保留原声，不改字幕。
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
        double factor = clipDur > 0 ? target / clipDur : 1.0;
        var vf = factor > 1.001 ? $"{vfBase},setpts={factor.ToString("0.####", Inv)}*PTS" : vfBase;

        string filter;
        if (capBare != null && fontBare != null)
        {
            // 抹除原字幕：底部模糊带；烧录解说字幕：drawtext（字体/文本用裸文件名，cwd=workDir 规避盘符转义）。
            int bandY = (int)(h * 0.78), bandH = (int)(h * 0.20), fs = Math.Max(20, (int)(h * 0.030));
            filter =
                $"[0:v]{vf}[base];" +
                $"[base]split[m][s];" +
                $"[s]crop={w}:{bandH}:0:{bandY},boxblur=18[bl];" +
                $"[m][bl]overlay=0:{bandY}[masked];" +
                $"[masked]drawtext=fontfile={fontBare}:textfile={capBare}:fontsize={fs}:fontcolor=white:borderw=3:bordercolor=black:line_spacing=10:x=(w-text_w)/2:y={bandY}+40[v];" +
                $"[1:a]apad[a]";
        }
        else
        {
            filter = $"[0:v]{vf}[v];[1:a]apad[a]";
        }

        await Ffmpeg.RunAsync(o.FfmpegPath, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString("0.###", Inv), "-t", clipDur.ToString("0.###", Inv), "-i", c.VideoPath,
            "-i", voice,
            "-filter_complex", filter,
            "-map", "[v]", "-map", "[a]", "-t", target.ToString("0.###", Inv),
            "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", segOut,
        }, ct, workingDirectory: workDir);
    }

    // 解说字幕折行：去换行，按每行 ~16 字，最多 2 行，超出加省略号。
    private static string WrapCaption(string text)
    {
        var t = new string((text ?? "").Where(ch => ch != '\n' && ch != '\r').ToArray()).Trim();
        if (t.Length == 0) return "";
        const int per = 16, maxLines = 2;
        var lines = new List<string>();
        for (int i = 0; i < t.Length && lines.Count < maxLines; i += per)
            lines.Add(t.Substring(i, Math.Min(per, t.Length - i)));
        if (t.Length > per * maxLines && lines.Count > 0)
        {
            var last = lines[^1];
            lines[^1] = (last.Length >= per ? last[..(per - 1)] : last) + "…";
        }
        return string.Join("\n", lines);
    }

    private static string? ResolveCjkFont()
    {
        string fontsDir;
        try { fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
        catch { fontsDir = @"C:\Windows\Fonts"; }
        if (string.IsNullOrWhiteSpace(fontsDir)) fontsDir = @"C:\Windows\Fonts";
        foreach (var name in new[] { "simhei.ttf", "simkai.ttf", "simfang.ttf", "msyh.ttc", "msyhl.ttc", "simsun.ttc", "Deng.ttf" })
        {
            var p = Path.Combine(fontsDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static (string preset, int crf) ResolveEncode(string speed) => (speed ?? "fast").Trim().ToLowerInvariant() switch
    {
        "quality" => ("medium", 20),
        "balanced" => ("faster", 21),
        _ => ("veryfast", 22),
    };
}
