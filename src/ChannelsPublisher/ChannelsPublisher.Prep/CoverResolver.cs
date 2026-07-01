namespace ChannelsPublisher.Prep;

/// <summary>为视频解析封面：优先用视频旁的封面图（sidecar），否则用 ffmpeg 抽一帧。</summary>
public sealed class CoverResolver
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp" };

    public async Task<string?> ResolveAsync(string videoPath, PrepConfig cfg, string outputDir, CancellationToken ct)
    {
        var mode = (cfg.CoverMode ?? "sidecar").Trim().ToLowerInvariant();
        if (mode == "none") return null;

        var sidecar = FindSidecarCover(videoPath);
        if (sidecar != null) return sidecar;
        if (mode == "sidecar") return null; // 只找旁车图，找不到就没有封面

        // frame：抽帧
        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var outPng = Path.Combine(outputDir, $"{stem}.cover.jpg");
        var seconds = Math.Max(0, cfg.CoverFrameSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await FfmpegRunner.RunAsync(cfg.FfmpegPath, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", seconds, "-i", videoPath, "-frames:v", "1", "-q:v", "2", outPng,
            }, ct);
            return File.Exists(outPng) ? outPng : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSidecarCover(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var ext in ImageExts)
        {
            // 常见命名：<stem>.cover.jpg / <stem>.jpg
            var c1 = Path.Combine(dir, stem + ".cover" + ext);
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(dir, stem + ext);
            if (File.Exists(c2)) return c2;
        }
        return null;
    }
}
