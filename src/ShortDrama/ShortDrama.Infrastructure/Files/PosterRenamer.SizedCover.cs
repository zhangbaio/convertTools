using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer
{
    public static void DrawLowerTitle(string inputPath, string outputPath, string title) =>
        PosterTitleProgrammaticRenderer.RenderLowerHalf(inputPath, outputPath, title);

    /// <summary>
    /// 按指定请求尺寸做一次全图编辑，再居中裁成目标画布。去字模式由调用方决定是否再画标题。
    /// </summary>
    public async Task GenerateSizedCoverAsync(
        string configFile,
        string inputPath,
        string outputPath,
        string title,
        string prompt,
        string safeRetryPrompt,
        int targetWidth,
        int targetHeight,
        string requestSize,
        bool drawTitleWithProgram,
        bool verifyTitle,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var config = KeyValueConfigReader.Read(configFile);
        var previousCall = CurrentCall.Value;
        CurrentCall.Value = new PosterCallContext(log, IsFastPosterLayoutMode(GetOptional(config, "PosterLayoutRequestMode")));
        try
        {
            var bytes = await GenerateSizedCoverBytesAsync(
                config, inputPath, prompt, safeRetryPrompt, requestSize, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await SaveFittedJpegAsync(bytes, outputPath, targetWidth, targetHeight, cancellationToken);
            log?.Invoke($"已生成横屏封面：{Path.GetFileName(outputPath)}（{targetWidth}×{targetHeight}）");
            if (drawTitleWithProgram)
            {
                DrawLowerTitle(outputPath, outputPath, title);
                log?.Invoke($"已在横屏封面下半部分写入固定标题：{title}");
                return;
            }

            if (verifyTitle)
                await VerifySizedCoverTitleAsync(configFile, config, outputPath, title, log, cancellationToken);
        }
        finally
        {
            CurrentCall.Value = previousCall;
        }
    }

    private async Task<byte[]> GenerateSizedCoverBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        string prompt,
        string safeRetryPrompt,
        string requestSize,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var imageQuality = NormalizeImageQuality(GetOptional(config, "ImageQuality"));
        var extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        var mediaType = GuessMediaType(extension);
        var imageSize = string.IsNullOrWhiteSpace(requestSize) ? "2560x1600" : requestSize.Trim();
        var layout = new PosterLayout(
            0, 0.65f, 1, 0.28f, 0.09f,
            new Rgba32(255, 255, 255),
            new Rgba32(17, 17, 17),
            0,
            SixLabors.Fonts.HorizontalAlignment.Center);

        try
        {
            return await GeneratePosterWithAiPromptAsync(
                requestUrl, apiPath, modelId, apiKey, inputPath, mediaType, layout, prompt,
                provider, imageQuality, imageSize, cancellationToken, editFullImage: true);
        }
        catch (PosterSensitiveContentException)
        {
            return await GeneratePosterWithAiPromptAsync(
                requestUrl, apiPath, modelId, apiKey, inputPath, mediaType, layout, safeRetryPrompt,
                provider, imageQuality, imageSize, cancellationToken, editFullImage: true);
        }
    }

    private static async Task SaveFittedJpegAsync(
        byte[] bytes,
        string outputPath,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream(bytes);
        using var generated = await Image.LoadAsync<Rgba32>(buffer, cancellationToken);
        if (generated.Width != targetWidth || generated.Height != targetHeight)
        {
            generated.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));
        }

        await generated.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = 92 }, cancellationToken);
    }

    private async Task VerifySizedCoverTitleAsync(
        string configFile,
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!IsPosterTitleVerifyEnabled(config))
            return;

        PosterLayout layout;
        try
        {
            layout = await DetectPosterLayoutAsync(configFile, outputPath, title, log, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"横屏标题位置未定位，保留当前封面：{ex.Message}");
            return;
        }

        var request = new PosterRenameRequest(Path.GetDirectoryName(outputPath) ?? ".", OutputFilePath: outputPath, Log: log);
        var verify = await VerifyGeneratedTitleAsync(config, outputPath, title, layout, request, cancellationToken);
        if (verify.Ok)
        {
            log?.Invoke($"横屏封面标题校验通过：{verify.DetectedTitle ?? title}");
            return;
        }

        log?.Invoke($"横屏封面标题校验未通过：{verify.Reason}");
        if (await TryRepairTitleWithAiRetriesAsync(config, outputPath, title, layout, verify, request, cancellationToken))
            return;

        var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
        if (verifyMode == "warn")
        {
            log?.Invoke("横屏封面标题校验失败，已按配置保留当前封面。");
            return;
        }

        if (verifyMode == "blocking")
            throw new InvalidOperationException($"横屏封面标题校验失败：{verify.Reason}");

        await FallbackRepaintVerifiedTitleAsync(
            config, outputPath, title, layout, verify, request, cancellationToken);
    }
}
