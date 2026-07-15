using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure;
using ShortDrama.Infrastructure.Config;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Imaging;

public sealed class ProjectImageGenerator : IProjectImageGenerator
{
    private const string SubtitleCacheFileName = ".project_image_subtitle_cache.json";
    private const string SubtitleAiCacheFileName = ".project_image_subtitle_ai_cache.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

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
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectImageGenerator> _logger;

    public ProjectImageGenerator(
        IExternalProcessRunner processRunner,
        IProjectInfoParser projectInfoParser,
        HttpClient httpClient,
        ILogger<ProjectImageGenerator> logger)
    {
        _processRunner = processRunner;
        _projectInfoParser = projectInfoParser;
        _httpClient = httpClient;
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

        var templateDirectory = request.TemplateImageDir;
        if (string.IsNullOrWhiteSpace(templateDirectory))
        {
            throw new InvalidOperationException("生成工程图必须提供模板目录，并且模板目录下必须包含 template.json。");
        }

        Directory.CreateDirectory(request.OutputDir);
        var projectInfo = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var manifest = ProjectImageTemplateManifest.Load(templateDirectory);
        var configMap = !string.IsNullOrWhiteSpace(request.ConfigFile) && File.Exists(request.ConfigFile)
            ? KeyValueConfigReader.Read(request.ConfigFile)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var count = request.Count ?? LoadProjectImageCount(request.ConfigFile) ?? manifest.Count;
        if (count <= 0)
        {
            throw new InvalidOperationException($"工程图数量必须大于 0，当前值为 {count}。");
        }

        if (manifest.Templates.Count == 0)
        {
            throw new InvalidOperationException("工程图模板清单中没有可用页面。");
        }

        var sourceVideos = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var renderEpisodeLimit = LoadProjectImageRenderEpisodeLimit(configMap);
        if (renderEpisodeLimit is > 0 && sourceVideos.Length > renderEpisodeLimit.Value)
        {
            sourceVideos = sourceVideos.Take(renderEpisodeLimit.Value).ToArray();
        }
        if (sourceVideos.Length == 0)
        {
            throw new InvalidOperationException($"未在目录中找到可用视频文件: {request.InputDir}");
        }

        var ffmpeg = ResolveBinary("ffmpeg");
        var ffprobe = ResolveBinary("ffprobe");
        var episodeNames = sourceVideos
            .Select((path, index) =>
                index < (request.EpisodeNames?.Count ?? 0) &&
                !string.IsNullOrWhiteSpace(request.EpisodeNames![index])
                    ? request.EpisodeNames[index].Trim()
                    : Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .ToArray();
        var episodeDurations = new List<double>(sourceVideos.Length);
        var episodeFrames = new List<Image<Rgba32>>(sourceVideos.Length);

        try
        {
            foreach (var sourceVideo in sourceVideos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var duration = await GetDurationSecondsAsync(ffprobe, sourceVideo, cancellationToken);
                episodeDurations.Add(duration);
                episodeFrames.Add(await ExtractFrameAsync(
                    ffmpeg,
                    sourceVideo,
                    ResolveEpisodePreviewTime(duration),
                    cancellationToken));
            }

            var portrait = episodeFrames[0].Height > episodeFrames[0].Width;
            var outputs = new List<string>(count);

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputPath = Path.Combine(request.OutputDir, $"工程图_{index + 1}.png");
                if (File.Exists(outputPath) && !request.Overwrite)
                {
                    outputs.Add(outputPath);
                    continue;
                }

                var page = manifest.Templates[index % manifest.Templates.Count];
                if (!SupportsModernTemplate(page))
                {
                    throw new InvalidOperationException($"仅支持新版图片模板工程图，模板页缺少新版区域定义：{page.File}");
                }

                var templateImagePath = Path.Combine(templateDirectory, page.File);
                if (!File.Exists(templateImagePath))
                {
                    throw new FileNotFoundException($"工程图模板图片不存在: {templateImagePath}");
                }

                using var templateImage = Image.Load<Rgba32>(templateImagePath);
                using var composite = await ComposeModernTemplateImageAsync(
                    templateImage,
                    page,
                    manifest,
                    projectInfo,
                    request.ProjectDir,
                    outputPath,
                    configMap,
                    sourceVideos,
                    episodeNames,
                    episodeDurations,
                    episodeFrames,
                    ffmpeg,
                    count,
                    index + 1,
                    portrait,
                    cancellationToken);

                PrepareScreenshotMetadata(composite);
                composite.Save(outputPath, new PngEncoder
                {
                    ColorType = PngColorType.Rgb
                });
                outputs.Add(outputPath);
                _logger.LogInformation("Generated project image {Index}/{Count}: {Path}", index + 1, count, outputPath);
            }

            return new ProjectImageGenerateResult(outputs.Count, outputs);
        }
        finally
        {
            foreach (var frame in episodeFrames)
            {
                frame.Dispose();
            }
        }
    }

    private static void PrepareScreenshotMetadata(Image<Rgba32> image)
    {
        image.Metadata.HorizontalResolution = 96.012;
        image.Metadata.VerticalResolution = 96.012;
        image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;

        var pngMetadata = image.Metadata.GetPngMetadata();
        pngMetadata.TextData.Clear();
        pngMetadata.TextData.Add(new PngTextData("Software", "Snipaste", string.Empty, string.Empty));
        pngMetadata.TextData.Add(new PngTextData("User Comment", "Screenshot", string.Empty, string.Empty));
    }

    private static bool SupportsModernTemplate(ProjectImageTemplatePage page)
    {
        return page.HasRegion("video_track_images") ||
               page.HasRegion("top_title") ||
               page.HasRegion("autosave_status") ||
               page.HasRegion("right_subtitle");
    }

    private async Task<Image<Rgba32>> ComposeModernTemplateImageAsync(
        Image<Rgba32> templateImage,
        ProjectImageTemplatePage page,
        ProjectImageTemplateManifest manifest,
        ProjectInfo projectInfo,
        string projectDir,
        string outputPath,
        IReadOnlyDictionary<string, string> configMap,
        IReadOnlyList<string> sourceVideos,
        IReadOnlyList<string> episodeNames,
        IReadOnlyList<double> episodeDurations,
        IReadOnlyList<Image<Rgba32>> episodeFrames,
        string ffmpeg,
        int outputCount,
        int currentIndex,
        bool portrait,
        CancellationToken cancellationToken)
    {
        var canvas = templateImage.Clone();
        var pageIndex = ResolvePageIndex(manifest, page);
        var videoTrackRects = page.GetRegions("video_track_images");
        var trackEpisodeIndex = ResolveTrackEpisodeIndex(pageIndex, currentIndex, episodeNames.Count, episodeFrames.Count, videoTrackRects);
        var trackEpisodeFrames = videoTrackRects.Count > 0
            ? await ExtractTrackFramesAsync(
                ffmpeg,
                sourceVideos[Math.Clamp(trackEpisodeIndex, 0, sourceVideos.Count - 1)],
                episodeDurations[Math.Clamp(trackEpisodeIndex, 0, episodeDurations.Count - 1)],
                videoTrackRects[0],
                currentIndex,
                outputCount,
                cancellationToken)
            : null;

        try
        {
            var displayFrames = ResolveVideoTrackDisplayFrames(videoTrackRects, episodeFrames, trackEpisodeIndex + 1, trackEpisodeFrames);
            var previewFrame = SelectTrackPlayheadFrame(videoTrackRects, displayFrames)
                ?? episodeFrames[Math.Clamp(trackEpisodeIndex, 0, episodeFrames.Count - 1)];
            var playerRect = ResolvePlayerRegion(page, portrait);
            using var subtitleProbe = playerRect is not null
                ? BuildPlayerImage(
                    previewFrame,
                    playerRect,
                    portrait ? new Rgba32(0, 0, 0, 255) : SampleRectColor(canvas, playerRect))
                : previewFrame.Clone();
            var subtitleText = await ResolveSubtitleTextAsync(
                projectDir,
                outputPath,
                configMap,
                subtitleProbe,
                sourceVideos[Math.Clamp(trackEpisodeIndex, 0, sourceVideos.Count - 1)],
                currentIndex,
                page,
                episodeNames,
                projectInfo,
                trackEpisodeIndex,
                cancellationToken);

            RenderAutosaveStatus(canvas, page, pageIndex, manifest.Templates.Count);
            RenderTopTitle(canvas, page, projectInfo.Title);
            RenderPlayer(canvas, page, previewFrame, portrait);
            RenderRightSubtitle(canvas, page, portrait, subtitleText);
            RenderMaterialPanel(canvas, page, episodeFrames, episodeNames, episodeDurations, projectInfo.Title);
            RenderVideoTrackImages(canvas, videoTrackRects, episodeFrames, trackEpisodeIndex + 1, trackEpisodeFrames);
            RenderTrackTexts(canvas, page, pageIndex, currentIndex, episodeNames, episodeDurations, projectInfo.Title, episodeFrames.Count, videoTrackRects, subtitleText);

            return canvas;
        }
        finally
        {
            if (trackEpisodeFrames is not null)
            {
                foreach (var frame in trackEpisodeFrames)
                {
                    frame.Dispose();
                }
            }
        }
    }

