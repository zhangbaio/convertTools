using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer
{
    private const string DefaultPosterTitleErasePrompt = """
这是海报标题区域去字任务，只处理遮罩区域。
请移除遮罩区域内所有标题文字、汉字、描边、阴影、发光和文字装饰。
用周围背景自然补全遮罩区域，保持原海报风格、光影、纹理和构图连续。
不要生成任何新文字、符号、logo、水印、印章或标记。
遮罩区域外的所有人物、背景、颜色、清晰度和构图必须保持不变。
""";

    private readonly PosterTitleVerificationService _titleVerifier = new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

    private static void Log(PosterRenameRequest request, string message) => request.Log?.Invoke(message);

    private static string NormalizePosterMode(string? value)
    {
        var mode = (value ?? "original").Trim().ToLowerInvariant();
        return mode switch
        {
            "ai" => "poster_ai_edit",
            _ => mode,
        };
    }

    private async Task TryGeneratePosterWithVerificationAsync(
        string configFile,
        string inputPath,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        await TryGeneratePosterWithAiAsync(configFile, inputPath, outputPath, title, layout, cancellationToken);

        var config = KeyValueConfigReader.Read(configFile);
        if (!IsPosterTitleVerifyEnabled(config))
            return;

        var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
        var titleLayout = ToTitleLayout(layout);
        var verifyResult = await _titleVerifier.VerifyAsync(config, outputPath, title, titleLayout, cancellationToken);
        if (verifyResult.Ok)
        {
            if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
                Log(request, $"AI 海报标题校验通过：{verifyResult.DetectedTitle}");
            else
                Log(request, $"AI 海报标题校验通过：{title}");
            return;
        }

        var reason = string.IsNullOrWhiteSpace(verifyResult.Reason)
            ? "标题校验未通过"
            : verifyResult.Reason;
        if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
            reason = $"{reason}（识别标题：{verifyResult.DetectedTitle}）";

        if (verifyMode == "warn")
        {
            Log(request, $"AI 海报标题校验失败：{reason}，已按配置跳过阻断。");
            return;
        }

        if (verifyMode == "blocking")
            throw new InvalidOperationException($"AI 海报标题校验失败：{reason}");

        if (verifyMode == "image2_regenerate")
        {
            try
            {
                await Image2RegenerateVerifiedTitleAsync(
                    config, outputPath, title, layout, verifyResult, request, cancellationToken);
                var secondVerify = await _titleVerifier.VerifyAsync(config, outputPath, title, titleLayout, cancellationToken);
                if (secondVerify.Ok)
                {
                    Log(request, "AI海报标题初次校验未通过，已通过 Image2 重生成修复");
                    return;
                }

                Log(request, $"Image2 重生成后二次校验未通过，改用AI去字+PIL重绘兜底：{secondVerify.Reason}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(request, $"Image2 重生成失败，改用AI去字+PIL重绘兜底：{ex.Message}");
            }
        }

        await FallbackRepaintVerifiedTitleAsync(
            config, outputPath, title, layout, verifyResult, request, cancellationToken);
    }

    private async Task GenerateErasePilTitlePosterAsync(
        PosterRenameRequest request,
        string inputPath,
        string outputPath,
        string title,
        string configFile,
        CancellationToken cancellationToken)
    {
        Log(request, "使用 AI去字+PIL重绘 模式生成海报…");
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            var layout = await DetectPosterLayoutAsync(configFile, renderInputPath, title, cancellationToken);
            await WriteGeneratedPosterCandidateAsync(renderInputPath, await File.ReadAllBytesAsync(renderInputPath, cancellationToken), outputPath, cancellationToken);
            var config = KeyValueConfigReader.Read(configFile);
            await FallbackRepaintVerifiedTitleAsync(
                config,
                outputPath,
                title,
                layout,
                PosterTitleVerifyResult.Fail("封面生成模式：AI消除文字+PIL绘制新标题"),
                request,
                cancellationToken);
            Log(request, $"已生成AI去字+PIL绘制标题封面：{Path.GetFileName(outputPath)}");
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal) && File.Exists(renderInputPath))
                File.Delete(renderInputPath);
        }
    }

    private async Task GenerateCoverFromPosterAsync(
        PosterRenameRequest request,
        string inputPath,
        string outputPath,
        string title,
        string configFile,
        CancellationToken cancellationToken)
    {
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            using var posterImage = await Image.LoadAsync<Rgba32>(renderInputPath, cancellationToken);
            var posterW = posterImage.Width;
            var posterH = posterImage.Height;
            Log(request, $"原始封面：{Path.GetFileName(inputPath)} ({posterW}x{posterH})");

            var config = KeyValueConfigReader.Read(configFile);
            var promptTemplate = GetOptional(config, "FrameCoverPrompt") ?? DefaultPosterGenerationPrompt;
            var prompt = RenderPromptTemplate(
                promptTemplate,
                CreatePosterPromptVariables(title, title, null, null));
            var apiSize = PosterCoverFrameSizeHelper.ResolveFrameApiSize(posterW, posterH, config);
            Log(request, $"AI 生成中：请求尺寸 {apiSize}");

            var resultBytes = await AiEditFrameAsync(posterImage, prompt, config, apiSize, request, cancellationToken);
            await ResizeAndSaveAsync(resultBytes, posterW, posterH, outputPath, cancellationToken);
            Log(request, $"已生成封面：{Path.GetFileName(outputPath)} ({posterW}x{posterH})");

            if (!IsPosterTitleVerifyEnabled(config))
                return;

            var layout = await DetectPosterLayoutAsync(configFile, outputPath, title, cancellationToken);
            var titleLayout = ToTitleLayout(layout);
            var verifyResult = await _titleVerifier.VerifyAsync(config, outputPath, title, titleLayout, cancellationToken);
            if (verifyResult.Ok)
            {
                Log(request, $"AI 封面标题校验通过：{verifyResult.DetectedTitle ?? title}");
                return;
            }

            var reason = verifyResult.Reason;
            if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
                reason = $"{reason}（识别标题：{verifyResult.DetectedTitle}）";
            var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
            if (verifyMode == "warn")
            {
                Log(request, $"AI 封面标题校验失败：{reason}，已按配置跳过阻断。");
                return;
            }

            if (verifyMode == "blocking")
                throw new InvalidOperationException($"AI 封面标题校验失败：{reason}");

            await FallbackRepaintVerifiedTitleAsync(
                config, outputPath, title, layout, verifyResult, request, cancellationToken);
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal) && File.Exists(renderInputPath))
                File.Delete(renderInputPath);
        }
    }

    private async Task FallbackRepaintVerifiedTitleAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        Log(request, "AI海报标题校验未通过，开始AI去字+PIL重绘兜底");
        var erasedPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}.title_erased.png");
        var erasePrompt = GetOptional(config, "PosterTitleErasePrompt") ?? DefaultPosterTitleErasePrompt;
        var erasedBytes = await GenerateTitleErasedPosterBytesAsync(
            config, outputPath, layout, erasePrompt, cancellationToken);
        await WriteGeneratedPosterCandidateAsync(outputPath, erasedBytes, erasedPath, cancellationToken);
        Log(request, $"已生成AI去字底图：{Path.GetFileName(erasedPath)}");

        PosterTitleProgrammaticRenderer.Render(
            erasedPath,
            outputPath,
            title,
            ToTitleLayout(layout));
        Log(request, $"已使用PIL重绘标准标题：{Path.GetFileName(outputPath)}");

        var finalVerify = await _titleVerifier.VerifyAsync(config, outputPath, title, ToTitleLayout(layout), cancellationToken);
        if (finalVerify.Ok)
            Log(request, "AI海报标题初次校验未通过，已通过AI去字+PIL重绘兜底修复");
        else
            Log(request, $"AI海报标题已用PIL确定性重绘完成；二次校验未通过但保留兜底结果：{finalVerify.Reason}");
    }

    private async Task Image2RegenerateVerifiedTitleAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        Log(request, "AI海报标题校验未通过，开始用 Image2 重生成标题");
        var regeneratedBytes = await GenerateImage2TitleRegeneratedPosterBytesAsync(config, outputPath, title, cancellationToken);
        await WriteGeneratedPosterCandidateAsync(outputPath, regeneratedBytes, outputPath, cancellationToken);
    }

    private async Task<byte[]> GenerateTitleErasedPosterBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        PosterLayout layout,
        string prompt,
        CancellationToken cancellationToken)
    {
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var imageQuality = NormalizeImageQuality(GetOptional(config, "ImageQuality"));
        var imageSize = NormalizeImageSize(GetOptional(config, "ImageSize"), provider);
        var mediaType = GuessMediaType(Path.GetExtension(inputPath).TrimStart('.'));

        return await GeneratePosterWithAiPromptAsync(
            requestUrl,
            apiPath,
            modelId,
            apiKey,
            inputPath,
            mediaType,
            layout,
            MergePromptWithGuardrails(prompt, Path.GetFileNameWithoutExtension(inputPath)),
            provider,
            imageQuality,
            imageSize,
            cancellationToken);
    }

    private async Task<byte[]> GenerateImage2TitleRegeneratedPosterBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        string title,
        CancellationToken cancellationToken)
    {
        var image2Config = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
        {
            ["ImageProvider"] = "ofox_image2",
        };
        var endpoint = (GetOptional(image2Config, "OfoxImage2Endpoint") ?? "https://api.ofox.ai/v1").TrimEnd('/');
        var apiPath = "/images/edits";
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(image2Config, "OfoxImage2ModelId") ?? "openai/gpt-image-2";
        var apiKey = GetOptional(image2Config, "OfoxImage2ApiKey") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Image2 重生成需要配置 OfoxImage2ApiKey");

        var prompt = BuildImage2TitleRegenerationPrompt(title);
        var quality = GetOptional(image2Config, "OfoxImage2Quality")
            ?? GetOptional(image2Config, "ImageQuality")
            ?? "medium";
        var size = GetOptional(image2Config, "OfoxImage2Size") ?? "auto";
        var mediaType = GuessMediaType(Path.GetExtension(inputPath).TrimStart('.'));

        return await GeneratePosterWithEditFormNoMaskAsync(
            requestUrl,
            modelId,
            apiKey,
            inputPath,
            mediaType,
            prompt,
            "ofox_image2",
            quality,
            size,
            cancellationToken);
    }

    private async Task<byte[]> AiEditFrameAsync(
        Image<Rgba32> frameImage,
        string prompt,
        IReadOnlyDictionary<string, string> config,
        string apiSize,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        var tempFrame = Path.Combine(Path.GetTempPath(), $"frame-cover-{Guid.NewGuid():N}.png");
        try
        {
            await frameImage.SaveAsPngAsync(tempFrame, cancellationToken);
            var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
            var endpoint = (GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint")).TrimEnd('/');
            var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
            var requestUrl = BuildApiUrl(endpoint, apiPath);
            var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
            var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
            Log(request, $"AI 封面模型：{provider} / {modelId}");

            if (apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            {
                return await GeneratePosterWithGenerationsJsonAsync(
                    requestUrl,
                    modelId,
                    apiKey,
                    tempFrame,
                    "image/png",
                    prompt,
                    provider,
                    NormalizeImageQuality(GetOptional(config, "ImageQuality")),
                    NormalizeImageSize(apiSize, provider),
                    cancellationToken);
            }

            var editSize = apiSize;
            if (IsOpenAiImageProvider(provider))
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "auto", "1024x1024", "1536x1024", "1024x1536",
                };
                if (!allowed.Contains(editSize))
                {
                    Log(request, $"Ofox/Image2 编辑接口按文档改用 size=auto（原请求尺寸 {editSize}）");
                    editSize = "auto";
                }
            }

            return await GeneratePosterWithEditFormNoMaskAsync(
                requestUrl,
                modelId,
                apiKey,
                tempFrame,
                "image/png",
                prompt,
                provider,
                NormalizeImageQuality(GetOptional(config, "ImageQuality")),
                editSize,
                cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempFrame);
        }
    }

    private async Task<byte[]> GeneratePosterWithEditFormNoMaskAsync(
        string requestUrl,
        string modelId,
        string apiKey,
        string inputPath,
        string mediaType,
        string prompt,
        string provider,
        string imageQuality,
        string imageSize,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(modelId), "model");
        form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        if (!IsOpenAiImageProvider(provider))
            form.Add(new StringContent("b64_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(imageSize))
            form.Add(new StringContent(imageSize), "size");
        if (IsOpenAiImageProvider(provider) && !string.IsNullOrWhiteSpace(imageQuality))
            form.Add(new StringContent(imageQuality), "quality");

        using var imageStream = File.OpenRead(inputPath);
        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(imageContent, "image", Path.GetFileName(inputPath));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            ThrowPosterApiFailure("AI 海报图片编辑", requestUrl, response, responseText);

        return await ReadImageResponseBytesAsync(responseText, cancellationToken)
            ?? throw new InvalidOperationException("AI 海报图片编辑成功返回，但响应中没有可解析的图片数据。");
    }

    private static async Task WriteGeneratedPosterCandidateAsync(
        string inputPath,
        byte[] imageBytes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempOut = Path.Combine(Path.GetTempPath(), $"poster-output-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempOut, imageBytes, cancellationToken);
        try
        {
            var sourceInfo = await Image.IdentifyAsync(inputPath, cancellationToken)
                ?? throw new InvalidOperationException($"无法读取原海报尺寸: {inputPath}");
            using var generated = await Image.LoadAsync<Rgba32>(tempOut, cancellationToken);
            if (generated.Width != sourceInfo.Width || generated.Height != sourceInfo.Height)
                generated.Mutate(ctx => ctx.Resize(sourceInfo.Width, sourceInfo.Height));
            await generated.SaveAsync(outputPath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempOut);
        }
    }

    private static async Task ResizeAndSaveAsync(
        byte[] imageBytes,
        int targetW,
        int targetH,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cover-resize-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken);
        try
        {
            using var img = await Image.LoadAsync<Rgba32>(tempPath, cancellationToken);
            if (img.Width == targetW && img.Height == targetH)
                await img.SaveAsync(outputPath, cancellationToken);
            else
            {
                img.Mutate(ctx => ctx.Resize(targetW, targetH));
                await img.SaveAsync(outputPath, cancellationToken);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static string BuildImage2TitleRegenerationPrompt(string title)
    {
        var safeTitle = (title ?? "").Trim();
        return "请编辑这张短剧海报，只修复标题文字区域。"
               + "保持人物、背景、构图、比例、色调和光影基本不变，不要重画主体人物，不要改变图片尺寸比例。"
               + "删除图片中错误、乱码、残缺、重复、方括号、书名号、引号或不完整的标题文字。"
               + $"重新添加准确的简体中文标题：{safeTitle}。"
               + "标题成品只能包含剧名本身，不要添加 []、《》、“”、拼音、英文、数字、书名号、引号或任何包裹符号。"
               + "必须逐字一致，不能漏字、改字、使用繁体字、异体字、形近字、乱码或艺术变形到难以识别的字。"
               + "标题要清晰、醒目、有短剧海报质感，使用常见、标准、易识别的简体中文海报粗标题。"
               + "最终画面中只能出现一次完整标题，不得保留旧标题残影、底层文字或重复标题。";
    }

    private static PosterTitleLayout ToTitleLayout(PosterLayout layout) =>
        PosterTitleProgrammaticRenderer.ToTitleLayout(
            layout.X,
            layout.Y,
            layout.Width,
            layout.Height,
            layout.FontScale,
            layout.TextColor,
            layout.BackgroundColor,
            layout.BackgroundOpacity,
            layout.Align);

    private static bool IsPosterTitleVerifyEnabled(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("PosterTitleVerifyEnabled", out var value) || string.IsNullOrWhiteSpace(value))
            return true;
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
