using System.Globalization;

namespace ChannelsPublisher.Clip;

/// <summary>高光渲染：逐段裁切成竖屏 + 响度归一 + 编码，再无损 concat 成一条短片。
/// 移植自 material_clip/rendering_segments.py + rendering_highlight.py 的 ffmpeg 参数。</summary>
public sealed class HighlightRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task RenderAsync(IReadOnlyList<ClipCandidate> bin, string outputPath, ClipEngineOptions opts, CancellationToken ct)
    {
        var (preset, crf) = ResolveEncode(opts.RenderSpeed);
        var tmpDir = Path.Combine(Path.GetTempPath(), "clip-seg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var segFiles = new List<string>();
        try
        {
            int idx = 0;
            foreach (var c in bin)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                var segOut = Path.Combine(tmpDir, $"seg{idx:D3}.mp4");
                double start = Math.Max(0, (c.StartMs - 400) / 1000.0);   // 前后各留 400ms
                double dur = (c.DurationMs + 800) / 1000.0;
                await Ffmpeg.RunAsync(opts.FfmpegPath, BuildSegArgs(c.VideoPath, segOut, start, dur, opts, preset, crf), ct);
                if (File.Exists(segOut) && new FileInfo(segOut).Length > 0) segFiles.Add(segOut);
            }
            if (segFiles.Count == 0) throw new Exception("没有可用片段");

            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (segFiles.Count == 1)
            {
                File.Copy(segFiles[0], outputPath);
            }
            else
            {
                var listFile = Path.Combine(tmpDir, "concat.txt");
                await File.WriteAllTextAsync(
                    listFile,
                    string.Join("\n", segFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'")),
                    ct);
                await Ffmpeg.RunAsync(opts.FfmpegPath, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "concat", "-safe", "0", "-i", listFile,
                    "-c", "copy", "-movflags", "+faststart", outputPath,
                }, ct);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    private static string[] BuildSegArgs(string video, string segOut, double start, double dur, ClipEngineOptions o, string preset, int crf)
    {
        int w = o.Width, h = o.Height;
        var vf = $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,fps=25,format=yuv420p";
        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString("0.###", Inv), "-t", dur.ToString("0.###", Inv),
            "-i", video, "-map", "0:v:0", "-map", "0:a?",
            "-vf", vf,
            "-af", "loudnorm=I=-16:TP=-1.5:LRA=11",
            "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(),
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart", segOut,
        };
    }

    private static (string preset, int crf) ResolveEncode(string speed) => (speed ?? "fast").Trim().ToLowerInvariant() switch
    {
        "quality" => ("medium", 20),
        "balanced" => ("faster", 21),
        _ => ("veryfast", 22),
    };
}
