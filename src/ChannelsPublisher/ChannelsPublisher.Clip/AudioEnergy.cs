using System.Globalization;
using System.Text.RegularExpressions;

namespace ChannelsPublisher.Clip;

/// <summary>基于 ffmpeg ebur128 的音频能量分析与候选加权。移植自 material_clip/audio_energy.py。
/// best-effort：ffmpeg 无 ebur128/无音轨/解析失败时降级为 no-op。</summary>
public static class AudioEnergy
{
    private const double LufsFloor = -32.0;
    private const double LufsCeil = -9.0;
    private const double SilenceLufs = -70.0;

    private static readonly Regex PtsTime = new(@"pts_time:([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex R128M = new(@"lavfi\.r128\.M=\s*(-?[0-9.]+|-?inf)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>对候选按响度峰值/均值填 AudioEnergy(0-10)。不改 Total（加权在 SignalWeights 统一做）。</summary>
    public static async Task<int> ApplyAsync(IReadOnlyList<ClipCandidate> candidates, string ffmpeg, string video, Action<string>? log, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;
        var envelope = await ExtractEnvelopeAsync(ffmpeg, video, log, ct);
        if (envelope.Count == 0) return 0;

        int scored = 0;
        foreach (var c in candidates)
        {
            var e = WindowScore(envelope, c.StartMs, c.EndMs);
            if (e is null) continue;
            c.AudioEnergy = Math.Round(e.Value, 2);
            scored++;
        }
        return scored;
    }

    private static async Task<List<(int Ms, double Lufs)>> ExtractEnvelopeAsync(string ffmpeg, string video, Action<string>? log, CancellationToken ct)
    {
        var env = new List<(int, double)>();
        try
        {
            var (_, stdout, stderr) = await Ffmpeg.RunCaptureAsync(ffmpeg, new[]
            {
                "-hide_banner", "-nostats",
                "-i", video, "-map", "0:a:0",
                "-af", "ebur128=metadata=1,ametadata=mode=print:file=-",
                "-f", "null", "-",
            }, ct);

            int? lastMs = null;
            foreach (var line in (stdout + "\n" + stderr).Split('\n'))
            {
                var pts = PtsTime.Match(line);
                if (pts.Success)
                {
                    lastMs = (int)Math.Round(double.Parse(pts.Groups[1].Value, CultureInfo.InvariantCulture) * 1000);
                    continue;
                }
                var m = R128M.Match(line);
                if (m.Success && lastMs is not null)
                {
                    var raw = m.Groups[1].Value.ToLowerInvariant();
                    double lufs = raw.Contains("inf") ? SilenceLufs : double.Parse(raw, CultureInfo.InvariantCulture);
                    env.Add((lastMs.Value, lufs));
                    lastMs = null;
                }
            }
        }
        catch (Exception ex) { log?.Invoke($"ℹ️ 音频能量分析跳过：{ex.Message}"); return new List<(int, double)>(); }

        if (env.Count == 0) log?.Invoke("ℹ️ 音频能量分析跳过：未取到响度（无音轨或 ffmpeg 不支持 ebur128）");
        return env;
    }

    private static double? WindowScore(List<(int Ms, double Lufs)> env, int startMs, int endMs)
    {
        var values = env.Where(p => p.Ms >= startMs && p.Ms <= endMs).Select(p => p.Lufs).ToList();
        if (values.Count == 0) return null;
        double peak = values.Max();
        double mean = values.Average();
        double blended = 0.6 * peak + 0.4 * mean;
        double score = (blended - LufsFloor) / (LufsCeil - LufsFloor) * 10.0;
        return Math.Max(0.0, Math.Min(10.0, score));
    }
}

/// <summary>信号加权：音频能量/镜头密度对综合分做温和乘子。移植自 material_clip/selection.py apply_signal_weights。</summary>
public static class SignalWeights
{
    public static void Apply(IReadOnlyList<ClipCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            double audioMult = 0.85 + 0.03 * c.AudioEnergy;  // 0.85~1.15
            double shotMult = 0.92 + 0.016 * c.ShotDensity;  // 0.92~1.08
            c.Total = Math.Round(c.Total * audioMult * shotMult, 2);
        }
    }
}