    private static int ResolvePageIndex(ProjectImageTemplateManifest manifest, ProjectImageTemplatePage page)
    {
        for (var index = 0; index < manifest.Templates.Count; index++)
        {
            if (ReferenceEquals(manifest.Templates[index], page) ||
                string.Equals(manifest.Templates[index].File, page.File, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static void RenderAutosaveStatus(
        Image<Rgba32> canvas,
        ProjectImageTemplatePage page,
        int pageIndex,
        int pageCount)
    {
        var rect = page.GetRegion("autosave_status");
        if (rect is null)
        {
            return;
        }

        FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));

        var lastIndex = Math.Max(0, pageCount - 1);
        var minutesAgo = (lastIndex - Math.Max(0, pageIndex)) * 7 + Math.Max(0, pageIndex) % 3 * 2;
        var generatedAt = DateTime.Now.AddMinutes(-minutesAgo);
        var timeFormat = ConvertPythonTimeFormat(NoteValue(rect, "format") ?? "%H:%M:%S");
        string timeText;
        try
        {
            timeText = generatedAt.ToString(timeFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            timeText = generatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var suffix = NoteValue(rect, "suffix") ?? "自动保存本地";
        var text = $"{timeText} {suffix}".Trim();
        var fill = ParseHexColor(NoteValue(rect, "fill") ?? NoteValue(rect, "color")) ?? new Rgba32(164, 169, 174, 255);
        var fontSize = NoteInt(rect, "font_size", 12, 8, 28);
        var offsetX = NoteInt(rect, "text_x_offset", 0, -80, 180);

        var x = Math.Clamp(rect.X + offsetX, 0, Math.Max(0, canvas.Width - 1));
        var maximumWidth = Math.Max(1, rect.X + rect.Width - x - 2);
        var font = GetFont(fontSize, false);
        var finalText = FitTextEnd(text, font, maximumWidth);
        var bounds = TextMeasurer.MeasureBounds(finalText, new TextOptions(font));
        var textHeight = (int)Math.Ceiling(bounds.Height);
        var y = rect.Y + Math.Max(0, (rect.Height - textHeight) / 2) - (float)Math.Floor(bounds.Top);
        canvas.Mutate(ctx => ctx.DrawText(finalText, font, fill, new PointF(x, y)));
    }

    private static string ConvertPythonTimeFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format) || !format.Contains('%'))
        {
            return string.IsNullOrWhiteSpace(format) ? "HH:mm:ss" : format;
        }

        return format
            .Replace("%Y", "yyyy", StringComparison.Ordinal)
            .Replace("%y", "yy", StringComparison.Ordinal)
            .Replace("%m", "MM", StringComparison.Ordinal)
            .Replace("%d", "dd", StringComparison.Ordinal)
            .Replace("%H", "HH", StringComparison.Ordinal)
            .Replace("%M", "mm", StringComparison.Ordinal)
            .Replace("%S", "ss", StringComparison.Ordinal);
    }

    private static void RenderTopTitle(Image<Rgba32> canvas, ProjectImageTemplatePage page, string title)
    {
        var rect = page.GetRegion("top_title");
        if (rect is null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var expanded = ExpandTopTitleRect(canvas, rect, title);
        FillRect(canvas, expanded, SampleSurroundingColor(canvas, expanded));
        DrawSingleLineText(
            canvas,
            expanded,
            title,
            new Rgba32(238, 238, 238, 255),
            Math.Max(11, Math.Min(18, expanded.Height - 3)),
            true,
            "center");
    }

    private static void RenderPlayer(Image<Rgba32> canvas, ProjectImageTemplatePage page, Image<Rgba32> previewFrame, bool portrait)
    {
        var rect = ResolvePlayerRegion(page, portrait);
        if (rect is null)
        {
            return;
        }

        var letterboxColor = portrait
            ? new Rgba32(0, 0, 0, 255)
            : SampleRectColor(canvas, rect);
        FillRect(canvas, rect, SampleRectColor(canvas, rect));
        using var playerImage = BuildPlayerImage(previewFrame, rect, letterboxColor);
        canvas.Mutate(ctx => ctx.DrawImage(playerImage, new Point(rect.X, rect.Y), 1f));
    }

    private static ProjectImageTemplateRegion? ResolvePlayerRegion(ProjectImageTemplatePage page, bool portrait)
    {
        return (!portrait ? page.GetRegion("player_landscape") : null) ?? page.GetRegion("player");
    }

    private static void RenderRightSubtitle(Image<Rgba32> canvas, ProjectImageTemplatePage page, bool portrait, string subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return;
        }

        var rect = (!portrait ? page.GetRegion("right_subtitle_landscape") : null) ?? page.GetRegion("right_subtitle");
        if (rect is null)
        {
            return;
        }

        FillRect(canvas, rect, SampleRectColor(canvas, rect));
        DrawWrappedText(
            canvas,
            rect,
            subtitle,
            new Rgba32(235, 235, 235, 255),
            Math.Max(8, Math.Min(11, rect.Height / 4 + 1)),
            false,
            "left",
            "top");
    }

    private static void RenderMaterialPanel(
        Image<Rgba32> canvas,
        ProjectImageTemplatePage page,
        IReadOnlyList<Image<Rgba32>> episodeFrames,
        IReadOnlyList<string> episodeNames,
        IReadOnlyList<double> episodeDurations,
        string projectTitle)
    {
        var rect = page.GetRegion("material_panel");
        if (rect is null || episodeFrames.Count == 0)
        {
            return;
        }

        FillRect(canvas, rect, SampleRectColor(canvas, rect));

        var columns = rect.Width >= rect.Height ? 2 : 1;
        var rows = Math.Max(1, Math.Min(3, (int)Math.Ceiling(Math.Min(episodeFrames.Count, 6) / (double)columns)));
        const int gap = 10;
        var cellWidth = Math.Max(1, (rect.Width - gap * (columns + 1)) / columns);
        var cellHeight = Math.Max(1, (rect.Height - gap * (rows + 1)) / rows);
        var itemCount = Math.Min(episodeFrames.Count, rows * columns);

        for (var index = 0; index < itemCount; index++)
        {
            var row = index / columns;
            var col = index % columns;
            var x = rect.X + gap + col * (cellWidth + gap);
            var y = rect.Y + gap + row * (cellHeight + gap);

            using var card = BuildTrackThumbnailCard(
                episodeFrames[index],
                ResolveTrackEpisodeTextWithDuration(index, episodeNames, episodeDurations, projectTitle),
                cellWidth,
                cellHeight);
            canvas.Mutate(ctx => ctx.DrawImage(card, new Point(x, y), 1f));
        }
    }

    private static void RenderVideoTrackImages(
        Image<Rgba32> canvas,
        IReadOnlyList<ProjectImageTemplateRegion> rects,
        IReadOnlyList<Image<Rgba32>> episodeFrames,
        int currentIndex,
        IReadOnlyList<Image<Rgba32>>? singleEpisodeTrackFrames)
    {
        if (rects.Count == 0)
        {
            return;
        }

        var frames = ResolveVideoTrackDisplayFrames(rects, episodeFrames, currentIndex, singleEpisodeTrackFrames);
        if (frames.Count == 0)
        {
            return;
        }

        if (rects.Count == 1 && rects[0].Width >= rects[0].Height * 3)
        {
            RenderVideoTrackStrip(canvas, rects[0], frames);
            return;
        }

        foreach (var rect in rects)
        {
            FillRect(canvas, rect, new Rgba32(18, 22, 26, 255));
            using var thumb = ResizeCrop(frames[0], rect.Width, rect.Height);
            canvas.Mutate(ctx => ctx.DrawImage(thumb, new Point(rect.X, rect.Y), 1f));
        }
    }

    private static void RenderVideoTrackStrip(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        IReadOnlyList<Image<Rgba32>> frames)
    {
        using var playheadOverlay = CapturePlayheadOverlay(canvas, rect);
        var thumbHeight = GetTrackThumbnailHeight(rect);
        var thumbY = Math.Max(0, (rect.Height - thumbHeight) / 2);
        var clipWidth = Math.Max(28, Math.Min(72, (int)Math.Round(thumbHeight * 1.35)));
        var clipCount = Math.Max(1, (int)Math.Ceiling(rect.Width / (double)clipWidth));

        using var strip = new Image<Rgba32>(rect.Width, rect.Height, SampleVideoTrackBackground(canvas, rect, thumbY, thumbHeight));
        for (var index = 0; index < clipCount; index++)
        {
            var x = index * clipWidth;
            var width = Math.Min(clipWidth, rect.Width - x);
            if (width <= 0)
            {
                break;
            }

            using var thumb = ResizeCrop(frames[index % frames.Count], clipWidth, thumbHeight);
            if (width != clipWidth)
            {
                using var partial = thumb.Clone(ctx => ctx.Crop(new Rectangle(0, 0, width, thumbHeight)));
                strip.Mutate(ctx => ctx.DrawImage(partial, new Point(x, thumbY), 1f));
            }
            else
            {
                strip.Mutate(ctx => ctx.DrawImage(thumb, new Point(x, thumbY), 1f));
            }
        }

        if (playheadOverlay is not null)
        {
            strip.Mutate(ctx => ctx.DrawImage(playheadOverlay, Point.Empty, 1f));
        }

        canvas.Mutate(ctx => ctx.DrawImage(strip, new Point(rect.X, rect.Y), 1f));
    }

    private static void RenderTrackTexts(
        Image<Rgba32> canvas,
        ProjectImageTemplatePage page,
        int pageIndex,
        int currentIndex,
        IReadOnlyList<string> episodeNames,
        IReadOnlyList<double> episodeDurations,
        string projectTitle,
        int episodeFrameCount,
        IReadOnlyList<ProjectImageTemplateRegion> videoTrackRects,
        string subtitleText)
    {
        var trackEpisodeIndex = ResolveTrackEpisodeIndex(pageIndex, currentIndex, episodeNames.Count, episodeFrameCount, videoTrackRects);
        var trackText = ResolveTrackEpisodeText(trackEpisodeIndex, episodeNames, projectTitle);
        var trackTextWithDuration = ResolveTrackEpisodeTextWithDuration(trackEpisodeIndex, episodeNames, episodeDurations, projectTitle);
        var videoTrackTextRects = page.GetRegions("video_track_texts");
        var audioTrackTextRects = page.GetRegions("audio_track_texts");
        var hideTrackText = ShouldHideTrackText(videoTrackRects);

        foreach (var rect in page.GetRegions("subtitle_track_texts"))
        {
            FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));
            DrawSingleLineText(canvas, rect, subtitleText, new Rgba32(245, 245, 245, 255), 11, false, "left", truncateMiddle: false);
        }

        foreach (var rect in videoTrackTextRects)
        {
            EraseVideoTrackTextRect(canvas, rect, videoTrackRects);
        }

        foreach (var rect in audioTrackTextRects)
        {
            EraseAudioTrackTextRect(canvas, rect);
        }

        if (videoTrackRects.Count > 0 && videoTrackTextRects.Count > 0)
        {
            RestoreVideoTrackSegmentBoundaries(canvas, videoTrackRects, videoTrackTextRects, audioTrackTextRects);
        }

