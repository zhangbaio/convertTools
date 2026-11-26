using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Networking;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouPosterSettings(
    bool EraseMode,
    string VerticalPrompt,
    string VerticalSafeRetryPrompt,
    string HorizontalPrompt,
    string HorizontalSafeRetryPrompt,
    string ImageProvider,
    string ImageModelId,
    string ImageModelApiKey,
    string ImageModelEndpoint,
    string ImageEditPath,
    string ImageQuality,
    string HorizontalRequestSize,
    string ChatEndpoint,
    string ChatApiKey,
    string ChatModel,
    bool TitleVerifyEnabled,
    string TitleVerifyMode,
    int TitleVerifyAiRetryCount,
    string LayoutDetectPrompt);

/// <summary>
/// 用现有海报步骤生成快手竖屏海报，再把成品或无文字底图扩成 1600×1000 横屏封面。
/// </summary>
public sealed class KuaishouPosterService(HttpClient? imageClient = null)
{
    public const int VerticalWidth = 1200;
    public const int VerticalHeight = 1600;
    public const int HorizontalWidth = 1600;
    public const int HorizontalHeight = 1000;
    public const string VerticalPosterFileName = "快手竖屏海报.jpg";
    public const string HorizontalCoverFileName = "快手横屏封面.jpg";
    public const string ErasedPosterFileName = "海报处理_已清除标题.png";

