using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;

namespace ShortDrama.Infrastructure.Imaging;

public sealed class ProjectImageGenerator : IProjectImageGenerator
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".mkv",
        ".avi",
        ".flv",
        ".wmv",
        ".webm"
    };

    private readonly IExternalProcessRunner _processRunner;
    private readonly IProjectInfoParser _projectInfoParser;
    private readonly ILogger<ProjectImageGenerator> _logger;

    public ProjectImageGenerator(
        IExternalProcessRunner processRunner,
        IProjectInfoParser projectInfoParser,
        ILogger<ProjectImageGenerator> logger)
    {
        _processRunner = processRunner;
        _projectInfoParser = projectInfoParser;
        _logger = logger;
    }

    public async Task<ProjectImageGenerateResult> GenerateAsync(
        ProjectImageGenerateRequest request,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.InputDir))
        {
            throw new DirectoryNotFoundException($"宸ョ▼鍥捐緭鍏ヨ棰戠洰褰曚笉瀛樺湪: {request.InputDir}");
        }

        var templateDirectory = request.TemplateImageDir;
        if (string.IsNullOrWhiteSpace(templateDirectory))
        {
            throw new InvalidOperationException("鐢熸垚宸ョ▼鍥惧繀椤绘彁渚涙ā鏉跨洰褰曪紝骞朵笖鏍规枃浠跺す涓嬪繀椤诲惈鏈? template.json銆?");
        }

        Directory.CreateDirectory(request.OutputDir);
        await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);

        var manifest = ProjectImageTemplateManifest.Load(templateDirectory);
        var count = request.Count ?? LoadProjectImageCount(request.ConfigFile) ?? manifest.Count;
        if (count <= 0)
        {
            throw new InvalidOperationException($"宸ョ▼鍥炬暟閲忓繀椤诲ぇ浜?0锛屽綋鍓嶄负 {count}");
        }

        if (manifest.Templates.Count < count)
        {
            throw new InvalidOperationException(
                $"宸ョ▼鍥炬ā鏉跨殑椤甸潰鏁伴噺涓嶈冻锛氶渶瑕?{count} 寮狅紝浣嗗彧鎻愪緵浜?{manifest.Templates.Count} 寮犮€?");
        }

        var sourceVideo = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sourceVideo is null)
        {
            throw new InvalidOperationException($"鏈湪鐩綍涓壘鍒板彲鐢ㄨ棰戞枃浠? {request.InputDir}");
        }

        var ffmpeg = ResolveBinary("ffmpeg");
        var ffprobe = ResolveBinary("ffprobe");
        var durationSeconds = await GetDurationSecondsAsync(ffprobe, sourceVideo, cancellationToken);
        using var timelineStrip = await BuildTimelineStripAsync(ffmpeg, sourceVideo, durationSeconds, cancellationToken);

        var outputs = new List<string>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outputPath = Path.Combine(request.OutputDir, $"宸ョ▼鍥綺{index + 1}.png");
            if (File.Exists(outputPath) && !request.Overwrite)
            {
                outputs.Add(outputPath);
                continue;
            }

            var page = manifest.Templates[index];
            var templateImagePath = Path.Combine(templateDirectory, page.File);
            if (!File.Exists(templateImagePath))
            {
                throw new FileNotFoundException($"宸ョ▼鍥炬ā鏉跨己澶遍〉闈㈠浘鐗? {templateImagePath}");
            }

            var frameTime = CalculateFrameTime(durationSeconds, index + 1, count);
            using var previewFrame = await ExtractFrameAsync(ffmpeg, sourceVideo, frameTime, cancellationToken);
            using var templateImage = Image.Load<Rgba32>(templateImagePath);
            using var composite = ComposeTemplateBasedImage(
                templateImage,
                previewFrame,
                timelineStrip,
                page,
                index + 1,
                count,
                TimeSpan.FromSeconds(durationSeconds));

            composite.Save(outputPath, new PngEncoder());
            outputs.Add(outputPath);
            _logger.LogInformation("Generated project image {Index}/{Count}: {Path}", index + 1, count, outputPath);
        }

        return new ProjectImageGenerateResult(outputs.Count, outputs);
    }

    private static int? LoadProjectImageCount(string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(configFile))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!key.Equals("ProjectImageCount", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    private async Task<double> GetDurationSecondsAsync(
        string ffprobe,
        string videoPath,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ffprobe,
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                videoPath
            ],
            Path.GetDirectoryName(videoPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 鑾峰彇瑙嗛鏃堕暱澶辫触: {result.StandardError}");
        }

        if (!double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException($"鏃犳硶瑙ｆ瀽瑙嗛鏃堕暱: {result.StandardOutput}");
        }

        return seconds;
    }

    private async Task<Image<Rgba32>> BuildTimelineStripAsync(
        string ffmpeg,
        string videoPath,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        const int timelineWidth = 2890;
        const int timelineHeight = 120;
        const int itemCount = 24;
        const int gap = 8;
        const int thumbWidth = 112;
        const int thumbHeight = 84;

        var strip = new Image<Rgba32>(timelineWidth, timelineHeight, new Rgba32(20, 22, 25, 255));

        for (var index = 0; index < itemCount; index++)
        {
            var time = Math.Max(0.1, durationSeconds * (index + 1) / (itemCount + 1));
            using var frame = await ExtractFrameAsync(ffmpeg, videoPath, time, cancellationToken);
            frame.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(thumbWidth, thumbHeight),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center
            }));

            var x = 16 + index * (thumbWidth + gap);
            strip.Mutate(ctx => ctx.DrawImage(frame, new Point(x, 14), 1f));
        }

        return strip;
    }

    private async Task<Image<Rgba32>> ExtractFrameAsync(
        string ffmpeg,
        string videoPath,
        double timeSeconds,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"shortdrama-frame-{Guid.NewGuid():N}.png");

        try
        {
            var result = await _processRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-ss", timeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", videoPath,
                    "-frames:v", "1",
                    "-y",
                    tempPath
                ],
                Path.GetDirectoryName(videoPath),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 鎶藉抚澶辫触: {result.StandardError}");
            }

            return await Image.LoadAsync<Rgba32>(tempPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static Image<Rgba32> ComposeTemplateBasedImage(
        Image<Rgba32> templateImage,
        Image<Rgba32> previewFrame,
        Image<Rgba32> timelineStrip,
        ProjectImageTemplatePage page,
        int currentIndex,
        int totalCount,
        TimeSpan videoDuration)
    {
        var requiredRegions = new[] { "player", "material_panel", "timeline_strip" };
        foreach (var regionKey in requiredRegions)
        {
            if (!page.Regions.ContainsKey(regionKey))
            {
                throw new InvalidOperationException($"宸ョ▼鍥炬ā鏉跨己灏戝叧閿尯鍩燂細{regionKey} -> {page.File}");
            }
        }

        var canvas = templateImage.Clone();
        var playerRegion = page.Regions["player"];
        var materialPanelRegion = page.Regions["material_panel"];
        var timelineRegion = page.Regions["timeline_strip"];
        var markerRegion = page.Regions.TryGetValue("timeline_marker", out var providedMarker)
            ? providedMarker
            : new ProjectImageTemplateRegion(0, timelineRegion.Y, 6, timelineRegion.Height);

        using var playerFrame = previewFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(playerRegion.Width, playerRegion.Height),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
        using var timeline = timelineStrip.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(timelineRegion.Width, timelineRegion.Height),
            Mode = ResizeMode.Stretch
        }));
        using var materialCard = BuildMaterialPanelCard(
            previewFrame,
            videoDuration,
            currentIndex,
            materialPanelRegion.Width,
            materialPanelRegion.Height);
        using var marker = new Image<Rgba32>(
            Math.Max(1, markerRegion.Width),
            Math.Max(1, markerRegion.Height),
            new Rgba32(32, 207, 255, 255));

        canvas.Mutate(ctx =>
        {
            ctx.DrawImage(playerFrame, new Point(playerRegion.X, playerRegion.Y), 1f);
            ctx.DrawImage(materialCard, new Point(materialPanelRegion.X, materialPanelRegion.Y), 1f);
            ctx.DrawImage(timeline, new Point(timelineRegion.X, timelineRegion.Y), 1f);

            var progress = totalCount <= 1 ? 0.5d : (currentIndex - 1d) / Math.Max(1d, totalCount - 1d);
            var markerX = timelineRegion.X + (int)Math.Round(progress * Math.Max(0, timelineRegion.Width - marker.Width));
            ctx.DrawImage(marker, new Point(markerX, markerRegion.Y), 1f);
        });

        return canvas;
    }

    private static Image<Rgba32> BuildMaterialPanelCard(
        Image<Rgba32> sourceFrame,
        TimeSpan duration,
        int orderNumber,
        int width,
        int height)
    {
        var card = new Image<Rgba32>(width, height, new Rgba32(38, 38, 38, 255));
        var previewHeight = Math.Max(1, (int)Math.Round(height * 0.78));
        var footerHeight = Math.Max(24, height - previewHeight);

        using var preview = sourceFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, previewHeight),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
        card.Mutate(ctx =>
        {
            ctx.DrawImage(preview, new Point(0, 0), 1f);
            ctx.Fill(Color.Parse("#141414"), new RectangleF(0, previewHeight, width, footerHeight));
        });

        var fontFamily = TryFindCjkFontFamily();
        if (fontFamily is null)
        {
            return card;
        }

        var resolvedFontFamily = fontFamily.Value;
        var titleFont = resolvedFontFamily.CreateFont(Math.Max(12, height / 10f), FontStyle.Bold);
        var footerFont = resolvedFontFamily.CreateFont(Math.Max(11, height / 12f), FontStyle.Regular);

        card.Mutate(ctx =>
        {
            DrawCenteredText(ctx, "已添加", titleFont, Color.Parse("#dcdcdc"), 6, 6, Math.Max(70, width / 3), Math.Max(28, previewHeight / 7));
            DrawCenteredText(ctx, FormatDuration(duration), titleFont, Color.Parse("#ede9e5"), Math.Max(0, width - Math.Max(86, width / 3) - 6), 6, Math.Max(80, width / 3), Math.Max(28, previewHeight / 7));
            ctx.DrawText($"第{orderNumber:00}集 mp4", footerFont, Color.Parse("#d0d0d0"), new PointF(10, previewHeight + 10));
        });

        return card;
    }

    private static void DrawCenteredText(
        IImageProcessingContext ctx,
        string text,
        Font font,
        Color color,
        int x,
        int y,
        int width,
        int height)
    {
        var bounds = TextMeasurer.MeasureBounds(text, new TextOptions(font));
        var tx = x + (width - bounds.Width) / 2f - bounds.Left;
        var ty = y + (height - bounds.Height) / 2f - bounds.Top;
        ctx.DrawText(text, font, color, new PointF(tx, ty));
    }

    private static FontFamily? TryFindCjkFontFamily()
    {
        string[] candidates =
        [
            "Heiti SC", "STHeiti", "Microsoft YaHei", "Noto Sans CJK SC",
            "Noto Sans SC", "WenQuanYi Micro Hei", "Arial Unicode MS",
            "PingFang SC", "Arial"
        ];
        foreach (var name in candidates)
        {
            if (!SystemFonts.TryGet(name, out var family))
            {
                continue;
            }

            try
            {
                var probe = family.CreateFont(12, FontStyle.Regular);
                TextMeasurer.MeasureBounds("测试", new TextOptions(probe));
                return family;
            }
            catch
            {
                // Skip unusable fonts.
            }
        }

        return null;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static double CalculateFrameTime(double durationSeconds, int index, int count)
    {
        if (count <= 1)
        {
            return Math.Max(0.1, durationSeconds * 0.5);
        }

        return Math.Max(0.1, durationSeconds * (index / (count + 1d)));
    }

    private static string ResolveBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var extensions = OperatingSystem.IsWindows()
                ? new[] { ".exe", ".cmd", ".bat", string.Empty }
                : new[] { string.Empty };

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(dir, name + ext);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
        }

        throw new InvalidOperationException($"鏈壘鍒?{name}銆傝瀹夎 {name}锛屾垨纭繚鍏跺湪 PATH 涓€?");
    }
}
