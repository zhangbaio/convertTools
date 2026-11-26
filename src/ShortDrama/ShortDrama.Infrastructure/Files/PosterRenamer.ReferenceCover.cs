using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer
{
    private async Task GenerateReferenceCoverAsync(
        PosterRenameRequest request,
        string inputPath,
        string outputPath,
        string title,
        CancellationToken cancellationToken)
    {
        var configFile = request.ConfigFile!;
        var config = ReadConfig(configFile);
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var imageSize = NormalizeImageSize(GetOptional(config, "ImageSize"), provider);
        if (imageSize.Length == 0)
            imageSize = "2K";

        var prompt = RenderPromptTemplate(
            PosterReferenceCover.DefaultPrompt,
            CreatePosterPromptVariables(title, title, null, null));
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var stagingDirectory = Path.Combine(outputDirectory, $".reference-cover-{Guid.NewGuid():N}");
        var candidatePath = Path.Combine(stagingDirectory, Path.GetFileName(outputPath));
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using var original = await Image.LoadAsync<Rgba32>(renderInputPath, cancellationToken);
            var originalLayout = await TryDetectReferenceTitleLayoutAsync(
                configFile, config, renderInputPath, title, request, cancellationToken);
            var originalTitle = GetReferenceTitleBounds(originalLayout);

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var attemptPrompt = attempt == 1 ? prompt : $"{prompt}\n\n{PosterReferenceCover.AlternateComposition}";
                Log(request, $"以原图 {Path.GetFileName(inputPath)} 生成第 {attempt} 张候选封面，请求尺寸 {imageSize}。");
                var bytes = await GenerateSizedCoverBytesAsync(
                    config, renderInputPath, attemptPrompt, safeRetryPrompt: null, imageSize, cancellationToken);

                await using var buffer = new MemoryStream(bytes);
                using var image = await Image.LoadAsync<Rgba32>(buffer, cancellationToken);
                if (PosterCoverFrameSizeHelper.TryParseSize(imageSize, out var width, out var height) &&
                    (image.Width != width || image.Height != height))
                {
                    ResizeToCanvasPreservingAspect(image, width, height);
                }

                await image.SaveAsync(candidatePath, cancellationToken);
                var candidateLayout = await TryDetectReferenceTitleLayoutAsync(
                    configFile, config, candidatePath, title, request, cancellationToken);
                var candidateTitle = GetReferenceTitleBounds(candidateLayout);
                var changedArea = PosterCoverDifference.Measure(original, image, originalTitle, candidateTitle, cancellationToken);
                Log(request, $"第 {attempt} 张候选封面的有效画面差异 {changedArea:P1}，要求至少 {PosterReferenceCover.MinimumChangedArea:P0}。");
                if (changedArea >= PosterReferenceCover.MinimumChangedArea)
                {
                    await VerifyCoverTitleAsync(configFile, config, candidatePath, title, request, cancellationToken, candidateLayout);
                    if (IsPosterTitleVerifyEnabled(config))
                    {
                        using var finalImage = await Image.LoadAsync<Rgba32>(candidatePath, cancellationToken);
                        changedArea = PosterCoverDifference.Measure(original, finalImage, originalTitle, candidateTitle, cancellationToken);
                        if (changedArea < PosterReferenceCover.MinimumChangedArea)
                            Log(request, $"标题处理后的有效画面差异降至 {changedArea:P1}。");
                    }
                }

                if (changedArea >= PosterReferenceCover.MinimumChangedArea)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(candidatePath, outputPath, request.Overwrite);
                    return;
                }

                if (attempt == maxAttempts)
                    throw new InvalidOperationException($"新封面连续两次与原图过于相似，最后一次有效画面差异为 {changedArea:P1}，低于 {PosterReferenceCover.MinimumChangedArea:P0}。");

                Log(request, "画面差异不足，改用另一套构图重试一次。");
            }
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal))
                TryDeleteFile(renderInputPath);

            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(request, $"清理封面临时文件失败：{ex.Message}");
            }
        }
    }

    private async Task<PosterLayout?> TryDetectReferenceTitleLayoutAsync(
        string configFile,
        IReadOnlyDictionary<string, string> config,
        string imagePath,
        string title,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsPosterTitleVerifyEnabled(config) || string.IsNullOrWhiteSpace(GetOptional(config, "ChatModelEndpoint")) ||
            string.IsNullOrWhiteSpace(GetOptional(config, "ChatModelId")) || string.IsNullOrWhiteSpace(GetOptional(config, "ChatModelApiKey")))
            return null;

        try
        {
            return await DetectPosterLayoutAsync(configFile, imagePath, title, request.Log, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(request, $"未定位到 {Path.GetFileName(imagePath)} 的标题框，画面评分将避开底部文字区域：{ex.Message}");
            return null;
        }
    }

    private static RectangleF? GetReferenceTitleBounds(PosterLayout? layout) =>
        layout is null ? null : new RectangleF(layout.X, layout.Y, layout.Width, layout.Height);
}