    public async Task PrepareAsync(
        string projectDirectory,
        KuaishouPosterSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ImageModelEndpoint) ||
            string.IsNullOrWhiteSpace(settings.ImageModelApiKey) ||
            string.IsNullOrWhiteSpace(settings.ImageModelId))
            throw new InvalidOperationException("请在快手发布配置的 AI 图片中填写模型和 API Key，或使用本机默认。");
        if (!settings.EraseMode && settings.TitleVerifyEnabled &&
            (string.IsNullOrWhiteSpace(settings.ChatEndpoint) ||
             string.IsNullOrWhiteSpace(settings.ChatApiKey) ||
             string.IsNullOrWhiteSpace(settings.ChatModel)))
            throw new InvalidOperationException("生成海报需要快手发布配置中的 AI 文本模型和 API Key，用于定位标题。");

        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        Directory.CreateDirectory(directories.WorkflowDirectory);
        var source = FindSourcePoster(directories.SourceDirectory, directories.WorkflowDirectory);
        var infoPath = Path.Combine(directories.WorkflowDirectory, "短剧信息.txt");
        if (!File.Exists(infoPath))
        {
            var sourceInfo = Path.Combine(directories.SourceDirectory, "短剧信息.txt");
            if (File.Exists(sourceInfo))
                File.Copy(sourceInfo, infoPath, overwrite: false);
        }

        var title = ReadTitle(directories.WorkflowDirectory);
        var pending = Directory.CreateDirectory(Path.Combine(
            directories.WorkflowDirectory, $".kuaishou-poster-{Guid.NewGuid():N}")).FullName;
        try
        {
            using var defaultClient = imageClient is null ? DefaultNetworkHttp.CreateClient(TimeSpan.FromMinutes(5)) : null;
            var renamer = new PosterRenamer(new TxtProjectInfoParser(), new ExternalProcessRunner(),
                imageClient ?? defaultClient!, NullLogger<PosterRenamer>.Instance, BuildConfig(settings));
            void Log(string message) => progress?.Report(message);

            Log("开始重新设计 3:4 竖屏海报。");
            var portraitBytes = await renamer.EditFullPosterAsync(source,
                ComposePrompt(settings.VerticalPrompt, title, horizontal: false, settings.EraseMode),
                ComposePrompt(settings.VerticalSafeRetryPrompt, title, horizontal: false, settings.EraseMode),
                VerticalRequestSize(settings.ImageProvider), Log, cancellationToken);
            var artworkPath = Path.Combine(pending, settings.EraseMode ? ErasedPosterFileName : "海报图片.png");
            using (var image = Image.Load<Rgba32>(portraitBytes))
            {
                if (image.Width >= image.Height)
                    throw new InvalidOperationException("模型未返回竖屏海报，请重试生成海报。");
                FitPortrait(image);
                await image.SaveAsPngAsync(artworkPath, cancellationToken);
            }

            if (!settings.EraseMode && settings.TitleVerifyEnabled)
            {
                await renamer.VerifyPosterTitleAsync(artworkPath, title, Log, cancellationToken);
                await EnsurePortraitSizeAsync(artworkPath, cancellationToken);
            }

            var portraitPng = Path.Combine(pending, "海报图片.png");
            if (settings.EraseMode)
                PosterRenamer.DrawLowerTitle(artworkPath, portraitPng, title);
            await SaveJpegCopyAsync(portraitPng, Path.Combine(pending, VerticalPosterFileName), cancellationToken);

            Log("开始扩展 16:10 横屏背景并回贴完整竖屏画面。");
            var horizontalPath = Path.Combine(pending, HorizontalCoverFileName);
            await GenerateLandscapeAsync(renamer, artworkPath, horizontalPath, pending, title, settings, Log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var files = new List<string> { "海报图片.png", VerticalPosterFileName, HorizontalCoverFileName };
            if (settings.EraseMode) files.Add(ErasedPosterFileName);
            foreach (var name in files)
                File.Move(Path.Combine(pending, name), Path.Combine(directories.WorkflowDirectory, name), overwrite: true);
            Log("已生成快手 3:4 竖屏海报与 1600×1000 横屏封面。");
        }
        finally
        {
            try { Directory.Delete(pending, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task GenerateLandscapeAsync(
        PosterRenamer renamer, string portraitPath, string outputPath, string pending, string title,
        KuaishouPosterSettings settings, Action<string> log, CancellationToken cancellationToken)
    {
        // 竖屏图已经归一为 3:4，横屏画布内只做等比缩放。
        var width = HorizontalHeight * VerticalWidth / VerticalHeight;
        var offset = new Point((HorizontalWidth - width) / 2, 0);
        using var portrait = await Image.LoadAsync<Rgba32>(portraitPath, cancellationToken);
        portrait.Mutate(context => context.Resize(width, HorizontalHeight));
        var seedPath = Path.Combine(pending, "扩图画布.png");
        using (var seed = new Image<Rgba32>(HorizontalWidth, HorizontalHeight, new Rgba32(128, 128, 128)))
        {
            seed.Mutate(context => context.DrawImage(portrait, offset, 1f));
            await seed.SaveAsPngAsync(seedPath, cancellationToken);
        }

        try
        {
            var bytes = await renamer.EditFullPosterAsync(seedPath,
                ComposePrompt(settings.HorizontalPrompt, title, horizontal: true, settings.EraseMode),
                ComposePrompt(settings.HorizontalSafeRetryPrompt, title, horizontal: true, settings.EraseMode),
                settings.HorizontalRequestSize, log, cancellationToken);
            using var expanded = Image.Load<Rgba32>(bytes);
            if (expanded.Width <= expanded.Height)
                throw new InvalidOperationException("模型未返回横屏背景。");

            // 供应商请求尺寸可能与快手画布不同，只调整扩图背景。
            expanded.Mutate(context => context.Resize(HorizontalWidth, HorizontalHeight)
                .DrawImage(portrait, offset, 1f));
            if (settings.EraseMode)
            {
                var expandedBase = Path.Combine(pending, "横屏无字底图.png");
                await expanded.SaveAsPngAsync(expandedBase, cancellationToken);
                PosterRenamer.DrawLowerTitle(expandedBase, outputPath, title);
            }
            else
            {
                await expanded.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = 95 }, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"快手横屏扩图未完成，请重试生成海报。{ex.Message}", ex);
        }
    }

    private static void FitPortrait(Image image)
    {
        if (image.Width != VerticalWidth || image.Height != VerticalHeight)
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(VerticalWidth, VerticalHeight),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));
    }

    private static async Task EnsurePortraitSizeAsync(string path, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(path, cancellationToken);
        if (image.Width == VerticalWidth && image.Height == VerticalHeight)
            return;
        FitPortrait(image);
        await image.SaveAsPngAsync(path, cancellationToken);
    }

    private static bool UsesOpenAiSizes(string? provider) =>
        (provider ?? "").Trim().ToLowerInvariant() is "ofox_image2" or "openai_image2";

    public static string VerticalRequestSize(string? provider) =>
        UsesOpenAiSizes(provider) ? "1024x1536" : "1776x2368";

    public static string HorizontalRequestSize(string? provider) =>
        UsesOpenAiSizes(provider) ? "1536x1024" : "2560x1600";

    public static string ComposePrompt(string prompt, string title, bool horizontal, bool eraseMode)
    {
        var includesTitle = prompt.Contains("{title}", StringComparison.Ordinal);
        var text = prompt.Trim().Replace("{title}", eraseMode ? "" : title, StringComparison.Ordinal);
        if (horizontal)
            return text + "\n\n输入画布中央是需要完整保留的竖屏画面，只补全两侧的灰色背景占位区域。" +
                (eraseMode ? "输出不得出现文字。" : "不要在扩图区域添加文字。");
        if (eraseMode)
            return text + "\n\n输出一张 3:4 无字底图，底部预留标题区。";
        return text + "\n\n输出一张 3:4 竖屏海报。" +
            (includesTitle ? "" : $"只写一次新剧名「{title}」。");
    }

    public static bool HasSourcePoster(string sourceDirectory, string workflowDirectory)
    {
        try
        {
            FindSourcePoster(sourceDirectory, workflowDirectory);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public static string FindSourcePoster(string sourceDirectory, string workflowDirectory)
    {
        var directories = DistinctDirectories(sourceDirectory, workflowDirectory).ToArray();
        foreach (var directory in directories)
        {
            foreach (var name in new[] { "海报原图.png", "海报原图.jpg", "海报原图.jpeg", "海报原图.webp", "海报原图.heic", "海报原图.heif" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }
        }

        foreach (var directory in directories)
        {
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg", "海报图片.jpeg", "海报图片.webp" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }
        }

        foreach (var directory in directories)
        {
            var candidate = Directory.EnumerateFiles(directory)
                .Where(IsUsableSourceImage)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is not null)
                return candidate;
        }

        throw new FileNotFoundException(
            "未找到可用于生成快手封面的海报图片：项目目录和 workflow 里都没有海报原图。" +
            "海报会在下载剧集的最后一步下载，请先确认下载剧集已完成。");
    }

    private static async Task SaveJpegCopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(sourcePath, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await image.SaveAsJpegAsync(targetPath, new JpegEncoder { Quality = 92 }, cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> BuildConfig(KuaishouPosterSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ImageProvider"] = settings.ImageProvider,
            ["ImageModelId"] = settings.ImageModelId,
            ["ImageModelApiKey"] = settings.ImageModelApiKey,
            ["ImageModelEndpoint"] = settings.ImageModelEndpoint,
            ["ImageEditModelId"] = settings.ImageModelId,
            ["ImageEditApiKey"] = settings.ImageModelApiKey,
            ["ImageEditEndpoint"] = settings.ImageModelEndpoint,
            ["ImageEditPath"] = settings.ImageEditPath,
            ["ImageQuality"] = settings.ImageQuality,
            ["ChatModelEndpoint"] = settings.ChatEndpoint,
            ["ChatModelApiKey"] = settings.ChatApiKey,
            ["ChatModelId"] = settings.ChatModel,
            ["PosterLayoutDetectPrompt"] = settings.LayoutDetectPrompt,
            ["PosterLayoutRequestMode"] = "standard",
            ["PosterTitleVerifyEnabled"] = settings.TitleVerifyEnabled ? "true" : "false",
            ["PosterTitleVerifyMode"] = settings.TitleVerifyMode,
            ["PosterTitleVerifyAiRetryCount"] = settings.TitleVerifyAiRetryCount.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string ReadTitle(string workflowDirectory)
    {
        var path = Path.Combine(workflowDirectory, "短剧信息.txt");
        if (!File.Exists(path))
            throw new InvalidOperationException("未找到短剧信息.txt，无法确定横屏封面标题。");
        string? fallback = null;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index <= 0)
                continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (value.Length == 0)
                continue;
            if (key is "新剧名" or "新剧名称")
                return value;
            if (fallback is null && key is "原剧名" or "剧名")
                fallback = value;
        }

        return fallback ?? throw new InvalidOperationException("短剧信息里没有新剧名，无法生成横屏封面。");
    }

    private static IEnumerable<string> DistinctDirectories(string sourceDirectory, string workflowDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { sourceDirectory, workflowDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            if (seen.Add(Path.GetFullPath(directory)))
                yield return directory;
        }
    }

    private static bool IsUsableSourceImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".heic" or ".heif"))
            return false;
        var name = Path.GetFileNameWithoutExtension(path);
        return name is not ("快手横屏封面" or "快手竖屏海报" or "海报处理_已清除标题")
               && !name.StartsWith("工程图_", StringComparison.Ordinal)
               && !name.StartsWith("成本报表", StringComparison.Ordinal)
               && !name.StartsWith(".poster-source-", StringComparison.Ordinal)
               && !name.StartsWith(".kuaishou-poster-", StringComparison.Ordinal);
    }

}
