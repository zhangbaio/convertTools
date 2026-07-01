using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChannelsPublisher.Prep;

/// <summary>简版剪辑生成（.NET ffmpeg 切片）：把源视频按「每集 N 条、每条约 T 秒」均匀切成竖屏成片，
/// 写入 material-clip-output/ 供 material_clips 来源消费。
/// 注意：这是规则/时长级切片，非 ASR/LLM 智能选段——高光/混剪/解说的智能差异需 Python 桥接。</summary>
public sealed class ClipGenerator
{
    private static readonly string[] VideoExt = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<IReadOnlyList<string>> GenerateAsync(
        IReadOnlyList<string> sourceVideos, string outputDir,
        int clipsPerVideo, int targetSeconds, int width, int height, bool force,
        string ffmpeg, string ffprobe, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var outputs = new List<string>();
        int epFallback = 0;
        foreach (var src in sourceVideos)
        {
            ct.ThrowIfCancellationRequested();
            epFallback++;

            double dur;
            try { dur = await ProbeDurationAsync(ffprobe, src, ct); }
            catch (Exception ex) { log?.Invoke($"  ⚠ 跳过（读不到时长）：{Path.GetFileName(src)} - {ex.Message}"); continue; }

            int ep = TryEpisode(src) ?? epFallback;
            var segments = BuildSegments(dur, Math.Max(1, targetSeconds), Math.Max(1, clipsPerVideo));
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = segments.Count <= 1 ? $"高光-第{ep:D3}集.mp4" : $"高光-第{ep:D3}集-{i + 1:D2}.mp4";
                var outPath = Path.Combine(outputDir, name);
                if (!force && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    log?.Invoke($"  ✓ 复用：{name}");
                    outputs.Add(outPath);
                    continue;
                }
                log?.Invoke($"  切片 {Path.GetFileName(src)} → {name}");
                await ExportAsync(ffmpeg, src, outPath, segments[i].Start, segments[i].Duration, width, height, ct);
                outputs.Add(outPath);
            }
        }
        return outputs;
    }

    /// <summary>扁平扫描某目录下的视频文件（顶层），自然排序。</summary>
    public static IReadOnlyList<string> ScanVideos(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => VideoExt.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 均匀取 count 个窗口，每个窗口约 target 秒（不足则居中取一段）。
    private static IReadOnlyList<(double Start, double Duration)> BuildSegments(double duration, int target, int count)
    {
        double safe = Math.Max(1, duration);
        double clip = Math.Max(1, Math.Min(target, safe));
        if (count <= 1 || safe <= clip + 0.001)
            return new[] { (Math.Max(0, (safe - clip) / 2), clip) };

        double maxStart = Math.Max(0, safe - clip);
        double step = maxStart / (count - 1);
        var list = new List<(double, double)>(count);
        for (int i = 0; i < count; i++) list.Add((step * i, clip));
        return list;
    }

    private static async Task ExportAsync(string ffmpeg, string input, string output, double start, double dur, int w, int h, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);
        var filter = $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,format=yuv420p";
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.###", Inv), "-t", dur.ToString("0.###", Inv),
            "-i", input, "-map", "0:v:0", "-map", "0:a?",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart",
            output,
        };
        await FfmpegRunner.RunAsync(ffmpeg, args, ct);
        if (!File.Exists(output) || new FileInfo(output).Length <= 0)
            throw new Exception($"剪辑导出未生成输出：{Path.GetFileName(output)}");
    }

    private static async Task<double> ProbeDurationAsync(string ffprobe, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new Exception($"无法启动 ffprobe：{ffprobe}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        var stdout = (await outTask).Trim();
        if (proc.ExitCode != 0 || !double.TryParse(stdout, NumberStyles.Float, Inv, out var d) || d <= 0)
            throw new Exception($"ffprobe 读时长失败：{stdout}");
        return d;
    }

    private static int? TryEpisode(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"(?:第\s*0*(\d+)\s*集|ep\s*0*(\d+)|^0*(\d+)$)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        for (int gi = 1; gi < m.Groups.Count; gi++)
            if (m.Groups[gi].Success && int.TryParse(m.Groups[gi].Value, out var v) && v > 0) return v;
        return null;
    }
}
