using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>对齐 Python <c>cover_service.ensure_tiktok_3x4_cover</c>：将海报裁成 3:4 再上传。</summary>
public static class TikTokCoverService
{
    public static string EnsureTikTok3x4Cover(string posterPath, string workflowProjectDir, Action<string>? log = null)
    {
        var source = Path.GetFullPath(posterPath);
        if (!File.Exists(source))
            throw new InvalidOperationException($"未找到可用于 TikTok 的封面图：{source}");

        var outputPath = Path.Combine(Path.GetFullPath(workflowProjectDir), "tiktok-cover-3x4.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var image = Image.Load(source);
        image.Mutate(ctx => ctx
            .AutoOrient()
            .Resize(new ResizeOptions
            {
                Size = new Size(1080, 1440),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));

        image.SaveAsPng(outputPath);
        log?.Invoke($"TikTok 3:4 封面已生成: {outputPath}");
        return outputPath;
    }

    public static string? ResolvePosterPath(string? workflowProjectDir, string? sourceProjectDir)
    {
        foreach (var root in new[] { workflowProjectDir, sourceProjectDir })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg" })
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }

        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
            return null;

        var imagePaths = Directory.GetFiles(workflowProjectDir)
            .Where(p => new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return imagePaths.Count > 0 ? Path.GetFullPath(imagePaths[0]) : null;
    }
}
