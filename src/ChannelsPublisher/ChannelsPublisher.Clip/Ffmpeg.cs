using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>ffmpeg/ffprobe 子进程封装（用 ArgumentList 避免转义）。</summary>
public static class Ffmpeg
{
    public static async Task RunAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct, string? workingDirectory = null)
    {
        var (code, _, stderr) = await RunCaptureAsync(ffmpeg, args, ct, workingDirectory);
        if (code != 0)
            throw new InvalidOperationException($"{Path.GetFileName(ffmpeg)} 退出码 {code}：{stderr.Trim()}");
    }

    /// <summary>执行并捕获 stdout+stderr（不因非零退出抛错，供 best-effort 分析如 ebur128）。</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string ffmpeg, IReadOnlyList<string> args, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        ct.ThrowIfCancellationRequested();
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new Exception($"无法启动 ffmpeg：{ffmpeg}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (proc.ExitCode, await outTask, await errTask);
    }

    public static async Task<double> ProbeDurationSecondsAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunCaptureAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path], ct);
        if (code != 0 || !double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d) || d <= 0)
            throw new InvalidDataException($"ffprobe 读时长失败：{stderr.Trim()}");
        return d;
    }

    internal static async Task<ClipMediaInfo> ProbeVideoAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunCaptureAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height:stream_side_data=rotation", "-of", "json", path], ct);
        if (code != 0)
            throw new InvalidDataException($"ffprobe 读取视频失败：{stderr.Trim()}");

        using var doc = JsonDocument.Parse(stdout);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined ||
            !double.TryParse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("素材没有可用的视频轨或时长。");

        var width = video.GetProperty("width").GetInt32();
        var height = video.GetProperty("height").GetInt32();
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Any(
                s => s.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var degrees) && Math.Abs(degrees % 180) > 45))
            (width, height) = (height, width);
        return new ClipMediaInfo(
            (int)Math.Clamp(Math.Round(duration * 1000), 1, int.MaxValue), width, height,
            streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"),
            streams.Any(s => s.GetProperty("codec_type").GetString() == "subtitle"));
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

internal sealed record ClipMediaInfo(int DurationMs, int Width, int Height, bool HasAudio, bool HasSubtitles);
