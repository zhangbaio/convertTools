using System.Diagnostics;
using System.Globalization;

namespace ChannelsPublisher.Clip;

/// <summary>ffmpeg/ffprobe 子进程封装（用 ArgumentList 避免转义）。</summary>
public static class Ffmpeg
{
    public static async Task RunAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new Exception($"无法启动 ffmpeg：{ffmpeg}");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg 退出码 {proc.ExitCode}：{stderr.Trim()}");
    }

    public static async Task<double> ProbeDurationSecondsAsync(string ffprobe, string path, CancellationToken ct)
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
        if (proc.ExitCode != 0 || !double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0)
            throw new Exception($"ffprobe 读时长失败：{stdout}");
        return d;
    }

    /// <summary>抽 16k 单声道 PCM WAV 供 ASR（移植自 material_clip/asr.py 抽音参数）。</summary>
    public static async Task ExtractAudioAsync(string ffmpeg, string video, string outWav, CancellationToken ct)
    {
        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outWav,
        };
        await RunAsync(ffmpeg, args, ct);
    }
}
