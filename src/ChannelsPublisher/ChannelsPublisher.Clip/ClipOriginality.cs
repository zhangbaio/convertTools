using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChannelsPublisher.Clip;

/// <summary>成片原创度后处理：对渲染好的成片做一次重编码，叠加确定性随机的轻扰动
/// （微缩放/调色/变速/开闭幕 + 可选贴纸）。移植自 material_clip/originality.py。
/// 每条成片按文件名种子 → 可复现、不同片不同扰动。失败保留原片。</summary>
public static class ClipOriginality
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] StickerExts = { ".png", ".webp" };
    private static readonly string[] Corners = { "tl", "tr", "bl", "br" };

    public static async Task ApplyAsync(string clipPath, ClipEngineOptions opts, Action<string>? log, CancellationToken ct)
    {
        if (!opts.OrigEnabled || !File.Exists(clipPath)) return;
        var rng = SeedRng(Path.GetFileName(clipPath));
        int w = opts.Width, h = opts.Height;

        var filters = new List<string>();
        double? atempo = null;

        if (opts.OrigZoom)
        {
            double z = Math.Round(1.02 + rng.NextDouble() * 0.04, 4);
            filters.Add($"crop=trunc(iw/{z.ToString(Inv)}/2)*2:trunc(ih/{z.ToString(Inv)}/2)*2,scale={w}:{h}");
        }
        if (opts.OrigColor)
        {
            double b = Math.Round((rng.NextDouble() - 0.5) * 0.06, 3);
            double c = Math.Round(0.97 + rng.NextDouble() * 0.06, 3);
            double s = Math.Round(0.95 + rng.NextDouble() * 0.10, 3);
            double g = Math.Round(0.97 + rng.NextDouble() * 0.06, 3);
            filters.Add($"eq=brightness={b.ToString(Inv)}:contrast={c.ToString(Inv)}:saturation={s.ToString(Inv)}:gamma={g.ToString(Inv)}");
        }
        if (opts.OrigSpeed)
        {
            atempo = Math.Round(0.96 + rng.NextDouble() * 0.08, 3);
            filters.Add($"setpts={Math.Round(1.0 / atempo.Value, 5).ToString(Inv)}*PTS");
        }
        if (opts.OrigFade)
        {
            filters.Add("fade=t=in:st=0:d=0.4");
            double dur = 0;
            try { dur = await Ffmpeg.ProbeDurationSecondsAsync(opts.FfprobePath, clipPath, ct); } catch { /* 无时长则只淡入 */ }
            if (dur > 0) filters.Add($"fade=t=out:st={Math.Max(0, dur - 0.4).ToString("0.###", Inv)}:d=0.4");
        }

        // 贴纸：随机选一张 PNG 叠到随机角落（随机大小/透明度/边距）。
        var stickers = ListStickers(opts.OrigStickerDir);
        string? sticker = null;
        int stkWidth = 0; double stkOp = 1; string corner = "br"; int margin = 24;
        if (stickers.Count > 0)
        {
            stkWidth = Math.Max(48, (int)(w * (0.10 + rng.NextDouble() * 0.08)));
            stkOp = Math.Round(0.55 + rng.NextDouble() * 0.30, 2);
            corner = Corners[rng.Next(Corners.Length)];
            margin = (int)(20 + rng.NextDouble() * 28);
            sticker = stickers[rng.Next(stickers.Count)];
        }

        if (filters.Count == 0 && atempo is null && sticker is null) return;

        var videoChain = filters.Count > 0 ? string.Join(",", filters) : "null";
        var parts = new List<string> { $"[0:v]{videoChain}[base]" };
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", clipPath };
        if (sticker != null)
        {
            args.Add("-i"); args.Add(sticker);
            parts.Add($"[1:v]scale={stkWidth}:-1,format=rgba,colorchannelmixer=aa={stkOp.ToString(Inv)}[stk]");
            parts.Add($"[base][stk]overlay={OverlayPos(corner, margin)}[v]");
        }
        else
        {
            parts.Add("[base]null[v]");
        }

        var maps = new List<string> { "-map", "[v]" };
        if (atempo is double at)
        {
            parts.Add($"[0:a]atempo={at.ToString(Inv)}[a]");
            maps.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "192k" });
        }
        else
        {
            maps.AddRange(new[] { "-map", "0:a?", "-c:a", "copy" });
        }

        var (preset, crf) = ResolveEncode(opts.RenderSpeed);
        var tmp = Path.Combine(Path.GetDirectoryName(clipPath)!, Path.GetFileNameWithoutExtension(clipPath) + ".orig-tmp" + Path.GetExtension(clipPath));
        args.AddRange(new[] { "-filter_complex", string.Join(";", parts) });
        args.AddRange(maps);
        args.AddRange(new[] { "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p", "-map_metadata", "-1", "-movflags", "+faststart", tmp });

        try
        {
            log?.Invoke($"🎨 原创度：{Path.GetFileName(clipPath)}（滤镜 {filters.Count} 项{(sticker != null ? "+贴纸" : "")}）");
            await Ffmpeg.RunAsync(opts.FfmpegPath, args, ct);
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                File.Delete(clipPath);
                File.Move(tmp, clipPath);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠️ 原创度处理失败，保留原片：{ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
        }
    }

    private static List<string> ListStickers(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir)
            .Where(p => StickerExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string OverlayPos(string corner, int m) => corner switch
    {
        "tl" => $"{m}:{m}",
        "tr" => $"main_w-overlay_w-{m}:{m}",
        "bl" => $"{m}:main_h-overlay_h-{m}",
        _ => $"main_w-overlay_w-{m}:main_h-overlay_h-{m}",
    };

    private static Random SeedRng(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? ""));
        return new Random(BitConverter.ToInt32(hash, 0));
    }

    private static (string preset, int crf) ResolveEncode(string speed) => (speed ?? "fast").Trim().ToLowerInvariant() switch
    {
        "quality" => ("medium", 20),
        "balanced" => ("faster", 21),
        _ => ("veryfast", 22),
    };
}
