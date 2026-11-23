using System.Net;
using System.Text.Json;
using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer
{
    private async Task GenerateWithVisiblePromptAsync(
        PosterRenameRequest request,
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        string outputPath,
        string title,
        string posterMode,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var stagingDirectory = Path.Combine(outputDirectory, $".visible-poster-{Guid.NewGuid():N}");
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        var requestInputPath = Path.Combine(stagingDirectory, $"原图输入{Path.GetExtension(renderInputPath)}");
        var candidatePath = Path.Combine(stagingDirectory, $"候选海报{Path.GetExtension(outputPath)}");
        var bottomPath = Path.Combine(stagingDirectory, "无字底图.png");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await using (var source = File.OpenRead(renderInputPath))
            await using (var snapshot = File.Create(requestInputPath))
                await source.CopyToAsync(snapshot, cancellationToken);

            using var original = await Image.LoadAsync<Rgba32>(requestInputPath, cancellationToken);
            var writeTitle = posterMode == PosterGenerationTemplates.EraseMode;
            var requireDifference = posterMode == PosterReferenceCover.Mode;
            var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
            var imageSize = NormalizeImageSize(GetOptional(config, "ImageSize"), provider);
            var hasConfiguredSize = PosterCoverFrameSizeHelper.TryParseSize(imageSize, out var configuredWidth, out var configuredHeight);
            var ratio = !writeTitle && hasConfiguredSize
                ? GetConfiguredAspectRatio(config, provider, configuredWidth, configuredHeight)
                : FormatAspectRatio(original.Width, original.Height);
            var prompt = PosterGenerationTemplates.ForMode(posterMode)
                .Replace("{ratio}", ratio, StringComparison.Ordinal)
                .Replace("{title}", title, StringComparison.Ordinal);
            var requestSize = writeTitle
                ? GetEraseRequestSize(config, provider, original.Width, original.Height)
                : imageSize.Length == 0 ? "2K" : imageSize;

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log(request, $"以原图 {Path.GetFileName(inputPath)} 请求第 {attempt} 张候选海报，比例 {ratio}，尺寸 {requestSize}。");
                Image<Rgba32> image;
                try
                {
                    var bytes = await GenerateSizedCoverBytesAsync(
                        config, requestInputPath, prompt, safeRetryPrompt: null, requestSize, cancellationToken);
                    await using var buffer = new MemoryStream(bytes);
                    image = await Image.LoadAsync<Rgba32>(buffer, cancellationToken);
                }
                catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested &&
                    IsVisibleImageRetryable(ex))
                {
                    Log(request, $"图片生成失败，将使用相同提示词、原图和配置重试一次：{ex.Message}");
                    continue;
                }

                using (image)
                {
                    if (writeTitle)
                        FitVisiblePoster(image, original.Width, original.Height);
                    else if (hasConfiguredSize)
                        FitVisiblePoster(image, configuredWidth, configuredHeight);
                    else
                        FitVisiblePosterAspect(image, original.Width, original.Height);

                    if (requireDifference)
                    {
                        var difference = PosterCoverDifference.Measure(original, image, cancellationToken: cancellationToken);
                        Log(request, $"第 {attempt} 张候选封面的有效画面差异 {difference:P1}，要求至少 {PosterReferenceCover.MinimumChangedArea:P0}。");
                        if (difference < PosterReferenceCover.MinimumChangedArea)
                        {
                            if (attempt == maxAttempts)
                            {
                                throw new InvalidOperationException(
                                    $"候选封面有效画面差异 {difference:P1}，低于 {PosterReferenceCover.MinimumChangedArea:P0}，已保留已有封面。");
                            }

                            Log(request, "画面差异不足，将使用相同提示词、原图和配置重试一次。");
                            continue;
                        }
                    }

                    if (writeTitle)
                    {
                        await image.SaveAsPngAsync(bottomPath, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        PosterTitleProgrammaticRenderer.RenderLowerHalf(bottomPath, candidatePath, title);
                        Log(request, "已在无字底图上写入固定字体标题。");
                    }
                    else
                    {
                        await image.SaveAsync(candidatePath, cancellationToken);
                    }
                }

                if (bool.TryParse(GetOptional(config, "PosterTitleVerifyEnabled"), out var verify) && verify)
                    await ReportVisiblePosterTitleAsync(config, candidatePath, title, request, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(candidatePath, outputPath, request.Overwrite);
                return;
            }
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal))
                TryDeleteFile(renderInputPath);

            TryDeleteFile(requestInputPath);
            TryDeleteFile(candidatePath);
            TryDeleteFile(bottomPath);
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(request, $"清理封面临时目录失败：{ex.Message}");
            }
        }
    }

    private async Task ReportVisiblePosterTitleAsync(
        IReadOnlyDictionary<string, string> config,
        string imagePath,
        string title,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        var verifier = new PosterTitleVerificationService(_httpClient);
        var result = await verifier.VerifyAsync(config, imagePath, title, layout: null, cancellationToken,
            PosterGenerationTemplates.TitleVerificationPrompt);
        if (result.Ok)
            Log(request, $"标题校验通过：{result.DetectedTitle}");
        else if (result.IsInconclusive)
            Log(request, $"标题校验无法确认：{result.Reason}。本次仅报告结果。");
        else
            Log(request, $"标题校验发现差异：{result.Reason}。本次仅报告结果。");
    }

    private static bool IsVisibleImageRetryable(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)http.StatusCode.GetValueOrDefault() >= 500,
        OperationCanceledException or TimeoutException or JsonException or FormatException or InvalidDataException or
            UnknownImageFormatException or InvalidImageContentException => true,
        _ => false,
    };

    private static string GetEraseRequestSize(IReadOnlyDictionary<string, string> config, string provider, int width, int height)
    {
        if (IsOpenAiImageProvider(provider))
            return "auto";

        var size = PosterCoverFrameSizeHelper.ResolveFrameApiSize(width, height, config);
        return PosterCoverFrameSizeHelper.TryParseSize(size, out var apiWidth, out var apiHeight) &&
            (long)apiWidth * apiHeight <= 4_624_220 ? size : "2K";
    }

    private static string GetConfiguredAspectRatio(IReadOnlyDictionary<string, string> config, string provider, int width, int height)
    {
        var configured = provider == "doubao" ? GetOptional(config, "DoubaoImageRatio") : null;
        return configured is not null && PosterCoverFrameSizeHelper.TryParseSize(configured.Replace(':', 'x'), out _, out _)
            ? configured
            : FormatAspectRatio(width, height);
    }

    private static string FormatAspectRatio(int width, int height)
    {
        var divisor = width;
        var remainder = height;
        while (remainder != 0)
            (divisor, remainder) = (remainder, divisor % remainder);

        return $"{width / divisor}:{height / divisor}";
    }

    private static void FitVisiblePosterAspect(Image<Rgba32> image, int width, int height)
    {
        var scale = Math.Min(image.Width / (double)width, image.Height / (double)height);
        FitVisiblePoster(image, Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static void FitVisiblePoster(Image<Rgba32> image, int width, int height)
    {
        if (image.Width != width || image.Height != height)
            ResizeToCanvasPreservingAspect(image, width, height);
    }
}
