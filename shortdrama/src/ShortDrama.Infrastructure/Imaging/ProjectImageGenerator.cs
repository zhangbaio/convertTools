using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
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
        if (sourceVideos.Length == 0)
        {
            throw new InvalidOperationException($"未在目录中找到可用视频文件: {request.InputDir}");
        }

        var ffmpeg = ResolveBinary("ffmpeg");
        var ffprobe = ResolveBinary("ffprobe");
        var episodeNames = sourceVideos
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
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
                    index + 1,
                    portrait,
                    cancellationToken);

                composite.Save(outputPath, new PngEncoder());
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
        int currentIndex,
        bool portrait,
        CancellationToken cancellationToken)
    {
        var canvas = templateImage.Clone();
        var pageIndex = ResolvePageIndex(manifest, page);
        var videoTrackRects = page.GetRegions("video_track_images");
        var trackEpisodeIndex = ResolveTrackEpisodeIndex(pageIndex, currentIndex, episodeNames.Count, episodeFrames.Count, videoTrackRects);
        var singleEpisodeTrackFrames = videoTrackRects.Count > 0 && NoteInt(videoTrackRects[0], "single_episode_track", 0, 0, 1) > 0
            ? await ExtractTrackFramesAsync(
                ffmpeg,
                sourceVideos[Math.Clamp(trackEpisodeIndex, 0, sourceVideos.Count - 1)],
                episodeDurations[Math.Clamp(trackEpisodeIndex, 0, episodeDurations.Count - 1)],
                videoTrackRects[0],
                cancellationToken)
            : null;

        try
        {
            var displayFrames = ResolveVideoTrackDisplayFrames(videoTrackRects, episodeFrames, trackEpisodeIndex + 1, singleEpisodeTrackFrames);
            var previewFrame = SelectTrackPlayheadFrame(videoTrackRects, displayFrames)
                ?? episodeFrames[Math.Clamp(trackEpisodeIndex, 0, episodeFrames.Count - 1)];
            var playerRect = ResolvePlayerRegion(page, portrait);
            using var subtitleProbe = playerRect is not null
                ? BuildPlayerImage(previewFrame, playerRect)
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

            RenderAutosaveStatus(canvas, page);
            RenderTopTitle(canvas, page, projectInfo.Title);
            RenderPlayer(canvas, page, previewFrame, portrait);
            RenderRightSubtitle(canvas, page, portrait, subtitleText);
            RenderMaterialPanel(canvas, page, episodeFrames, episodeNames, episodeDurations, projectInfo.Title);
            RenderVideoTrackImages(canvas, videoTrackRects, episodeFrames, trackEpisodeIndex + 1, singleEpisodeTrackFrames);
            RenderTrackTexts(canvas, page, pageIndex, currentIndex, episodeNames, episodeDurations, projectInfo.Title, episodeFrames.Count, videoTrackRects, subtitleText);

            return canvas;
        }
        finally
        {
            if (singleEpisodeTrackFrames is not null)
            {
                foreach (var frame in singleEpisodeTrackFrames)
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

    private static void RenderAutosaveStatus(Image<Rgba32> canvas, ProjectImageTemplatePage page)
    {
        var rect = page.GetRegion("autosave_status");
        if (rect is null)
        {
            return;
        }

        FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));

        var generatedAt = DateTime.Now;
        var timeFormat = NoteValue(rect, "format") ?? "HH:mm:ss";
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

        DrawSingleLineText(
            canvas,
            new ProjectImageTemplateRegion(rect.X + offsetX, rect.Y, Math.Max(1, rect.Width - offsetX), rect.Height, rect.Note),
            text,
            fill,
            fontSize,
            false,
            "left",
            truncateMiddle: false);
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

        FillRect(canvas, rect, new Rgba32(0, 0, 0, 255));
        using var playerImage = BuildPlayerImage(previewFrame, rect);
        canvas.Mutate(ctx => ctx.DrawImage(playerImage, new Point(rect.X, rect.Y + 2), 1f));
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

        FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));
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

        using var strip = new Image<Rgba32>(rect.Width, rect.Height, SampleRectColor(canvas, rect));
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

        foreach (var rect in page.GetRegions("video_track_texts"))
        {
            FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));
            DrawSingleLineText(
                canvas,
                ExtendTrackTextRect(rect, videoTrackRects, canvas.Width),
                trackTextWithDuration,
                new Rgba32(245, 245, 245, 255),
                11,
                false,
                "left",
                truncateMiddle: false);
        }

        foreach (var rect in page.GetRegions("audio_track_texts"))
        {
            FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));
            DrawSingleLineText(
                canvas,
                ExtendTrackTextRect(rect, videoTrackRects, canvas.Width),
                trackText,
                new Rgba32(245, 245, 245, 255),
                11,
                false,
                "left",
                truncateMiddle: false);
        }

        foreach (var rect in page.GetRegions("subtitle_track_texts"))
        {
            FillRect(canvas, rect, SampleSurroundingColor(canvas, rect));
            DrawSingleLineText(canvas, rect, subtitleText, new Rgba32(245, 245, 245, 255), 11, false, "left", truncateMiddle: false);
        }
    }

    private async Task<List<Image<Rgba32>>> ExtractTrackFramesAsync(
        string ffmpeg,
        string videoPath,
        double durationSeconds,
        ProjectImageTemplateRegion rect,
        CancellationToken cancellationToken)
    {
        var thumbHeight = GetTrackThumbnailHeight(rect);
        var clipWidth = Math.Max(28, Math.Min(72, (int)Math.Round(thumbHeight * 1.35)));
        var clipCount = Math.Max(1, (int)Math.Ceiling(rect.Width / (double)clipWidth));
        var frames = new List<Image<Rgba32>>(clipCount);

        for (var index = 0; index < clipCount; index++)
        {
            var time = clipCount <= 1
                ? ResolveEpisodePreviewTime(durationSeconds)
                : Math.Max(0.1, durationSeconds * (index + 1d) / (clipCount + 1d));
            frames.Add(await ExtractFrameAsync(ffmpeg, videoPath, time, cancellationToken));
        }

        return frames;
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

        if (rects.Count == 0 || NoteInt(rects[0], "single_episode_track", 0, 0, 1) == 0)
        {
            return episodeFrames;
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
        var frameIndex = Math.Clamp((playheadX - rect.X) / clipWidth, 0, frames.Count - 1);
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

    private static Image<Rgba32> BuildPlayerImage(Image<Rgba32> previewFrame, ProjectImageTemplateRegion rect)
    {
        var canvas = new Image<Rgba32>(rect.Width, rect.Height, new Rgba32(0, 0, 0, 255));
        if (previewFrame.Width > previewFrame.Height)
        {
            var ratio = previewFrame.Width / (double)Math.Max(1, previewFrame.Height);
            var targetWidth = rect.Width;
            var targetHeight = Math.Max(1, Math.Min(rect.Height, (int)Math.Round(targetWidth / ratio)));
            if (targetHeight < Math.Max(1, rect.Height / 4))
            {
                targetHeight = Math.Max(1, rect.Height / 4);
                targetWidth = Math.Max(1, Math.Min(rect.Width, (int)Math.Round(targetHeight * ratio)));
            }

            using var content = ResizeBoxPad(previewFrame, targetWidth, targetHeight, new Rgba32(0, 0, 0, 255));
            canvas.Mutate(ctx => ctx.DrawImage(content, new Point((rect.Width - targetWidth) / 2, (rect.Height - targetHeight) / 2), 1f));
            return canvas;
        }

        using var portraitContent = ResizeBoxPad(previewFrame, rect.Width, rect.Height, new Rgba32(0, 0, 0, 255));
        canvas.Mutate(ctx => ctx.DrawImage(portraitContent, Point.Empty, 1f));
        return canvas;
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

    private static Image<Rgba32>? CapturePlayheadOverlay(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var playheadX = NoteInt(rect, "playhead_x", int.MinValue, -10000, 10000);
        if (playheadX == int.MinValue)
        {
            return null;
        }

        playheadX += NoteInt(rect, "playhead_x_offset", 0, -20, 20);
        var localX = Math.Clamp(playheadX - rect.X - 4, 0, Math.Max(0, rect.Width - 1));
        var overlayWidth = Math.Min(10, rect.Width - localX);
        return overlayWidth <= 0
            ? null
            : canvas.Clone(ctx => ctx.Crop(new Rectangle(rect.X + localX, rect.Y, overlayWidth, rect.Height)));
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
        AddSampleBox(canvas, rect.X - band, rect.Y, band, rect.Height, samples);
        AddSampleBox(canvas, rect.X + rect.Width, rect.Y, band, rect.Height, samples);
        if (samples.Count == 0)
        {
            AddSampleBox(canvas, rect.X, rect.Y - band, rect.Width, band, samples);
            AddSampleBox(canvas, rect.X, rect.Y + rect.Height, rect.Width, band, samples);
        }

        return samples.Count == 0 ? SampleRectColor(canvas, rect) : MedianColor(samples);
    }

    private static void AddSampleBox(Image<Rgba32> canvas, int x, int y, int width, int height, List<Rgba32> samples)
    {
        var left = Math.Clamp(x, 0, canvas.Width);
        var top = Math.Clamp(y, 0, canvas.Height);
        var right = Math.Clamp(x + width, 0, canvas.Width);
        var bottom = Math.Clamp(y + height, 0, canvas.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        using var crop = canvas.Clone(ctx => ctx.Crop(new Rectangle(left, top, right - left, bottom - top)));
        samples.Add(MedianColor(crop));
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

        throw new InvalidOperationException($"未找到 {name}，请先将 {name} 加入系统 PATH。");
    }

    private sealed record ChatCompletionResponse(IReadOnlyList<ChatChoice>? Choices);
    private sealed record ChatChoice(ChatMessage? Message);
    private sealed record ChatMessage(string? Content);
    private sealed record SubtitleAiResponse([property: JsonPropertyName("subtitle")] string? Subtitle);
}
