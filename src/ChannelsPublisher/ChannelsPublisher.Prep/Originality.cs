using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChannelsPublisher.Prep;

/// <summary>原创度扰动计划（视频滤镜链 + 可选音频变速）。</summary>
public sealed class OriginalityPlan
{
    public List<string> VideoFilters { get; } = new();
    public double? Atempo { get; set; }
    public bool IsEmpty => VideoFilters.Count == 0 && Atempo is null;
}

/// <summary>构建原创度扰动计划。移植自 Python material_clip/originality.build_originality_plan：
/// 种子=文件名 → 确定性 RNG（同名可复现、不同片不同扰动）。滤镜与取值区间保持一致。</summary>
public static class OriginalityPlanBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static OriginalityPlan Build(PrepConfig cfg, string seed, int width, int height, int durationMs = 0)
    {
        var plan = new OriginalityPlan();
        if (!cfg.OriginalityEnabled) return plan;
        var rng = SeedRng(seed);

        if (cfg.OrigZoom)
        {
            // 放大 1.02~1.06：裁出居中 1/zoom 区域再缩放回标准尺寸（保证输出偶数尺寸）
            double zoom = Math.Round(1.02 + rng.NextDouble() * 0.04, 4);
            string z = zoom.ToString(Inv);
            plan.VideoFilters.Add($"crop=trunc(iw/{z}/2)*2:trunc(ih/{z}/2)*2,scale={width}:{height}");
        }

        if (cfg.OrigColor)
        {
            double brightness = Math.Round((rng.NextDouble() - 0.5) * 0.06, 3); // -0.03~0.03
            double contrast = Math.Round(0.97 + rng.NextDouble() * 0.06, 3);
            double saturation = Math.Round(0.95 + rng.NextDouble() * 0.10, 3);
            double gamma = Math.Round(0.97 + rng.NextDouble() * 0.06, 3);
            plan.VideoFilters.Add(
                $"eq=brightness={brightness.ToString(Inv)}:contrast={contrast.ToString(Inv)}:saturation={saturation.ToString(Inv)}:gamma={gamma.ToString(Inv)}");
        }

        if (cfg.OrigSpeed)
        {
            double atempo = Math.Round(0.96 + rng.NextDouble() * 0.08, 3); // 0.96~1.04
            plan.Atempo = atempo;
            plan.VideoFilters.Add($"setpts={Math.Round(1.0 / atempo, 5).ToString(Inv)}*PTS");
        }

        if (cfg.OrigFade)
        {
            plan.VideoFilters.Add("fade=t=in:st=0:d=0.4");
            if (durationMs > 0)
            {
                double fadeOutStart = Math.Max(0.0, durationMs / 1000.0 - 0.4);
                plan.VideoFilters.Add($"fade=t=out:st={fadeOutStart.ToString("F3", Inv)}:d=0.4");
            }
        }
        return plan;
    }

    private static Random SeedRng(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? ""));
        int s = BitConverter.ToInt32(hash, 0);
        return new Random(s);
    }
}

/// <summary>用 ffmpeg 应用原创度扰动，一次重编码为 H.264/AAC（视频号友好）。</summary>
public sealed class FfmpegOriginalityProcessor
{
    public async Task<string> ProcessAsync(string inputPath, string outputPath, OriginalityPlan plan, string ffmpeg, CancellationToken ct)
    {
        if (plan.IsEmpty)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return outputPath;
        }

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath };
        if (plan.VideoFilters.Count > 0)
            args.AddRange(new[] { "-vf", string.Join(",", plan.VideoFilters) });
        if (plan.Atempo is double at)
            args.AddRange(new[] { "-af", $"atempo={at.ToString(CultureInfo.InvariantCulture)}" });

        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart",
            outputPath,
        });
        await FfmpegRunner.RunAsync(ffmpeg, args, ct);
        return outputPath;
    }
}
