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
            throw new DirectoryNotFoundException($"工程图输入视频目录不存在: {request.InputDir}");
        }

        Directory.CreateDirectory(request.OutputDir);

        var project = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var sourceVideo = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sourceVideo is null)
        {
            throw new InvalidOperationException($"未在目录中找到可用视频文件: {request.InputDir}");
        }

        var count = request.Count ?? LoadProjectImageCount(request.ConfigFile) ?? 5;
        if (count <= 0)
        {
            throw new InvalidOperationException($"工程图数量必须大于 0，当前为 {count}");
        }

        var ffmpeg = ResolveBinary("ffmpeg");
        var ffprobe = ResolveBinary("ffprobe");
        var durationSeconds = await GetDurationSecondsAsync(ffprobe, sourceVideo, cancellationToken);

        var outputs = new List<string>();

        using var timelineStrip = await BuildTimelineStripAsync(ffmpeg, sourceVideo, durationSeconds, cancellationToken);
        var templateImages = LoadTemplateImages(request.TemplateImageDir, count);
        try
        {
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputPath = Path.Combine(request.OutputDir, $"工程图_{index}.png");
                if (File.Exists(outputPath) && !request.Overwrite)
                {
                    outputs.Add(outputPath);
                    continue;
                }

                var frameTime = CalculateFrameTime(durationSeconds, index, count);
                using var previewFrame = await ExtractFrameAsync(ffmpeg, sourceVideo, frameTime, cancellationToken);
                var templateImage = templateImages[index - 1];
                using var composite = ComposeTemplateBasedImage(
                    templateImage,
                    previewFrame,
                    timelineStrip,
                    index,
                    count,
                    TimeSpan.FromSeconds(durationSeconds));

                composite.Save(outputPath, new PngEncoder());
                outputs.Add(outputPath);
                _logger.LogInformation("Generated project image {Index}/{Count}: {Path}", index, count, outputPath);
            }
        }
        finally
        {
            foreach (var img in templateImages)
            {
                img?.Dispose();
            }
        }

        return new ProjectImageGenerateResult(outputs.Count, outputs);
    }

    private static List<Image<Rgba32>> LoadTemplateImages(string? templateImageDir, int count)
    {
        if (string.IsNullOrWhiteSpace(templateImageDir))
        {
            throw new InvalidOperationException("生成工程图必须提供模板目录。请通过 --template-dir 或 config.txt 中的 ProjectImageTemplateDir 配置工程图模板目录。");
        }

        if (!Directory.Exists(templateImageDir))
        {
            throw new DirectoryNotFoundException($"工程图模板目录不存在: {templateImageDir}");
        }

        var result = new List<Image<Rgba32>>(count);
        for (var i = 1; i <= count; i++)
        {
            var path = Path.Combine(templateImageDir, $"工程图_{i}.png");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"工程图模板缺失: {path}");
            }

            result.Add(Image.Load<Rgba32>(path));
        }

        return result;
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

            return int.Parse(value, CultureInfo.InvariantCulture);
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
            throw new InvalidOperationException($"ffprobe 获取视频时长失败: {result.StandardError}");
        }

        if (!double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException($"无法解析视频时长: {result.StandardOutput}");
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
            var y = 14;
            strip.Mutate(ctx => ctx.DrawImage(frame, new Point(x, y), 1f));
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
                throw new InvalidOperationException($"FFmpeg 抽帧失败: {result.StandardError}");
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

    private static Image<Rgba32> ComposeEditorLikeImage(
        Image<Rgba32> previewFrame,
        Image<Rgba32> timelineStrip,
        int currentIndex,
        int totalCount)
    {
        var canvas = new Image<Rgba32>(3024, 1800, new Rgba32(28, 30, 34, 255));

        using var titleBar = new Image<Rgba32>(3024, 54, new Rgba32(12, 13, 15, 255));
        using var leftRail = new Image<Rgba32>(96, 1410, new Rgba32(24, 26, 29, 255));
        using var leftPanel = new Image<Rgba32>(690, 1410, new Rgba32(31, 33, 37, 255));
        using var rightPanel = new Image<Rgba32>(586, 1410, new Rgba32(34, 36, 40, 255));
        using var centerBg = new Image<Rgba32>(1652, 1410, new Rgba32(18, 20, 24, 255));
        using var playerTopBar = new Image<Rgba32>(1416, 76, new Rgba32(40, 42, 46, 255));
        using var playerFrame = new Image<Rgba32>(1416, 980, new Rgba32(12, 13, 15, 255));
        using var playerControlBar = new Image<Rgba32>(1416, 92, new Rgba32(32, 34, 38, 255));
        using var centerFrame = previewFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(1416, 796),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
        using var timelineBg = new Image<Rgba32>(3024, 336, new Rgba32(23, 25, 28, 255));
        using var timelineHeader = new Image<Rgba32>(3024, 44, new Rgba32(19, 21, 24, 255));
        using var marker = new Image<Rgba32>(6, 192, new Rgba32(32, 207, 255, 255));
        using var leftThumb = previewFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(250, 140),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
        using var cyanButton = new Image<Rgba32>(130, 56, new Rgba32(43, 193, 255, 255));
        using var darkButton = new Image<Rgba32>(164, 56, new Rgba32(45, 47, 52, 255));
        using var searchBox = new Image<Rgba32>(176, 48, new Rgba32(22, 24, 27, 255));
        using var thumbCard = new Image<Rgba32>(272, 190, new Rgba32(42, 44, 49, 255));
        using var rightTabActive = new Image<Rgba32>(108, 42, new Rgba32(66, 68, 74, 255));
        using var rightTab = new Image<Rgba32>(108, 42, new Rgba32(43, 45, 50, 255));
        using var rightRow = new Image<Rgba32>(470, 46, new Rgba32(39, 41, 46, 255));
        using var sliderTrack = new Image<Rgba32>(240, 8, new Rgba32(70, 72, 78, 255));
        using var sliderFill = new Image<Rgba32>(110, 8, new Rgba32(43, 193, 255, 255));
        using var miniButton = new Image<Rgba32>(34, 34, new Rgba32(52, 54, 59, 255));
        using var panelHeader = new Image<Rgba32>(1652, 76, new Rgba32(26, 28, 32, 255));
        using var leftSection = new Image<Rgba32>(560, 2, new Rgba32(56, 58, 63, 255));
        using var timeBadge = new Image<Rgba32>(128, 36, new Rgba32(18, 20, 24, 255));
        using var bottomToolbar = new Image<Rgba32>(3024, 42, new Rgba32(19, 21, 24, 255));

        canvas.Mutate(ctx =>
        {
            ctx.DrawImage(titleBar, new Point(0, 0), 1f);
            ctx.DrawImage(leftRail, new Point(0, 54), 1f);
            ctx.DrawImage(leftPanel, new Point(0, 54), 1f);
            ctx.DrawImage(centerBg, new Point(786, 54), 1f);
            ctx.DrawImage(rightPanel, new Point(2438, 54), 1f);
            ctx.DrawImage(timelineBg, new Point(0, 1464), 1f);
            ctx.DrawImage(timelineHeader, new Point(0, 1464), 1f);
            ctx.DrawImage(bottomToolbar, new Point(0, 1758), 1f);

            ctx.DrawImage(cyanButton, new Point(22, 164), 1f);
            ctx.DrawImage(darkButton, new Point(170, 164), 1f);
            ctx.DrawImage(searchBox, new Point(388, 168), 1f);
            ctx.DrawImage(thumbCard, new Point(224, 330), 1f);
            ctx.DrawImage(leftThumb, new Point(236, 342), 1f);
            ctx.DrawImage(leftSection, new Point(150, 284), 1f);
            ctx.DrawImage(leftSection, new Point(150, 584), 1f);
            ctx.DrawImage(leftSection, new Point(150, 812), 1f);

            ctx.DrawImage(panelHeader, new Point(786, 54), 1f);
            ctx.DrawImage(playerTopBar, new Point(882, 132), 1f);
            ctx.DrawImage(playerFrame, new Point(882, 208), 1f);
            ctx.DrawImage(centerFrame, new Point(882, 292), 1f);
            ctx.DrawImage(playerControlBar, new Point(882, 1112), 1f);
            ctx.DrawImage(timeBadge, new Point(908, 1140), 1f);

            ctx.DrawImage(rightTabActive, new Point(2514, 168), 1f);
            ctx.DrawImage(rightTab, new Point(2638, 168), 1f);
            ctx.DrawImage(rightTab, new Point(2762, 168), 1f);
            ctx.DrawImage(rightRow, new Point(2470, 258), 1f);
            ctx.DrawImage(rightRow, new Point(2470, 330), 1f);
            ctx.DrawImage(rightRow, new Point(2470, 468), 1f);
            ctx.DrawImage(rightRow, new Point(2470, 548), 1f);
            ctx.DrawImage(sliderTrack, new Point(2570, 404), 1f);
            ctx.DrawImage(sliderFill, new Point(2570, 404), 1f);
            ctx.DrawImage(miniButton, new Point(2834, 391), 1f);

            ctx.DrawImage(timelineStrip, new Point(62, 1546), 1f);

            var markerX = 108 + (int)Math.Round((currentIndex - 1d) / Math.Max(1, totalCount - 1d) * 2760);
            ctx.DrawImage(marker, new Point(markerX, 1522), 1f);
        });

        return canvas;
    }

    private static Image<Rgba32> ComposeTemplateBasedImage(
        Image<Rgba32> templateImage,
        Image<Rgba32> previewFrame,
        Image<Rgba32> timelineStrip,
        int currentIndex,
        int totalCount,
        TimeSpan videoDuration)
    {
        var canvas = templateImage.Clone();

        // Coordinates reverse-engineered from ShortDramaTools reference implementation:
        //   PlayImage:          (900,180)  1455×1080
        //   ThumbnailContainer: (230,295)  576×1050  items 240×194, margin 32, 2-column grid
        //   ProgressBar:        (267,1602) 2728×148  frame height 78
        //   Player header:      pixel scan shows y=156–179 (38,38,38)

        using var preview = previewFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(1455, 1080),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
        using var timeline = timelineStrip.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(2728, 78),
            Mode = ResizeMode.Stretch
        }));
        // Erase left panel thumbnail container.
        using var darkThumbAreaMask = new Image<Rgba32>(576, 1050, new Rgba32(38, 38, 38, 255));
        // Erase center player area including header bar.
        using var darkPlayerMask = new Image<Rgba32>(1480, 1120, new Rgba32(27, 27, 28, 255));
        using var marker = new Image<Rgba32>(6, 148, new Rgba32(32, 207, 255, 255));

        // Build the left-panel thumbnail item with badges and order text.
        using var thumbItem = BuildThumbnailItem(previewFrame, videoDuration, 1);

        canvas.Mutate(ctx =>
        {
            ctx.DrawImage(darkThumbAreaMask, new Point(230, 295), 1f);
            ctx.DrawImage(darkPlayerMask, new Point(880, 155), 1f);
            ctx.DrawImage(preview, new Point(900, 180), 1f);
            // First item slot: container(230,295) + padding(32,32)
            ctx.DrawImage(thumbItem, new Point(262, 327), 1f);
            ctx.DrawImage(timeline, new Point(267, 1637), 1f);
            var markerX = 267 + (int)Math.Round((currentIndex - 1d) / Math.Max(1, totalCount - 1d) * 2728);
            ctx.DrawImage(marker, new Point(markerX, 1602), 1f);
        });

        return canvas;
    }

    /// <summary>
    /// Builds a 240×194 thumbnail item card matching the reference ShortDramaTools layout:
    /// 240×160 cover (rounded r=15) with "已添加" left badge + duration right badge,
    /// followed by "第NN集.mp4" order text below.
    /// </summary>
    private static Image<Rgba32> BuildThumbnailItem(
        Image<Rgba32> videoFrame,
        TimeSpan duration,
        int orderNumber)
    {
        const int itemWidth = 240;
        const int itemHeight = 194;
        const int thumbWidth = 240;
        const int thumbHeight = 160;
        const float thumbRoundRadius = 15f;
        const int tagWidth = 76;
        const int tagHeight = 28;
        const float tagRound = 5f;
        const int leftTagX = 8;
        const int leftTagY = 8;
        const int rightTagX = 156; // thumbWidth - tagWidth - 8
        const int orderTextX = 10;
        const int orderTextY = 170; // thumbHeight + 10

        var item = new Image<Rgba32>(itemWidth, itemHeight, new Rgba32(38, 38, 38, 255));

        using var thumb = new Image<Rgba32>(thumbWidth, thumbHeight, new Rgba32(59, 59, 59, 255));

        // Letterbox-scale video frame into the thumbnail area.
        using var scaled = videoFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(thumbWidth, thumbHeight),
            Mode = ResizeMode.BoxPad,
            Position = AnchorPositionMode.Center,
            PadColor = Color.Parse("#3b3b3b")
        }));
        thumb.Mutate(ctx => ctx.DrawImage(scaled, new Point(0, 0), 1f));

        // Clip to rounded rectangle.
        ApplyRoundedCorners(thumb, thumbRoundRadius);

        // Draw "已添加" and duration badges on the thumbnail.
        thumb.Mutate(ctx =>
        {
            ctx.Fill(Color.Parse("#141414"), pb =>
                FillRoundedRect(pb, leftTagX, leftTagY, tagWidth, tagHeight, tagRound));

            var fontFamily = TryFindCjkFontFamily();
            if (fontFamily is { } ff)
            {
                var bold20 = ff.CreateFont(20, FontStyle.Bold);
                var bold16 = ff.CreateFont(16, FontStyle.Bold);

                DrawCenteredText(ctx, "已添加", bold20, Color.Parse("#dcdcdc"),
                    leftTagX, leftTagY, tagWidth, tagHeight);

                DrawCenteredText(ctx, FormatDuration(duration), bold16, Color.Parse("#ede9e5"),
                    rightTagX, leftTagY, tagWidth, tagHeight);
            }
        });

        item.Mutate(ctx => ctx.DrawImage(thumb, new Point(0, 0), 1f));

        // Draw order text below thumbnail.
        item.Mutate(ctx =>
        {
            var fontFamily = TryFindCjkFontFamily();
            if (fontFamily is { } ff)
            {
                var font22 = ff.CreateFont(22, FontStyle.Regular);
                ctx.DrawText($"第{orderNumber:00}集.mp4", font22, Color.Parse("#808080"),
                    new PointF(orderTextX, orderTextY));
            }
        });

        return item;
    }

    private static void DrawCenteredText(
        IImageProcessingContext ctx,
        string text,
        Font font,
        Color color,
        int x, int y, int width, int height)
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
                continue;
            try
            {
                // Verify the font can actually render CJK text before returning it.
                var probe = family.CreateFont(12, FontStyle.Regular);
                TextMeasurer.MeasureBounds("已", new TextOptions(probe));
                return family;
            }
            catch
            {
                // Font exists but has incompatible tables; try the next candidate.
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

    /// <summary>
    /// Builds a rounded rectangle path into the given <see cref="SixLabors.ImageSharp.Drawing.PathBuilder"/>.
    /// Uses SVG-style arc segments for each corner.
    /// </summary>
    private static void FillRoundedRect(
        SixLabors.ImageSharp.Drawing.PathBuilder pb,
        float x, float y, float w, float h, float r)
    {
        pb.MoveTo(new PointF(x + r, y));
        pb.LineTo(new PointF(x + w - r, y));
        pb.ArcTo(r, r, 0, false, true, new PointF(x + w, y + r));
        pb.LineTo(new PointF(x + w, y + h - r));
        pb.ArcTo(r, r, 0, false, true, new PointF(x + w - r, y + h));
        pb.LineTo(new PointF(x + r, y + h));
        pb.ArcTo(r, r, 0, false, true, new PointF(x, y + h - r));
        pb.LineTo(new PointF(x, y + r));
        pb.ArcTo(r, r, 0, false, true, new PointF(x + r, y));
        pb.CloseFigure();
    }

    private static void ApplyRoundedCorners(Image<Rgba32> image, float cornerRadius)
    {
        // Draw a white rounded rectangle on a grayscale mask, then apply it as alpha.
        using var mask = new Image<L8>(image.Width, image.Height, new L8(0));
        mask.Mutate(ctx =>
        {
            ctx.Fill(Color.White, pb =>
                FillRoundedRect(pb, 0, 0, image.Width, image.Height, cornerRadius));
        });

        for (var py = 0; py < image.Height; py++)
        {
            for (var px = 0; px < image.Width; px++)
            {
                var pixel = image[px, py];
                pixel.A = mask[px, py].PackedValue;
                image[px, py] = pixel;
            }
        }
    }

    private static double CalculateFrameTime(double durationSeconds, int index, int count)
    {
        if (count <= 1)
        {
            return Math.Max(0.1, durationSeconds * 0.5);
        }

        var progress = index / (count + 1d);
        return Math.Max(0.1, durationSeconds * progress);
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

        throw new InvalidOperationException($"未找到 {name}。请安装 {name}，或确保其在 PATH 中。");
    }
}
