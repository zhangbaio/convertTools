using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Config;
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
        var config = KeyValueConfigReader.Read(configFile);
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
            Log(request, $"以原图 {Path.GetFileName(inputPath)} 生成新封面，请求尺寸 {imageSize}。");
            var bytes = await GenerateSizedCoverBytesAsync(
                config, renderInputPath, prompt, safeRetryPrompt: null, imageSize, cancellationToken);

            Directory.CreateDirectory(stagingDirectory);
            await using (var buffer = new MemoryStream(bytes))
            using (var image = await Image.LoadAsync<Rgba32>(buffer, cancellationToken))
            {
                if (PosterCoverFrameSizeHelper.TryParseSize(imageSize, out var width, out var height) &&
                    (image.Width != width || image.Height != height))
                {
                    ResizeToCanvasPreservingAspect(image, width, height);
                }

                await image.SaveAsync(candidatePath, cancellationToken);
            }

            await VerifyCoverTitleAsync(configFile, config, candidatePath, title, request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(candidatePath, outputPath, request.Overwrite);
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
}