        if (hideTrackText)
        {
            RestoreVideoTrackPlayheadOverlay(canvas, videoTrackRects);
            return;
        }

        foreach (var rect in videoTrackTextRects)
        {
            var drawRect = ShiftRect(
                canvas,
                ExtendTrackTextRect(rect, videoTrackRects, canvas.Width),
                dy: NoteInt(rect, "draw_dy", -4, -40, 40));
            DrawTrackSingleLineText(
                canvas,
                drawRect,
                trackTextWithDuration,
                new Rgba32(245, 245, 245, 255),
                11,
                false);
        }

        foreach (var rect in audioTrackTextRects)
        {
            var drawRect = ShiftRect(
                canvas,
                ExtendTrackTextRect(rect, videoTrackRects, canvas.Width),
                dy: NoteInt(rect, "draw_dy", -3, -40, 40));
            DrawTrackSingleLineText(
                canvas,
                drawRect,
                trackText,
                new Rgba32(245, 245, 245, 255),
                11,
                false);
        }

        RestoreVideoTrackPlayheadOverlay(canvas, videoTrackRects);
    }

    private async Task<List<Image<Rgba32>>> ExtractTrackFramesAsync(
        string ffmpeg,
        string videoPath,
        double durationSeconds,
        ProjectImageTemplateRegion rect,
        int currentPage,
        int pageCount,
        CancellationToken cancellationToken)
    {
        var thumbHeight = GetTrackThumbnailHeight(rect);
        var clipWidth = Math.Max(28, Math.Min(72, (int)Math.Round(thumbHeight * 1.35)));
        var clipCount = Math.Max(1, (int)Math.Ceiling(rect.Width / (double)clipWidth));
        var sampleCount = Math.Max(14, clipCount);
        var focusTime = pageCount <= 1
            ? Math.Max(0.1, durationSeconds * 0.5)
            : Math.Max(0.1, durationSeconds * currentPage / (pageCount + 1d));
        var playheadSlot = ResolveVideoTrackPlayheadSlot(rect, sampleCount);
        var sampleTimes = BuildTrackSampleTimes(durationSeconds, sampleCount, focusTime, playheadSlot);
        var frames = new Image<Rgba32>?[sampleTimes.Count];
        using var semaphore = new SemaphoreSlim(Math.Min(4, sampleTimes.Count));
        var tasks = sampleTimes.Select(async (time, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                frames[index] = await ExtractFrameAsync(ffmpeg, videoPath, time, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
            return frames.Where(static frame => frame is not null).Select(static frame => frame!).ToList();
        }
        catch
        {
            foreach (var frame in frames)
            {
                frame?.Dispose();
            }

            throw;
        }
    }

    private static IReadOnlyList<double> BuildTrackSampleTimes(
        double durationSeconds,
        int sampleCount,
        double focusTimeSeconds,
        int? playheadSlot)
    {
        var duration = Math.Max(0.1, durationSeconds);
        var count = Math.Clamp(sampleCount <= 0 ? 18 : sampleCount, 4, 72);
        var maximumTime = Math.Max(0.1, duration - 0.1);
        var focus = Math.Clamp(focusTimeSeconds, 0.1, maximumTime);
        var slot = Math.Clamp(playheadSlot ?? count / 2, 0, count - 1);
        var localWindow = Math.Min(Math.Max(18d, duration * 0.32), 72d);
        var step = Math.Max(0.8, localWindow / Math.Max(1, count - 1));
        var startTime = focus - slot * step;
        var sampleTimes = new double[count];

        for (var index = 0; index < count; index++)
        {
            sampleTimes[index] = Math.Clamp(startTime + index * step, 0.1, maximumTime);
        }

        sampleTimes[slot] = focus;
        return sampleTimes;
    }

    private static int? ResolveVideoTrackPlayheadSlot(ProjectImageTemplateRegion rect, int frameCount)
    {
        if (frameCount <= 0 || rect.Width < rect.Height * 3)
        {
            return null;
        }

        var playheadX = ResolveConfiguredPlayheadX(rect);
        if (playheadX is null || playheadX.Value < rect.X || playheadX.Value >= rect.X + rect.Width)
        {
            return null;
        }

        var thumbHeight = GetTrackThumbnailHeight(rect);
        var clipWidth = Math.Max(28, Math.Min(72, (int)Math.Round(thumbHeight * 1.35)));
        var localX = Math.Clamp(playheadX.Value - rect.X, 0, rect.Width - 1);
        return localX / clipWidth % frameCount;
    }

    private static IReadOnlyList<Image<Rgba32>> ResolveVideoTrackDisplayFrames(
        IReadOnlyList<ProjectImageTemplateRegion> rects,
        IReadOnlyList<Image<Rgba32>> episodeFrames,
        int currentIndex,
        IReadOnlyList<Image<Rgba32>>? singleEpisodeTrackFrames)
    {
        if (singleEpisodeTrackFrames is not null && singleEpisodeTrackFrames.Count > 0)
        {
            return singleEpisodeTrackFrames;
        }

        if (episodeFrames.Count == 0)
        {
            return Array.Empty<Image<Rgba32>>();
        }

        return new[] { episodeFrames[Math.Clamp(currentIndex - 1, 0, episodeFrames.Count - 1)] };
    }

    private static Image<Rgba32>? SelectTrackPlayheadFrame(
        IReadOnlyList<ProjectImageTemplateRegion> rects,
        IReadOnlyList<Image<Rgba32>> frames)
    {
        if (rects.Count != 1 || frames.Count == 0)
        {
            return null;
        }

        var rect = rects[0];
        if (rect.Width < rect.Height * 3)
        {
            return null;
        }

        var playheadX = NoteInt(rect, "playhead_x", int.MinValue, -10000, 10000);
        if (playheadX == int.MinValue)
        {
            return null;
        }

        playheadX += NoteInt(rect, "playhead_x_offset", 0, -20, 20);
        if (playheadX < rect.X || playheadX >= rect.X + rect.Width)
        {
            return null;
        }

        var thumbHeight = GetTrackThumbnailHeight(rect);
        var clipWidth = Math.Max(28, Math.Min(72, (int)Math.Round(thumbHeight * 1.35)));
        var frameIndex = ((playheadX - rect.X) / clipWidth) % frames.Count;
        return frames[frameIndex];
    }

    private static ProjectImageTemplateRegion ExpandTopTitleRect(Image<Rgba32> canvas, ProjectImageTemplateRegion rect, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return rect;
        }

        var targetWidth = Math.Min(canvas.Width, Math.Max(rect.Width, Math.Max(640, Math.Min(1120, text.Length * 48))));
        var centerX = rect.X + rect.Width / 2;
        var x = Math.Max(0, Math.Min(canvas.Width - targetWidth, centerX - targetWidth / 2));
        return rect with { X = x, Width = targetWidth };
    }

    private static Image<Rgba32> BuildPlayerImage(
        Image<Rgba32> previewFrame,
        ProjectImageTemplateRegion rect,
        Rgba32 letterboxColor)
    {
        var canvas = new Image<Rgba32>(rect.Width, rect.Height, new Rgba32(0, 0, 0, 255));
        using var sourceFrame = CropPlayerHorizontalMargins(previewFrame, letterboxColor);
        if (sourceFrame.Width <= sourceFrame.Height)
        {
            using var portraitContent = ResizeCrop(sourceFrame, rect.Width, rect.Height);
            canvas.Mutate(ctx => ctx.DrawImage(portraitContent, Point.Empty, 1f));
            return canvas;
        }

        var fitMode = (NoteValue(rect, "fit") ?? NoteValue(rect, "player_fit") ?? string.Empty).Trim().ToLowerInvariant();
        if (fitMode is "crop" or "cover" or "fill")
        {
            using var croppedContent = ResizeCrop(sourceFrame, rect.Width, rect.Height);
            canvas.Mutate(ctx => ctx.DrawImage(croppedContent, Point.Empty, 1f));
            return canvas;
        }

        var ratio = sourceFrame.Width / (double)Math.Max(1, sourceFrame.Height);
        var targetWidth = rect.Width;
        var targetHeight = Math.Max(1, Math.Min(rect.Height, (int)Math.Round(targetWidth / ratio)));
        if (targetHeight < Math.Max(1, rect.Height / 4))
        {
            targetHeight = Math.Max(1, rect.Height / 4);
            targetWidth = Math.Max(1, Math.Min(rect.Width, (int)Math.Round(targetHeight * ratio)));
        }

        using var content = sourceFrame.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        var x = (rect.Width - targetWidth) / 2;
        var y = (rect.Height - targetHeight) / 2;
        if (y > 0)
        {
            canvas.Mutate(ctx =>
            {
                ctx.Fill(letterboxColor, new RectangleF(0, 0, rect.Width, y));
                var bottomHeight = rect.Height - y - targetHeight;
                if (bottomHeight > 0)
                {
                    ctx.Fill(letterboxColor, new RectangleF(0, y + targetHeight, rect.Width, bottomHeight));
                }
            });
        }

        canvas.Mutate(ctx => ctx.DrawImage(content, new Point(x, y), 1f));
        return canvas;
    }

    private static Image<Rgba32> CropPlayerHorizontalMargins(Image<Rgba32> previewFrame, Rgba32 letterboxColor)
    {
        var leftMargin = GetPlayerHorizontalMarginWidth(previewFrame, letterboxColor, fromLeft: true);
        var rightMargin = GetPlayerHorizontalMarginWidth(previewFrame, letterboxColor, fromLeft: false);
        if (leftMargin <= 0 && rightMargin <= 0)
        {
            return previewFrame.Clone();
        }

        var left = Math.Min(previewFrame.Width - 1, leftMargin);
        var right = Math.Max(left + 1, previewFrame.Width - rightMargin);
        if (right - left < Math.Max(24, previewFrame.Width / 4))
        {
            return previewFrame.Clone();
        }

        return previewFrame.Clone(ctx => ctx.Crop(new Rectangle(left, 0, right - left, previewFrame.Height)));
    }

    private static int GetPlayerHorizontalMarginWidth(Image<Rgba32> image, Rgba32 letterboxColor, bool fromLeft)
    {
        var maximumScan = Math.Max(0, Math.Min(image.Width / 3, 240));
        if (maximumScan <= 0)
        {
            return 0;
        }

        var yStep = Math.Max(1, image.Height / 80);
        var result = maximumScan;
        image.ProcessPixelRows(accessor =>
        {
            for (var offset = 0; offset < maximumScan; offset++)
            {
                var x = fromLeft ? offset : image.Width - 1 - offset;
                var total = 0;
                var matched = 0;
                for (var y = 0; y < image.Height; y += yStep)
                {
                    if (IsPlayerMarginPixel(accessor.GetRowSpan(y)[x], letterboxColor))
                    {
                        matched++;
                    }

                    total++;
                }

                if (total > 0 && matched / (double)total < 0.86)
                {
                    result = offset;
                    return;
                }
            }
        });

        return result;
    }

    private static bool IsPlayerMarginPixel(Rgba32 pixel, Rgba32 letterboxColor)
    {
        if (pixel.A <= 12)
        {
            return true;
        }

        if (Math.Abs(pixel.R - letterboxColor.R) +
            Math.Abs(pixel.G - letterboxColor.G) +
            Math.Abs(pixel.B - letterboxColor.B) <= 42)
        {
            return true;
        }

        var minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
        var maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
        var average = (pixel.R + pixel.G + pixel.B) / 3d;
        return maximum - minimum <= 14 && average is >= 12 and <= 72;
    }

    private async Task<string> ResolveSubtitleTextAsync(
        string projectDir,
        string outputPath,
        IReadOnlyDictionary<string, string> configMap,
        Image<Rgba32> subtitleProbe,
        string sourceVideo,
        int outputIndex,
        ProjectImageTemplatePage page,
        IReadOnlyList<string> episodeNames,
        ProjectInfo projectInfo,
        int trackEpisodeIndex,
        CancellationToken cancellationToken)
    {
        var fallback = ResolveFallbackSubtitleText(projectInfo, episodeNames, trackEpisodeIndex);
        var mode = ResolveSubtitleAiMode(configMap);
        var cacheKey = BuildSubtitleCacheKey(outputPath, sourceVideo, outputIndex, page, subtitleProbe);

        var subtitleCache = LoadSubtitleCache(projectDir, SubtitleCacheFileName);
        if (TryGetCachedSubtitle(subtitleCache, cacheKey, out var cachedSubtitle))
        {
            return string.IsNullOrWhiteSpace(cachedSubtitle) ? fallback : cachedSubtitle;
        }

        var ocrText = await TryOcrSubtitleAsync(subtitleProbe, cancellationToken);
        if (!LooksLikePlaceholderSubtitle(ocrText))
        {
            SetCachedSubtitle(projectDir, SubtitleCacheFileName, subtitleCache, cacheKey, ocrText);
            return ocrText;
        }

        if (!string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            var aiCache = LoadSubtitleCache(projectDir, SubtitleAiCacheFileName);
            var aiCacheKey = BuildSubtitleAiCacheKey(subtitleProbe, configMap, mode);
            if (TryGetCachedSubtitle(aiCache, aiCacheKey, out var cachedAiSubtitle) && !string.IsNullOrWhiteSpace(cachedAiSubtitle))
            {
                SetCachedSubtitle(projectDir, SubtitleCacheFileName, subtitleCache, cacheKey, cachedAiSubtitle);
                return cachedAiSubtitle;
            }

            var aiSubtitle = await TryDetectSubtitleWithAiAsync(subtitleProbe, configMap, mode, cancellationToken);
            if (!LooksLikePlaceholderSubtitle(aiSubtitle))
            {
                SetCachedSubtitle(projectDir, SubtitleAiCacheFileName, aiCache, aiCacheKey, aiSubtitle);
                SetCachedSubtitle(projectDir, SubtitleCacheFileName, subtitleCache, cacheKey, aiSubtitle);
                return aiSubtitle;
            }
        }

        SetCachedSubtitle(projectDir, SubtitleCacheFileName, subtitleCache, cacheKey, fallback);
        return fallback;
    }

    private static string ResolveFallbackSubtitleText(ProjectInfo projectInfo, IReadOnlyList<string> episodeNames, int trackEpisodeIndex)
    {
        var fallback = trackEpisodeIndex >= 0 && trackEpisodeIndex < episodeNames.Count
            ? episodeNames[trackEpisodeIndex]
            : projectInfo.Title;

        if (!string.IsNullOrWhiteSpace(projectInfo.Tagline))
        {
            return projectInfo.Tagline!;
        }

        return !string.IsNullOrWhiteSpace(projectInfo.Synopsis)
            ? projectInfo.Synopsis!
            : fallback;
    }

    private static string ResolveSubtitleAiMode(IReadOnlyDictionary<string, string> configMap)
    {
        if (configMap.TryGetValue("ProjectImageSubtitleAiMode", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim().ToLowerInvariant();
        }

        return "fast";
    }

    private static bool LooksLikePlaceholderSubtitle(string? text)
    {
        var normalized = Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
        if (normalized.Length == 0)
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"第\d{1,3}集$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return normalized.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("subtitletest", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("title", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("episode", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSubtitleCacheKey(string outputPath, string sourceVideo, int outputIndex, ProjectImageTemplatePage page, Image<Rgba32> image)
    {
        using var probe = image.Clone(ctx =>
        {
            if (image.Width > 640)
            {
                var scaledHeight = Math.Max(1, (int)Math.Round(image.Height * (640d / image.Width)));
                ctx.Resize(640, scaledHeight);
            }
        });

        using var ms = new MemoryStream();
        probe.Save(ms, new PngEncoder());
        using var sha = System.Security.Cryptography.SHA1.Create();
        var digest = Convert.ToHexString(sha.ComputeHash(ms.ToArray()));
        return $"{Path.GetFileNameWithoutExtension(outputPath)}|{Path.GetFileName(sourceVideo)}|{outputIndex}|{page.File}|{digest}";
    }

    private static string BuildSubtitleAiCacheKey(Image<Rgba32> image, IReadOnlyDictionary<string, string> configMap, string mode)
    {
        var endpoint = configMap.TryGetValue("AiTextEndpoint", out var aiEndpoint) && !string.IsNullOrWhiteSpace(aiEndpoint)
            ? aiEndpoint
            : configMap.GetValueOrDefault("ChatModelEndpoint", string.Empty);
        var model = configMap.TryGetValue("AiTextModel", out var aiModel) && !string.IsNullOrWhiteSpace(aiModel)
            ? aiModel
            : configMap.GetValueOrDefault("ChatModelId", string.Empty);
        return BuildSubtitleCacheKey($"ai:{endpoint}:{model}:{mode}", model, 0, new ProjectImageTemplatePage("ai", new Dictionary<string, IReadOnlyList<ProjectImageTemplateRegion>>()), image);
    }

    private static Dictionary<string, string> LoadSubtitleCache(string projectDir, string fileName)
    {
        var path = Path.Combine(projectDir, fileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
            return payload is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(payload, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool TryGetCachedSubtitle(Dictionary<string, string> cache, string key, out string value)
    {
        if (cache.TryGetValue(key, out value!))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void SetCachedSubtitle(string projectDir, string fileName, Dictionary<string, string> cache, string key, string value)
    {
        cache[key] = value ?? string.Empty;
        var path = Path.Combine(projectDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions), Encoding.UTF8);
    }

    private async Task<string> TryOcrSubtitleAsync(Image<Rgba32> image, CancellationToken cancellationToken)
    {
        var tesseract = ResolveBinaryOptional("tesseract");
        if (string.IsNullOrWhiteSpace(tesseract))
        {
            return string.Empty;
        }

        foreach (var band in BuildSubtitleProbeBands(image))
        {
            using (band)
            {
                foreach (var variant in BuildOcrVariants(band))
                {
                    using (variant)
                    {
                        var text = await RunTesseractAsync(tesseract, variant, cancellationToken);
                        var selected = SelectBestOcrLine(text);
                        if (!LooksLikePlaceholderSubtitle(selected))
                        {
                            return selected;
                        }
                    }
                }
            }
        }

        return string.Empty;
    }

    private IEnumerable<Image<Rgba32>> BuildSubtitleProbeBands(Image<Rgba32> image)
    {
        var bands = image.Width >= image.Height
            ? new (double Top, double Bottom)[]
            {
                (0.72, 0.96),
                (0.66, 0.98),
                (0.58, 0.98),
                (0.45, 0.86)
            }
            : new (double Top, double Bottom)[]
            {
                (0.42, 0.82),
                (0.58, 0.96)
            };

        foreach (var band in bands)
        {
            var top = Math.Clamp((int)(image.Height * band.Top), 0, Math.Max(0, image.Height - 1));
            var bottom = Math.Clamp((int)(image.Height * band.Bottom), top + 1, image.Height);
            var crop = image.Clone(ctx => ctx.Crop(new Rectangle(0, top, image.Width, bottom - top)));
            var scale = Math.Min(crop.Width, crop.Height) < 420 ? 3 : 2;
            crop.Mutate(ctx => ctx.Resize(crop.Width * scale, crop.Height * scale));
            yield return crop;
        }
    }

    private IEnumerable<Image<Rgba32>> BuildOcrVariants(Image<Rgba32> image)
    {
        var grayscale = image.Clone(ctx => ctx.Grayscale());
        yield return grayscale.Clone();
        yield return grayscale.Clone(ctx => ctx.BinaryThreshold(0.56f));
        yield return grayscale.Clone(ctx => ctx.Invert().BinaryThreshold(0.56f));
        grayscale.Dispose();
    }

    private async Task<string> RunTesseractAsync(string tesseract, Image<Rgba32> image, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"shortdrama-subtitle-{Guid.NewGuid():N}.png");
        try
        {
            await image.SaveAsync(tempPath, new PngEncoder(), cancellationToken);
            foreach (var lang in new[] { "chi_sim+eng", "chi_sim", "eng" })
            {
                foreach (var psm in new[] { "6", "7", "11" })
                {
                    var result = await _processRunner.RunAsync(
                        tesseract,
                        [tempPath, "stdout", "-l", lang, "--psm", psm],
                        Path.GetDirectoryName(tempPath),
                        cancellationToken);
                    if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                    {
                        return result.StandardOutput;
                    }
                }
            }

            return string.Empty;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string SelectBestOcrLine(string rawText)
    {
        var candidates = new List<(int Score, string Text)>();
        foreach (var line in rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = Regex.Replace(line.Trim(), @"\s+", string.Empty);
            cleaned = Regex.Replace(cleaned, @"[|_~`^=+<>\\/\[\]{}]", string.Empty);
            cleaned = Regex.Replace(cleaned, @"^[^\u4e00-\u9fffA-Za-z0-9]+|[^\u4e00-\u9fffA-Za-z0-9，。！？?!]+$", string.Empty);
            if (cleaned.Length < 2 || LooksLikePlaceholderSubtitle(cleaned))
            {
                continue;
            }

            var chinese = Regex.Matches(cleaned, @"[\u4e00-\u9fff]").Count;
            var alpha = Regex.Matches(cleaned, @"[A-Za-z]").Count;
            var digits = Regex.Matches(cleaned, @"\d").Count;
            if (digits > Math.Max(3, cleaned.Length / 2))
            {
                continue;
            }

            candidates.Add((chinese * 4 + alpha + Math.Min(cleaned.Length, 18), cleaned));
        }

        return candidates.Count == 0
            ? string.Empty
            : candidates.OrderByDescending(item => item.Score).First().Text[..Math.Min(40, candidates.OrderByDescending(item => item.Score).First().Text.Length)];
    }

    private async Task<string> TryDetectSubtitleWithAiAsync(
        Image<Rgba32> image,
        IReadOnlyDictionary<string, string> configMap,
        string mode,
        CancellationToken cancellationToken)
    {
        var endpoint = configMap.TryGetValue("AiTextEndpoint", out var aiEndpoint) && !string.IsNullOrWhiteSpace(aiEndpoint)
            ? aiEndpoint.Trim().TrimEnd('/')
            : configMap.GetValueOrDefault("ChatModelEndpoint", string.Empty).Trim().TrimEnd('/');
        var modelId = configMap.TryGetValue("AiTextModel", out var aiModel) && !string.IsNullOrWhiteSpace(aiModel)
            ? aiModel.Trim()
            : configMap.GetValueOrDefault("ChatModelId", string.Empty).Trim();
        var apiKey = configMap.TryGetValue("AiTextApiKey", out var aiKey) && !string.IsNullOrWhiteSpace(aiKey)
            ? aiKey.Trim()
            : configMap.GetValueOrDefault("ChatModelApiKey", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        using var prepared = image.Clone(ctx =>
        {
            if (image.Width > 640)
            {
                var height = Math.Max(1, (int)Math.Round(image.Height * (640d / image.Width)));
                ctx.Resize(640, height);
            }
        });
        using var ms = new MemoryStream();
        await prepared.SaveAsync(ms, new PngEncoder(), cancellationToken);
        var imageBase64 = Convert.ToBase64String(ms.ToArray());

        var prompt = """
请直接识别这张图片中播放器画面里的对白字幕。
优先读取画面中人物附近或画面下方的白色中文字幕。
不要输出剧名、集数、时间轴文字、按钮文字或右侧面板文字。
如果字幕分两行，请合并成一句中文原文。
如果没有可读对白字幕，返回空字符串。
只返回 JSON：{"subtitle":"..."}
""";

        var payload = new
        {
            model = modelId,
            temperature = string.Equals(mode, "accurate", StringComparison.OrdinalIgnoreCase) ? 0.0 : 0.2,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = prompt
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:image/png;base64,{imageBase64}"
                            }
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions);
        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        try
        {
            var jsonText = ExtractJsonObject(content);
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                var result = JsonSerializer.Deserialize<SubtitleAiResponse>(jsonText, JsonOptions);
                return CleanSubtitleText(result?.Subtitle);
            }
        }
        catch
        {
            // fall back to plain text below
        }

        return CleanSubtitleText(content);
    }

    private static string CleanSubtitleText(string? text)
    {
        var cleaned = (text ?? string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, "^```(?:json)?|```$", string.Empty, RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.Trim('\"', '\'', '“', '”', '‘', '’');
        cleaned = Regex.Replace(cleaned, @"\s+", string.Empty);
        cleaned = Regex.Replace(cleaned, @"^(字幕|台词|对白)[:：]?", string.Empty);
        return LooksLikePlaceholderSubtitle(cleaned)
            ? string.Empty
            : cleaned[..Math.Min(40, cleaned.Length)];
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : string.Empty;
    }

    private static string? ResolveBinaryOptional(string name)
    {
        try
        {
            return ResolveBinary(name);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveTrackEpisodeText(int trackEpisodeIndex, IReadOnlyList<string> episodeNames, string projectTitle)
    {
        if (trackEpisodeIndex >= 0 && trackEpisodeIndex < episodeNames.Count)
        {
            return episodeNames[trackEpisodeIndex];
        }

        return $"{projectTitle} 第{trackEpisodeIndex + 1:00}集";
    }

    private static string ResolveTrackEpisodeTextWithDuration(int trackEpisodeIndex, IReadOnlyList<string> episodeNames, IReadOnlyList<double> episodeDurations, string projectTitle)
    {
        var text = ResolveTrackEpisodeText(trackEpisodeIndex, episodeNames, projectTitle);
        return trackEpisodeIndex >= 0 && trackEpisodeIndex < episodeDurations.Count
            ? $"{text}   {FormatTrackTimecode(episodeDurations[trackEpisodeIndex])}"
            : text;
    }

    private static int ResolveTrackEpisodeIndex(
        int pageIndex,
        int currentIndex,
        int episodeNameCount,
        int episodeFrameCount,
        IReadOnlyList<ProjectImageTemplateRegion> trackRects)
    {
        var configuredEpisode = trackRects.Count > 0
            ? NoteInt(trackRects[0], "track_episode_index", 0, 0, 9999)
            : 0;
        if (configuredEpisode > 0)
        {
            return configuredEpisode - 1;
        }

        var episodeCount = Math.Max(Math.Max(episodeNameCount, episodeFrameCount), Math.Max(currentIndex, 1));
        return pageIndex switch
        {
            0 => 0,
            1 => Math.Min(1, Math.Max(0, episodeCount - 1)),
            2 => Math.Max(0, episodeCount - 2),
            _ => Math.Max(0, episodeCount - 1)
        };
    }

    private static ProjectImageTemplateRegion ExtendTrackTextRect(ProjectImageTemplateRegion rect, IReadOnlyList<ProjectImageTemplateRegion> trackRects, int canvasWidth)
    {
        if (trackRects.Count == 0)
        {
            return rect;
        }

        var rightEdge = rect.X + rect.Width;
        foreach (var trackRect in trackRects)
        {
            var trackRight = trackRect.X + trackRect.Width;
            if (trackRect.X <= rect.X && rect.X < trackRight)
            {
                rightEdge = Math.Max(rightEdge, trackRight - 2);
                break;
            }
        }

        rightEdge = Math.Min(canvasWidth, Math.Max(rect.X + 1, rightEdge));
        return rect with { Width = rightEdge - rect.X };
    }

    private static bool ShouldHideTrackText(IReadOnlyList<ProjectImageTemplateRegion> trackRects)
    {
        if (trackRects.Count == 0 || NoteInt(trackRects[0], "single_episode_track", 0, 0, 1) <= 0)
        {
            return false;
        }

        return NoteInt(trackRects[0], "hide_track_text", 1, 0, 1) > 0;
    }

    private static void EraseVideoTrackTextRect(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        IReadOnlyList<ProjectImageTemplateRegion> videoTrackRects)
    {
        FillRect(canvas, rect, SampleVideoTrackTextBackground(canvas, rect, videoTrackRects));
    }

    private static void EraseAudioTrackTextRect(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var eraseLeft = NoteInt(rect, "erase_left", 0, 0, 80);
        var eraseRight = NoteInt(rect, "erase_right", 0, 0, 120);
        var eraseRect = ExpandRect(canvas, rect, top: 2, right: eraseRight, bottom: 0, left: eraseLeft);
        FillRect(canvas, eraseRect, SampleAudioTrackBackground(canvas, rect));
    }

    private static Rgba32 SampleVideoTrackTextBackground(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        IReadOnlyList<ProjectImageTemplateRegion> videoTrackRects)
    {
        var trackRect = FindOverlappingTrackRect(rect, videoTrackRects);
        if (trackRect is null)
        {
            return SampleSurroundingColor(canvas, rect);
        }

        var thumbHeight = GetTrackThumbnailHeight(trackRect);
        var thumbY = Math.Max(0, (trackRect.Height - thumbHeight) / 2);
        return SampleVideoTrackBackground(canvas, trackRect, thumbY, thumbHeight);
    }

    private static Rgba32 SampleVideoTrackBackground(Image<Rgba32> canvas, ProjectImageTemplateRegion rect, int thumbY, int thumbHeight)
    {
        var top = Math.Clamp(rect.Y, 0, canvas.Height);
        var bottom = Math.Clamp(rect.Y + rect.Height, top, canvas.Height);
        var left = Math.Clamp(rect.X, 0, canvas.Width);
        var right = Math.Clamp(rect.X + rect.Width, left, canvas.Width);
        var thumbTop = Math.Clamp(rect.Y + thumbY, top, bottom);
        var thumbBottom = Math.Clamp(rect.Y + thumbY + thumbHeight, thumbTop, bottom);
        var samples = new List<Rgba32>();

        AddSampleBox(canvas, left, top, right - left, thumbTop - top, samples);
        AddSampleBox(canvas, left, thumbBottom, right - left, bottom - thumbBottom, samples);

        return samples.Count > 0 ? MedianColor(samples) : SampleRectColor(canvas, rect);
    }

    private static Rgba32 SampleAudioTrackBackground(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var sampleRect = InsetRect(canvas, rect, top: Math.Max(1, rect.Height / 4), bottom: 1);
        var samples = CollectPixels(canvas, sampleRect, static pixel =>
            pixel.A > 0 && pixel.R + pixel.G + pixel.B < 560);
        return samples.Count > 0 ? MedianColor(samples) : SampleSurroundingColor(canvas, rect);
    }

    private static ProjectImageTemplateRegion? FindOverlappingTrackRect(
        ProjectImageTemplateRegion rect,
        IReadOnlyList<ProjectImageTemplateRegion> trackRects)
    {
        ProjectImageTemplateRegion? bestRect = null;
        var bestArea = 0;
        var rectLeft = rect.X;
        var rectTop = rect.Y;
        var rectRight = rect.X + rect.Width;
        var rectBottom = rect.Y + rect.Height;

        foreach (var trackRect in trackRects)
        {
            var left = Math.Max(rectLeft, trackRect.X);
            var top = Math.Max(rectTop, trackRect.Y);
            var right = Math.Min(rectRight, trackRect.X + trackRect.Width);
            var bottom = Math.Min(rectBottom, trackRect.Y + trackRect.Height);
            var area = Math.Max(0, right - left) * Math.Max(0, bottom - top);
            if (area <= bestArea)
            {
                continue;
            }

            bestArea = area;
            bestRect = trackRect;
        }

        return bestRect;
    }

    private static ProjectImageTemplateRegion ExpandRect(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        int top = 0,
        int right = 0,
        int bottom = 0,
        int left = 0)
    {
        var x = Math.Max(0, rect.X - Math.Max(0, left));
        var y = Math.Max(0, rect.Y - Math.Max(0, top));
        var rightEdge = Math.Min(canvas.Width, rect.X + rect.Width + Math.Max(0, right));
        var bottomEdge = Math.Min(canvas.Height, rect.Y + rect.Height + Math.Max(0, bottom));
        return rect with
        {
            X = x,
            Y = y,
            Width = Math.Max(1, rightEdge - x),
            Height = Math.Max(1, bottomEdge - y)
        };
    }

    private static ProjectImageTemplateRegion InsetRect(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        int top = 0,
        int right = 0,
        int bottom = 0,
        int left = 0)
    {
        var x = Math.Max(0, rect.X + Math.Max(0, left));
        var y = Math.Max(0, rect.Y + Math.Max(0, top));
        var rightEdge = Math.Min(canvas.Width, rect.X + rect.Width - Math.Max(0, right));
        var bottomEdge = Math.Min(canvas.Height, rect.Y + rect.Height - Math.Max(0, bottom));
        if (rightEdge <= x)
        {
            rightEdge = Math.Min(canvas.Width, x + 1);
        }

        if (bottomEdge <= y)
        {
            bottomEdge = Math.Min(canvas.Height, y + 1);
        }

        return rect with
        {
            X = x,
            Y = y,
            Width = Math.Max(1, rightEdge - x),
            Height = Math.Max(1, bottomEdge - y)
        };
    }

    private static ProjectImageTemplateRegion ShiftRect(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        int dx = 0,
        int dy = 0)
    {
        var x = Math.Clamp(rect.X + dx, 0, Math.Max(0, canvas.Width - 1));
        var y = Math.Clamp(rect.Y + dy, 0, Math.Max(0, canvas.Height - 1));
        return rect with
        {
            X = x,
            Y = y,
            Width = Math.Max(1, Math.Min(rect.Width, canvas.Width - x)),
            Height = Math.Max(1, Math.Min(rect.Height, canvas.Height - y))
        };
    }

    private static void RestoreVideoTrackSegmentBoundaries(
        Image<Rgba32> canvas,
        IReadOnlyList<ProjectImageTemplateRegion> videoTrackRects,
        IReadOnlyList<ProjectImageTemplateRegion> textRects,
        IReadOnlyList<ProjectImageTemplateRegion> audioTextRects)
    {
        foreach (var trackRect in videoTrackRects)
        {
            if (NoteInt(trackRect, "segment_boundary", 0, 0, 1) <= 0)
            {
                continue;
            }

            var width = NoteInt(trackRect, "segment_boundary_width", 4, 1, 20);
            var alpha = NoteInt(trackRect, "segment_boundary_alpha", 190, 40, 255);
            var edgeAlpha = NoteInt(trackRect, "segment_boundary_edge_alpha", 80, 0, 255);
            var xOffset = NoteInt(trackRect, "segment_boundary_x_offset", 0, -20, 20);
            var absoluteX = NoteInt(trackRect, "segment_boundary_x", -1, -1, Math.Max(0, canvas.Width - 1));
            var topOffset = NoteInt(trackRect, "segment_boundary_top_offset", 0, 0, Math.Max(0, canvas.Height));
            var bottomOffset = NoteInt(trackRect, "segment_boundary_bottom_offset", 0, 0, Math.Max(0, canvas.Height));
            var top = Math.Clamp(trackRect.Y - topOffset, 0, canvas.Height);
            var bottom = Math.Max(top, Math.Min(Math.Max(0, canvas.Height - 1), trackRect.Y + trackRect.Height - 1 + bottomOffset));
            var leftLimit = Math.Max(0, trackRect.X);
            var rightLimit = Math.Min(Math.Max(0, canvas.Width - 1), trackRect.X + trackRect.Width - 1);
            if (bottom <= top || rightLimit <= leftLimit)
            {
                continue;
            }

            DrawSegmentBoundaries(
                canvas,
                textRects,
                leftLimit,
                rightLimit,
                top,
                bottom,
                width,
                alpha,
                edgeAlpha,
                xOffset,
                absoluteX >= 0 ? absoluteX : null);

            if (audioTextRects.Count == 0)
            {
                continue;
            }

            var audioHeight = NoteInt(trackRect, "audio_segment_boundary_height", trackRect.Height, 1, Math.Max(1, canvas.Height));
            var audioTopOffset = NoteInt(trackRect, "audio_segment_boundary_top_offset", 0, 0, 80);
            var audioBottomOffset = NoteInt(trackRect, "audio_segment_boundary_bottom_offset", 0, 0, 80);
            foreach (var audioRect in audioTextRects)
            {
                var audioTop = Math.Clamp(audioRect.Y - audioTopOffset, 0, Math.Max(0, canvas.Height - 1));
                var audioBottom = Math.Max(audioTop, Math.Min(Math.Max(0, canvas.Height - 1), audioTop + audioHeight - 1 + audioBottomOffset));
                DrawSegmentBoundaries(
                    canvas,
                    [audioRect],
                    leftLimit,
                    rightLimit,
                    audioTop,
                    audioBottom,
                    width,
                    alpha,
                    edgeAlpha,
                    xOffset,
                    absoluteX >= 0 ? absoluteX : null);
            }
        }
    }

    private static void DrawSegmentBoundaries(
        Image<Rgba32> canvas,
        IReadOnlyList<ProjectImageTemplateRegion> textRects,
        int leftLimit,
        int rightLimit,
        int top,
        int bottom,
        int width,
        int alpha,
        int edgeAlpha,
        int xOffset,
        int? absoluteX)
    {
        foreach (var textRect in textRects)
        {
            var sourceX = absoluteX ?? textRect.X + xOffset;
            var x = Math.Clamp(sourceX, leftLimit, rightLimit);
            var left = Math.Max(leftLimit, x - width);
            var right = Math.Min(rightLimit, x - 1);
            if (right < left)
            {
                continue;
            }

            var fill = new Rgba32(12, 48, 52, (byte)Math.Clamp(alpha, 0, 255));
            var edge = new Rgba32(82, 112, 116, (byte)Math.Clamp(edgeAlpha, 0, 255));
            canvas.Mutate(ctx =>
            {
                ctx.Fill(fill, new RectangleF(left, top, right - left + 1, bottom - top + 1));
                if (edgeAlpha > 0)
                {
                    ctx.Fill(edge, new RectangleF(Math.Min(rightLimit, x), top, 1, bottom - top + 1));
                }
            });
        }
    }

    private static void RestoreVideoTrackPlayheadOverlay(Image<Rgba32> canvas, IReadOnlyList<ProjectImageTemplateRegion> videoTrackRects)
    {
        foreach (var rect in videoTrackRects)
        {
            if (rect.Width < rect.Height * 3)
            {
                continue;
            }

            using var overlay = CapturePlayheadOverlay(canvas, rect);
            if (overlay is null)
            {
                continue;
            }

            var left = Math.Clamp(rect.X, 0, canvas.Width);
            var top = Math.Clamp(rect.Y, 0, canvas.Height);
            canvas.Mutate(ctx => ctx.DrawImage(overlay, new Point(left, top), 1f));
        }
    }

    private static Image<Rgba32>? CapturePlayheadOverlay(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var left = Math.Clamp(rect.X, 0, canvas.Width);
        var top = Math.Clamp(rect.Y, 0, canvas.Height);
        var right = Math.Clamp(rect.X + rect.Width, left, canvas.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top, canvas.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var playheadX = ResolveConfiguredPlayheadX(rect);
        if (playheadX is not null && playheadX.Value >= left && playheadX.Value < right)
        {
            return BuildPlayheadOverlay(canvas, rect, playheadX.Value);
        }

        var detectedX = DetectPlayheadXFromSurrounding(canvas, rect);
        return detectedX is not null && detectedX.Value >= left && detectedX.Value < right
            ? BuildPlayheadOverlay(canvas, rect, detectedX.Value)
            : null;
    }

    private static Image<Rgba32>? BuildPlayheadOverlay(Image<Rgba32> canvas, ProjectImageTemplateRegion rect, int playheadX)
    {
        var left = Math.Clamp(rect.X, 0, canvas.Width);
        var top = Math.Clamp(rect.Y, 0, canvas.Height);
        var right = Math.Clamp(rect.X + rect.Width, left, canvas.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top, canvas.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var overlay = new Image<Rgba32>(right - left, bottom - top, new Rgba32(0, 0, 0, 0));
        var localX = playheadX - left;
        var lineLeft = Math.Max(0, localX - 1);
        var lineRight = Math.Min(overlay.Width - 1, localX);
        if (lineRight < lineLeft)
        {
            overlay.Dispose();
            return null;
        }

        var color = SamplePlayheadColor(canvas, playheadX, rect);
        overlay.Mutate(ctx => ctx.Fill(color, new RectangleF(lineLeft, 0, lineRight - lineLeft + 1, overlay.Height)));
        return overlay;
    }

    private static int? ResolveConfiguredPlayheadX(ProjectImageTemplateRegion rect)
    {
        var playheadX = NoteInt(rect, "playhead_x", int.MinValue, -10000, 10000);
        if (playheadX == int.MinValue)
        {
            return null;
        }

        return playheadX + NoteInt(rect, "playhead_x_offset", 0, -20, 20);
    }

    private static int? DetectPlayheadXFromSurrounding(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var left = Math.Clamp(rect.X, 0, canvas.Width);
        var right = Math.Clamp(rect.X + rect.Width, left, canvas.Width);
        var topBandTop = Math.Max(0, rect.Y - 260);
        var topBandBottom = Math.Max(topBandTop, Math.Min(canvas.Height, rect.Y));
        var bottomBandTop = Math.Max(0, Math.Min(canvas.Height, rect.Y + rect.Height));
        var bottomBandBottom = Math.Max(bottomBandTop, Math.Min(canvas.Height, rect.Y + rect.Height + 260));
        if (right <= left || (topBandBottom <= topBandTop && bottomBandBottom <= bottomBandTop))
        {
            return null;
        }

        var scores = new List<(int Score, int X)>();
        canvas.ProcessPixelRows(accessor =>
        {
            for (var x = left; x < right; x++)
            {
                var topCount = 0;
                for (var y = topBandTop; y < topBandBottom; y += 2)
                {
                    if (IsPlayheadPixel(accessor.GetRowSpan(y)[x]))
                    {
                        topCount++;
                    }
                }

                var bottomCount = 0;
                for (var y = bottomBandTop; y < bottomBandBottom; y += 2)
                {
                    if (IsPlayheadPixel(accessor.GetRowSpan(y)[x]))
                    {
                        bottomCount++;
                    }
                }

                if (topCount >= 18 && bottomCount >= 18)
                {
                    scores.Add((topCount + bottomCount, x));
                }
            }
        });

        if (scores.Count == 0)
        {
            return null;
        }

        scores.Sort(static (first, second) => second.Score != first.Score
            ? second.Score.CompareTo(first.Score)
            : first.X.CompareTo(second.X));

        var bestScore = scores[0].Score;
        var candidateXs = scores
            .Where(score => score.Score >= Math.Max(1, bestScore - 8))
            .Select(score => score.X)
            .Distinct()
            .OrderBy(static x => x)
            .ToList();
        if (candidateXs.Count == 0)
        {
            return scores[0].X;
        }

        var groups = new List<List<int>>();
        foreach (var x in candidateXs)
        {
            if (groups.Count == 0 || x - groups[^1][^1] > 2)
            {
                groups.Add([x]);
            }
            else
            {
                groups[^1].Add(x);
            }
        }

        var selected = groups
            .OrderByDescending(static group => group.Count)
            .ThenByDescending(group => scores.Where(score => group.Contains(score.X)).Sum(score => score.Score))
            .First();
        return selected[selected.Count / 2];
    }

    private static Rgba32 SamplePlayheadColor(Image<Rgba32> canvas, int x, ProjectImageTemplateRegion rect)
    {
        var clampedX = Math.Clamp(x, 0, Math.Max(0, canvas.Width - 1));
        var ranges = new[]
        {
            (Start: Math.Max(0, rect.Y - 260), End: Math.Max(0, Math.Min(canvas.Height, rect.Y))),
            (Start: Math.Max(0, Math.Min(canvas.Height, rect.Y + rect.Height)), End: Math.Max(0, Math.Min(canvas.Height, rect.Y + rect.Height + 260)))
        };
        var samples = new List<Rgba32>();

        canvas.ProcessPixelRows(accessor =>
        {
            foreach (var range in ranges)
            {
                for (var y = range.Start; y < range.End; y++)
                {
                    var pixel = accessor.GetRowSpan(y)[clampedX];
                    if (IsPlayheadPixel(pixel))
                    {
                        samples.Add(pixel);
                    }
                }
            }
        });

        int red;
        int green;
        int blue;
        int alpha;
        if (samples.Count == 0)
        {
            red = 170;
            green = 170;
            blue = 170;
            alpha = 165;
        }
        else
        {
            red = MedianByte(samples.Select(static pixel => pixel.R));
            green = MedianByte(samples.Select(static pixel => pixel.G));
            blue = MedianByte(samples.Select(static pixel => pixel.B));
            alpha = MedianByte(samples.Select(static pixel => pixel.A));
        }

        var brightnessLimit = NoteInt(rect, "playhead_brightness", 178, 80, 255);
        var alphaLimit = NoteInt(rect, "playhead_alpha", 170, 40, 255);
        var peak = Math.Max(red, Math.Max(green, blue));
        if (peak > brightnessLimit)
        {
            var scale = brightnessLimit / (double)peak;
            red = Math.Clamp((int)Math.Round(red * scale), 0, 255);
            green = Math.Clamp((int)Math.Round(green * scale), 0, 255);
            blue = Math.Clamp((int)Math.Round(blue * scale), 0, 255);
        }

        var minAlpha = Math.Min(120, alphaLimit);
        alpha = Math.Max(minAlpha, Math.Min(alphaLimit, alpha));
        return new Rgba32((byte)red, (byte)green, (byte)blue, (byte)alpha);
    }

    private static bool IsPlayheadPixel(Rgba32 pixel)
    {
        if (pixel.A <= 0)
        {
            return false;
        }

        var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
        var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
        return pixel.R >= 150 && pixel.G >= 150 && pixel.B >= 150 && max - min <= 55;
    }

    private static List<Rgba32> CollectPixels(Image<Rgba32> canvas, ProjectImageTemplateRegion rect, Func<Rgba32, bool> predicate)
    {
        var left = Math.Clamp(rect.X, 0, Math.Max(0, canvas.Width - 1));
        var top = Math.Clamp(rect.Y, 0, Math.Max(0, canvas.Height - 1));
        var right = Math.Clamp(rect.X + rect.Width, left + 1, canvas.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top + 1, canvas.Height);
        var samples = new List<Rgba32>((right - left) * (bottom - top));

        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = top; y < bottom; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = left; x < right; x++)
                {
                    if (predicate(row[x]))
                    {
                        samples.Add(row[x]);
                    }
                }
            }
        });

        return samples;
    }

    private static byte MedianByte(IEnumerable<byte> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        return ordered.Length == 0 ? (byte)0 : ordered[ordered.Length / 2];
    }

    private static int GetTrackThumbnailHeight(ProjectImageTemplateRegion rect)
    {
        return NoteInt(rect, "thumbnail_height", Math.Max(1, rect.Height), 1, Math.Max(1, rect.Height));
    }

    private static string? NoteValue(ProjectImageTemplateRegion rect, string key)
    {
        var match = Regex.Match(rect.Note ?? string.Empty, $@"(?:^|[;,\s]){Regex.Escape(key)}\s*=\s*([^;,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int NoteInt(ProjectImageTemplateRegion rect, string key, int defaultValue, int minimum, int maximum)
    {
        var match = Regex.Match(rect.Note ?? string.Empty, $@"(?:^|[;,\s]){Regex.Escape(key)}\s*=\s*(-?\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
        {
            return defaultValue;
        }

        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private static Rgba32? ParseHexColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim().TrimStart('#');
        if (text.Length is not (6 or 8))
        {
            return null;
        }

        try
        {
            var red = Convert.ToByte(text[..2], 16);
            var green = Convert.ToByte(text.Substring(2, 2), 16);
            var blue = Convert.ToByte(text.Substring(4, 2), 16);
            var alpha = text.Length == 8 ? Convert.ToByte(text.Substring(6, 2), 16) : (byte)255;
            return new Rgba32(red, green, blue, alpha);
        }
        catch
        {
            return null;
        }
    }

    private static void FillRect(Image<Rgba32> canvas, ProjectImageTemplateRegion rect, Rgba32 color)
    {
        canvas.Mutate(ctx => ctx.Fill(color, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height)));
    }

    private static Rgba32 SampleRectColor(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var left = Math.Clamp(rect.X, 0, Math.Max(0, canvas.Width - 1));
        var top = Math.Clamp(rect.Y, 0, Math.Max(0, canvas.Height - 1));
        var right = Math.Clamp(rect.X + rect.Width, left + 1, canvas.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top + 1, canvas.Height);
        using var crop = canvas.Clone(ctx => ctx.Crop(new Rectangle(left, top, right - left, bottom - top)));
        return MedianColor(crop);
    }

    private static Rgba32 SampleSurroundingColor(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var samples = new List<Rgba32>();
        var band = Math.Max(2, Math.Min(6, rect.Height / 3));
        var hasSideBoxes = AddSampleBox(canvas, rect.X - band, rect.Y, band, rect.Height, samples);
        hasSideBoxes |= AddSampleBox(canvas, rect.X + rect.Width, rect.Y, band, rect.Height, samples);
        if (!hasSideBoxes)
        {
            AddSampleBox(canvas, rect.X, rect.Y - band, rect.Width, band, samples);
            AddSampleBox(canvas, rect.X, rect.Y + rect.Height, rect.Width, band, samples);
        }

        return samples.Count == 0 ? SampleRectColor(canvas, rect) : MedianColor(samples);
    }

    private static bool AddSampleBox(Image<Rgba32> canvas, int x, int y, int width, int height, List<Rgba32> samples)
    {
        var left = Math.Clamp(x, 0, canvas.Width);
        var top = Math.Clamp(y, 0, canvas.Height);
        var right = Math.Clamp(x + width, 0, canvas.Width);
        var bottom = Math.Clamp(y + height, 0, canvas.Height);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        canvas.ProcessPixelRows(accessor =>
        {
            for (var rowIndex = top; rowIndex < bottom; rowIndex++)
            {
                var row = accessor.GetRowSpan(rowIndex);
                for (var columnIndex = left; columnIndex < right; columnIndex++)
                {
                    if (row[columnIndex].A > 0)
                    {
                        samples.Add(row[columnIndex]);
                    }
                }
            }
        });
        return true;
    }

    private static Rgba32 MedianColor(Image<Rgba32> image)
    {
        var samples = new List<Rgba32>(image.Width * image.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A > 0)
                    {
                        samples.Add(row[x]);
                    }
                }
            }
        });
        return MedianColor(samples);
    }

    private static Rgba32 MedianColor(IReadOnlyList<Rgba32> colors)
    {
        if (colors.Count == 0)
        {
            return new Rgba32(34, 34, 34, 255);
        }

        byte Channel(Func<Rgba32, byte> selector)
        {
            var ordered = colors.Select(selector).OrderBy(value => value).ToArray();
            return ordered[ordered.Length / 2];
        }

        return new Rgba32(
            Channel(static color => color.R),
            Channel(static color => color.G),
            Channel(static color => color.B),
            Channel(static color => color.A));
    }

    private static void DrawWrappedText(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        string text,
        Rgba32 fill,
        int fontSize,
        bool bold,
        string align,
        string verticalAlign)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var paddingX = Math.Min(8, Math.Max(2, rect.Width / 18));
        var paddingY = Math.Min(5, Math.Max(1, rect.Height / 8));
        var font = GetFont(Math.Max(8, fontSize), bold);
        var maxWidth = Math.Max(1, rect.Width - paddingX * 2);
        var maxLines = Math.Max(1, rect.Height / Math.Max(12, fontSize + 3));
        var lines = WrapText(text, font, maxWidth, maxLines);
        var lineHeight = Math.Max(fontSize + 3, (int)Math.Ceiling(TextMeasurer.MeasureBounds("Ag", new TextOptions(font)).Height) + 2);
        var totalHeight = lineHeight * lines.Count;
        var y = verticalAlign == "top"
            ? rect.Y + paddingY
            : rect.Y + Math.Max(paddingY, (rect.Height - totalHeight) / 2);

        foreach (var line in lines)
        {
            var fitted = FitTextEnd(line, font, maxWidth);
            var bounds = TextMeasurer.MeasureBounds(fitted, new TextOptions(font));
            var textWidth = (int)Math.Ceiling(bounds.Width);
            var x = align == "center"
                ? rect.X + (rect.Width - textWidth) / 2
                : rect.X + paddingX;
            canvas.Mutate(ctx => ctx.DrawText(fitted, font, fill, new PointF(x, y)));
            y += lineHeight;
        }
    }

    private static void DrawSingleLineText(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        string text,
        Rgba32 fill,
        int fontSize,
        bool bold,
        string align,
        bool truncateMiddle = true)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var paddingX = Math.Min(10, Math.Max(2, rect.Width / 32));
        var maxWidth = Math.Max(1, rect.Width - paddingX * 2);
        var size = Math.Max(8, fontSize);
        var font = GetFont(size, bold);
        while (size > 8 && TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width > maxWidth)
        {
            size--;
            font = GetFont(size, bold);
        }

        var finalText = truncateMiddle ? FitTextMiddle(text, font, maxWidth) : FitTextEnd(text, font, maxWidth);
        var bounds = TextMeasurer.MeasureBounds(finalText, new TextOptions(font));
        var textWidth = (int)Math.Ceiling(bounds.Width);
        var textHeight = (int)Math.Ceiling(bounds.Height);
        var x = align == "center"
            ? rect.X + (rect.Width - textWidth) / 2
            : rect.X + paddingX;
        var y = rect.Y + Math.Max(0, (rect.Height - textHeight) / 2) - 1;
        canvas.Mutate(ctx => ctx.DrawText(finalText, font, fill, new PointF(x, y)));
    }

    private static void DrawTrackSingleLineText(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        string text,
        Rgba32 fill,
        int fontSize,
        bool bold)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var paddingX = Math.Min(8, Math.Max(2, rect.Width / 18));
        var maxWidth = Math.Max(1, rect.Width - paddingX * 2);
        var size = Math.Max(8, fontSize);
        var font = GetFont(size, bold);
        while (size > 8 && TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width > maxWidth)
        {
            size--;
            font = GetFont(size, bold);
        }

        var bounds = TextMeasurer.MeasureBounds(text, new TextOptions(font));
        var textHeight = (int)Math.Ceiling(bounds.Height);
        var x = rect.X + paddingX;
        var y = rect.Y + Math.Max(0, (rect.Height - textHeight) / 2) - (float)Math.Floor(bounds.Top);
        canvas.Mutate(ctx => ctx.DrawText(text, font, fill, new PointF(x, y)));
    }

    private static List<string> WrapText(string text, Font font, int maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    lines.Add(current.TrimEnd());
                }
                current = string.Empty;
                if (lines.Count >= maxLines)
                {
                    return lines;
                }
                continue;
            }

            var next = current + ch;
            if (TextMeasurer.MeasureBounds(next, new TextOptions(font)).Width <= maxWidth || current.Length == 0)
            {
                current = next;
                continue;
            }

            lines.Add(current.TrimEnd());
            current = ch.ToString();
            if (lines.Count >= maxLines)
            {
                return lines;
            }
        }

        if (!string.IsNullOrWhiteSpace(current) && lines.Count < maxLines)
        {
            lines.Add(current.TrimEnd());
        }

        return lines.Count == 0 ? [text] : lines;
    }

    private static string FitTextEnd(string text, Font font, int maxWidth)
    {
        if (TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";
        if (TextMeasurer.MeasureBounds(ellipsis, new TextOptions(font)).Width >= maxWidth)
        {
            return ellipsis;
        }

        var working = text;
        while (working.Length > 1 && TextMeasurer.MeasureBounds(working + ellipsis, new TextOptions(font)).Width > maxWidth)
        {
            working = working[..^1];
        }

        return working + ellipsis;
    }

    private static string FitTextMiddle(string text, Font font, int maxWidth)
    {
        if (TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";
        var dotsWidth = TextMeasurer.MeasureBounds(ellipsis, new TextOptions(font)).Width;
        if (dotsWidth >= maxWidth)
        {
            return ellipsis;
        }

        var budget = maxWidth - dotsWidth;
        var headBudget = budget * 0.55f;
        var head = new List<char>();
        var tail = new LinkedList<char>();
        var usedHead = 0f;
        var usedTail = 0f;

        foreach (var ch in text)
        {
            var width = TextMeasurer.MeasureBounds(ch.ToString(), new TextOptions(font)).Width;
            if (usedHead + width > headBudget)
            {
                break;
            }
            head.Add(ch);
            usedHead += width;
        }

        foreach (var ch in text.Skip(head.Count).Reverse())
        {
            var width = TextMeasurer.MeasureBounds(ch.ToString(), new TextOptions(font)).Width;
            if (usedTail + width > budget - usedHead)
            {
                break;
            }
            tail.AddFirst(ch);
            usedTail += width;
        }

        return string.Concat(head) + ellipsis + string.Concat(tail);
    }

    private static Font GetFont(float size, bool bold)
    {
        var family = TryFindCjkFontFamily() ?? SystemFonts.Families.First();
        return family.CreateFont(size, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    private static Image<Rgba32> ResizeCrop(Image<Rgba32> source, int width, int height)
    {
        return source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center
        }));
    }

    private static Image<Rgba32> ResizeBoxPad(Image<Rgba32> source, int width, int height, Rgba32 padColor)
    {
        var canvas = new Image<Rgba32>(width, height, padColor);
        var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        var scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point((width - scaledWidth) / 2, (height - scaledHeight) / 2), 1f));
        return canvas;
    }

    private static Image<Rgba32> BuildTrackThumbnailCard(Image<Rgba32> sourceFrame, string title, int width, int height)
    {
        var card = new Image<Rgba32>(width, height, new Rgba32(38, 38, 38, 255));
        var previewHeight = Math.Max(1, (int)Math.Round(height * 0.72));
        using var preview = ResizeCrop(sourceFrame, width, previewHeight);
        card.Mutate(ctx =>
        {
            ctx.DrawImage(preview, Point.Empty, 1f);
            ctx.Fill(new Rgba32(20, 20, 20, 220), new RectangleF(0, previewHeight, width, height - previewHeight));
        });

        DrawWrappedText(
            card,
            new ProjectImageTemplateRegion(8, previewHeight + 6, Math.Max(1, width - 16), Math.Max(1, height - previewHeight - 8)),
            title,
            new Rgba32(226, 226, 226, 255),
            12,
            false,
            "left",
            "top");
        return card;
    }

    private static int? LoadProjectImageCount(string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            return null;
        }

        var config = KeyValueConfigReader.Read(configFile);
        return config.TryGetValue("ProjectImageCount", out var rawCount) &&
               int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? LoadProjectImageRenderEpisodeLimit(IReadOnlyDictionary<string, string> configMap)
    {
        return configMap.TryGetValue("ProjectImageRenderEpisodeLimit", out var rawLimit) &&
               int.TryParse(rawLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : null;
    }

    private async Task<double> GetDurationSecondsAsync(string ffprobe, string videoPath, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", videoPath],
            Path.GetDirectoryName(videoPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 获取视频时长失败: {result.StandardError}");
        }

        if (!double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw new InvalidOperationException($"无法解析视频时长: {result.StandardOutput}");
        }

        return seconds;
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

    private static double ResolveEpisodePreviewTime(double durationSeconds)
    {
        return Math.Max(0.1, Math.Min(2.0, durationSeconds * 0.2));
    }

    private static string FormatTrackTimecode(double durationSeconds, int fps = 25)
    {
        var totalFrames = Math.Max(0, (int)Math.Round(durationSeconds * Math.Max(1, fps)));
        var frame = totalFrames % fps;
        var totalSeconds = totalFrames / fps;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}:{frame:00}";
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
                // ignore unusable fonts
            }
        }

        return null;
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

        var packaged = BundledToolResolver.TryResolveBinary(name);
        if (packaged is not null)
        {
            return packaged;
        }

        throw new InvalidOperationException($"未找到 {name}，请先将 {name} 加入系统 PATH。");
    }

    private sealed record ChatCompletionResponse(IReadOnlyList<ChatChoice>? Choices);
    private sealed record ChatChoice(ChatMessage? Message);
    private sealed record ChatMessage(string? Content);
    private sealed record SubtitleAiResponse([property: JsonPropertyName("subtitle")] string? Subtitle);
}
