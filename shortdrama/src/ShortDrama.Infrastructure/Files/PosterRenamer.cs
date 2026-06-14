using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Files;

public sealed class PosterRenamer : IPosterRenamer
{
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif"];
    private const string DefaultPosterLayoutDetectPrompt = """
你是短剧海报版式分析助手。请识别海报上“现有主标题文字”的最小覆盖区域，并返回 JSON。
要求：
1. 只返回 JSON，不要解释。
2. 所有坐标和尺寸都用 0 到 1 的比例。
3. 只能返回现有主标题区域，不要返回海报其他空白区域。
4. 这个矩形要刚好能覆盖原标题，尽量小，不要影响其他图像元素。
5. 颜色返回十六进制，如 #F6E85A。
6. backgroundOpacity 取 0 到 1。若可以仅靠新标题覆盖旧标题，请返回 0。
7. 新标题风格默认使用短剧海报常见的大号黄色字体、黑色粗描边，位置优先在底部。

JSON 结构：
{
  "x": 0.18,
  "y": 0.73,
  "width": 0.64,
  "height": 0.12,
  "fontScale": 0.08,
  "textColor": "#F6E85A",
  "backgroundColor": "#1A1A1A",
  "backgroundOpacity": 0,
  "align": "center"
}

只分析当前海报里已经存在的主标题，不要自己设计新位置。
需要替换成的新剧名：{title}
""";
    private const string DefaultPosterInpaintPrompt = """
这是海报局部改字任务，不是重绘海报。
只允许在遮罩区域内把原有剧名文字替换为“{title}”。
除了剧名文字本身，图片其余任何内容都不能修改，包括但不限于：
1. 人物脸部、姿势、服装、发型。
2. 背景、道具、光影、颜色、清晰度。
3. 构图、裁切、排版、装饰元素、特效。
4. 原图中未被文字遮挡的任何像素。

输出要求：
1. 保持原海报整体视觉完全一致，只做文字替换。
2. 新文字位置与原剧名区域一致，横向居中。
3. 字体风格保持短剧海报标题风格，大号黄色粗体，带黑色粗描边。
4. 不要新增任何其他文字，不要润色，不要改图，不要重画。
""";
    private const string DefaultPosterInpaintSafeRetryPrompt = """
这是安全合规的局部改字任务。
只允许在遮罩区域内将现有标题文字替换为“{title}”。
不要新增任何人物变化、冲突场景、受伤效果、成人暗示、敏感剧情元素。
不能修改遮罩区域外的任何像素，人物、背景、服装、道具、光影、构图保持与原图一致。
输出为普通、安全、清晰的中文海报标题，仅完成标题替换。
""";
    private const string DefaultPosterGenerationPrompt = """
参考输入海报图，执行一次精确的海报改字编辑。
只把海报中现有的剧名文字替换为“{title}”。
其余所有内容必须保持不变，包括人物、表情、服装、背景、颜色、光影、构图、比例、清晰度和装饰元素。
不要重绘角色，不要修改背景，不要新增任何文字。
新剧名必须是清晰可读的中文，风格保持短剧海报标题样式，大号黄色粗体，黑色粗描边，位置与原剧名区域一致。
这是局部文字替换任务，不是重新生成整张海报。
""";
    private const string DefaultPosterGenerationSafeRetryPrompt = """
参考输入海报，执行一次安全合规的局部标题替换。
只把现有主标题替换为“{title}”，其余画面保持不变。
这是普通宣传图编辑任务，不要增加任何暴力、血腥、成人、医疗、政治、未成年人风险或其他敏感元素。
不要改变人物、背景、服装、动作、表情、光影、构图、装饰和清晰度。
输出仅需生成安全、合规、清晰的中文标题文字。
""";
    private const string DefaultPosterNameSystemPrompt = "你是短剧海报命名助手。请输出一个适合作为海报文件名主标题的短句。不要带扩展名、不要带引号、不要输出解释。";
    private const string DefaultPosterNameUserPrompt = """
请为这个短剧生成 1 个适合作为海报文件名的中文标题。
要求：
1. 8 到 18 个汉字。
2. 风格偏短剧宣发，有钩子感。
3. 不要输出“海报”“短剧”“jpg”“png”等字样。
4. 不要带标点、引号、解释。
5. 只输出标题本身。

短剧标题：{project_title}
原剧名：{original_title}
推荐语：{tagline}
简介：{synopsis}
""";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IProjectInfoParser _projectInfoParser;
    private readonly IExternalProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PosterRenamer> _logger;

    public PosterRenamer(
        IProjectInfoParser projectInfoParser,
        IExternalProcessRunner processRunner,
        HttpClient httpClient,
        ILogger<PosterRenamer> logger)
    {
        _projectInfoParser = projectInfoParser;
        _processRunner = processRunner;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PosterRenameResult> RenameAsync(
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(request.ProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {request.ProjectDir}");
        }

        var project = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var inputPath = ResolveInputFile(request.ProjectDir, request.InputFilePath);
        var posterName = request.UseAi
            ? await GeneratePosterNameAsync(project, request.ConfigFile, cancellationToken)
            : project.Title;

        var extension = Path.GetExtension(inputPath);
        var outputPath = request.OutputFilePath
            ?? Path.Combine(request.ProjectDir, BuildFileName(posterName, request.NameTemplate, extension));

        if (File.Exists(outputPath) && !string.Equals(inputPath, outputPath, StringComparison.Ordinal))
        {
            if (!request.Overwrite)
            {
                throw new InvalidOperationException($"目标海报文件已存在: {outputPath}");
            }

            File.Delete(outputPath);
        }

        await RenderPosterAsync(inputPath, outputPath, posterName, request.ConfigFile, cancellationToken);

        _logger.LogInformation("Updated poster: {Input} -> {Output}", inputPath, outputPath);
        return new PosterRenameResult(inputPath, outputPath, posterName);
    }

    private async Task RenderPosterAsync(
        string inputPath,
        string outputPath,
        string posterName,
        string? configFile,
        CancellationToken cancellationToken)
    {
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
            {
                throw new InvalidOperationException("AI 海报图片生成必须提供有效的 configFile。");
            }

            var layout = await DetectPosterLayoutAsync(configFile, renderInputPath, posterName, cancellationToken);
            await TryGeneratePosterWithAiAsync(
                configFile,
                renderInputPath,
                outputPath,
                posterName,
                layout,
                cancellationToken);

            _logger.LogInformation("AI 海报图片生成成功: {Output}", outputPath);
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal) && File.Exists(renderInputPath))
            {
                File.Delete(renderInputPath);
            }
        }
    }

    private static string ResolveInputFile(string projectDir, string? inputFilePath)
    {
        if (!string.IsNullOrWhiteSpace(inputFilePath))
        {
            if (!File.Exists(inputFilePath))
            {
                throw new FileNotFoundException($"未找到海报文件: {inputFilePath}", inputFilePath);
            }

            return inputFilePath;
        }

        var preferred = SupportedExtensions
            .Select(ext => Path.Combine(projectDir, $"海报图片{ext}"))
            .FirstOrDefault(File.Exists);

        if (preferred is not null)
        {
            return preferred;
        }

        var candidate = Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return !fileName.StartsWith("工程图_", StringComparison.Ordinal) &&
                       !fileName.StartsWith("成本报表", StringComparison.Ordinal) &&
                       !fileName.StartsWith("seal.prepared", StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return candidate
            ?? throw new InvalidOperationException($"未在项目目录中找到可重命名的海报图片: {projectDir}。当前支持 jpg/jpeg/png/webp/heic/heif。");
    }

    private static string BuildFileName(string projectTitle, string? nameTemplate, string extension)
    {
        var template = string.IsNullOrWhiteSpace(nameTemplate)
            ? "{name}-海报"
            : nameTemplate;

        var fileName = template.Replace("{name}", projectTitle, StringComparison.Ordinal);
        return $"{fileName}{extension}";
    }

    private async Task<string> PrepareRenderableInputAsync(string inputPath, CancellationToken cancellationToken)
    {
        try
        {
            await Image.IdentifyAsync(inputPath, cancellationToken);
            return inputPath;
        }
        catch when (OperatingSystem.IsMacOS())
        {
            var tempPngPath = Path.Combine(
                Path.GetTempPath(),
                $"{Path.GetFileNameWithoutExtension(inputPath)}.{Guid.NewGuid():N}.png");

            var result = await _processRunner.RunAsync(
                "/usr/bin/sips",
                ["-s", "format", "png", inputPath, "--out", tempPngPath],
                Path.GetDirectoryName(inputPath),
                cancellationToken);

            if (result.ExitCode != 0 || !File.Exists(tempPngPath))
            {
                throw new InvalidOperationException($"海报图片格式转换失败。stderr: {result.StandardError}");
            }

            return tempPngPath;
        }
    }

    private async Task<PosterLayout> DetectPosterLayoutAsync(
        string? configFile,
        string imagePath,
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            throw new InvalidOperationException("AI 海报布局检测必须提供有效的 configFile。");
        }

        var config = KeyValueConfigReader.Read(configFile);
        var endpoint = GetRequired(config, "ChatModelEndpoint").TrimEnd('/');
        var modelId = GetRequired(config, "ChatModelId");
        var apiKey = GetRequired(config, "ChatModelApiKey");
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath, cancellationToken));
        var extension = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        var mediaType = extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "image/png"
        };

        var prompt = RenderPromptTemplate(
            GetOptional(config, "PosterLayoutDetectPrompt") ?? DefaultPosterLayoutDetectPrompt,
            CreatePosterPromptVariables(title, title, null, null));

        var payload = new
        {
            model = modelId,
            temperature = 0.2,
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
                                url = $"data:{mediaType};base64,{imageBase64}"
                            }
                        }
                    }
                }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AI 海报布局检测接口请求失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {responseText}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions);
        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("AI 海报布局检测未返回内容。");
        }

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"AI 海报布局检测未返回合法 JSON: {content}");
        }

        var aiLayout = JsonSerializer.Deserialize<PosterLayoutResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("AI 海报布局检测返回的 JSON 无法解析。");

        return new PosterLayout(
            Clamp01(aiLayout.X ?? 0.1f),
            Clamp01(aiLayout.Y ?? 0.78f),
            Clamp01(aiLayout.Width ?? 0.8f),
            Clamp01(aiLayout.Height ?? 0.16f),
            Clamp(aiLayout.FontScale ?? 0.09f, 0.05f, 0.14f),
            ParseColor(aiLayout.TextColor, new Rgba32(255, 255, 255, 255)),
            ParseColor(aiLayout.BackgroundColor, new Rgba32(0, 0, 0, 255)),
            Clamp01(aiLayout.BackgroundOpacity ?? 0.95f),
            string.Equals(aiLayout.Align, "left", StringComparison.OrdinalIgnoreCase) ? HorizontalAlignment.Left :
                string.Equals(aiLayout.Align, "right", StringComparison.OrdinalIgnoreCase) ? HorizontalAlignment.Right :
                HorizontalAlignment.Center);
    }

    private async Task TryGeneratePosterWithAiAsync(
        string configFile,
        string inputPath,
        string outputPath,
        string title,
        PosterLayout layout,
        CancellationToken cancellationToken)
    {
        var config = KeyValueConfigReader.Read(configFile);
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        var mediaType = extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "image/png"
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        var useGenerationApi = apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase);
        var promptVariables = CreatePosterPromptVariables(title, title, null, null);
        var primaryPrompt = useGenerationApi
            ? RenderPromptTemplate(GetOptional(config, "PosterGenerationPrompt") ?? DefaultPosterGenerationPrompt, promptVariables)
            : RenderPromptTemplate(GetOptional(config, "PosterInpaintPrompt") ?? DefaultPosterInpaintPrompt, promptVariables);
        var safeRetryPrompt = useGenerationApi
            ? RenderPromptTemplate(GetOptional(config, "PosterGenerationSafeRetryPrompt") ?? DefaultPosterGenerationSafeRetryPrompt, promptVariables)
            : RenderPromptTemplate(GetOptional(config, "PosterInpaintSafeRetryPrompt") ?? DefaultPosterInpaintSafeRetryPrompt, promptVariables);

        byte[] bytes;
        try
        {
            bytes = await GeneratePosterWithAiPromptAsync(
                requestUrl,
                apiPath,
                modelId,
                apiKey,
                inputPath,
                mediaType,
                layout,
                primaryPrompt,
                timeoutCts.Token);
        }
        catch (PosterSensitiveContentException ex)
        {
            _logger.LogWarning(
                ex,
                "AI 海报图片生成命中内容审核，切换安全提示词重试：{Title}",
                title);
            try
            {
                bytes = await GeneratePosterWithAiPromptAsync(
                    requestUrl,
                    apiPath,
                    modelId,
                    apiKey,
                    inputPath,
                    mediaType,
                    layout,
                    safeRetryPrompt,
                    timeoutCts.Token);
            }
            catch (PosterSensitiveContentException retryEx)
            {
                var sanitizedTitle = SanitizePosterTitleForSafety(title);
                if (string.Equals(sanitizedTitle, title, StringComparison.Ordinal))
                {
                    throw;
                }

                _logger.LogWarning(
                    retryEx,
                    "AI 海报图片生成再次命中内容审核，使用净化标题重试：{OriginalTitle} -> {SanitizedTitle}",
                    title,
                    sanitizedTitle);
                var sanitizedPromptVariables = CreatePosterPromptVariables(sanitizedTitle, title, null, null);
                var sanitizedSafeRetryPrompt = useGenerationApi
                    ? RenderPromptTemplate(GetOptional(config, "PosterGenerationSafeRetryPrompt") ?? DefaultPosterGenerationSafeRetryPrompt, sanitizedPromptVariables)
                    : RenderPromptTemplate(GetOptional(config, "PosterInpaintSafeRetryPrompt") ?? DefaultPosterInpaintSafeRetryPrompt, sanitizedPromptVariables);
                bytes = await GeneratePosterWithAiPromptAsync(
                    requestUrl,
                    apiPath,
                    modelId,
                    apiKey,
                    inputPath,
                    mediaType,
                    layout,
                    sanitizedSafeRetryPrompt,
                    timeoutCts.Token);
            }
        }

        await using var buffer = new MemoryStream(bytes);
        var sourceInfo = await Image.IdentifyAsync(inputPath, timeoutCts.Token)
            ?? throw new InvalidOperationException($"无法读取原海报尺寸信息: {inputPath}");
        using var generated = await Image.LoadAsync<Rgba32>(buffer, timeoutCts.Token);
        if (generated.Width != sourceInfo.Width || generated.Height != sourceInfo.Height)
        {
            generated.Mutate(ctx => ctx.Resize(sourceInfo.Width, sourceInfo.Height));
        }

        await generated.SaveAsync(outputPath, timeoutCts.Token);
    }

    private async Task<byte[]> GeneratePosterWithAiPromptAsync(
        string requestUrl,
        string apiPath,
        string modelId,
        string apiKey,
        string inputPath,
        string mediaType,
        PosterLayout layout,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
        {
            return await GeneratePosterWithGenerationsJsonAsync(
                requestUrl,
                modelId,
                apiKey,
                inputPath,
                mediaType,
                prompt,
                cancellationToken);
        }

        return await GeneratePosterWithEditFormAsync(
            requestUrl,
            modelId,
            apiKey,
            inputPath,
            mediaType,
            prompt,
            layout,
            cancellationToken);
    }

    private async Task<byte[]> GeneratePosterWithGenerationsJsonAsync(
        string requestUrl,
        string modelId,
        string apiKey,
        string inputPath,
        string mediaType,
        string prompt,
        CancellationToken cancellationToken)
    {
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(inputPath, cancellationToken));
        var payload = new
        {
            model = modelId,
            prompt,
            image = $"data:{mediaType};base64,{imageBase64}",
            response_format = "b64_json",
            watermark = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowPosterApiFailure("AI 海报图片生成", requestUrl, response, responseText);
        }

        return await ReadImageResponseBytesAsync(responseText, cancellationToken)
            ?? throw new InvalidOperationException(
                $"AI 海报图片生成成功返回，但响应中没有可解析的图片数据。url: {requestUrl}");
    }

    private async Task<byte[]> GeneratePosterWithEditFormAsync(
        string requestUrl,
        string modelId,
        string apiKey,
        string inputPath,
        string mediaType,
        string prompt,
        PosterLayout layout,
        CancellationToken cancellationToken)
    {
        // Create mask: black opaque everywhere, transparent in the title region (allows AI to edit only there).
        var maskBytes = await CreateTitleMaskAsync(inputPath, layout, cancellationToken);
        if (maskBytes is null)
        {
            throw new InvalidOperationException("无法创建 AI 海报编辑遮罩图。");
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(modelId), "model");
        form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        form.Add(new StringContent("b64_json"), "response_format");
        form.Add(new StringContent("1024x1536"), "size");

        using var imageStream = File.OpenRead(inputPath);
        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(imageContent, "image", Path.GetFileName(inputPath));

        using var maskStream = new MemoryStream(maskBytes);
        var maskContent = new StreamContent(maskStream);
        maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(maskContent, "mask", "mask.png");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowPosterApiFailure("AI 海报图片编辑", requestUrl, response, responseText);
        }

        return await ReadImageResponseBytesAsync(responseText, cancellationToken)
            ?? throw new InvalidOperationException(
                $"AI 海报图片编辑成功返回，但响应中没有可解析的图片数据。url: {requestUrl}");
    }

    private async Task<byte[]?> ReadImageResponseBytesAsync(string responseText, CancellationToken cancellationToken)
    {
        var imageResponse = JsonSerializer.Deserialize<ImageGenerationResponse>(responseText, JsonOptions);
        var item = imageResponse?.Data?.FirstOrDefault();
        if (item is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.B64Json))
        {
            return Convert.FromBase64String(item.B64Json);
        }

        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            return await _httpClient.GetByteArrayAsync(item.Url, cancellationToken);
        }

        return null;
    }

    private static void ThrowPosterApiFailure(
        string operation,
        string requestUrl,
        HttpResponseMessage response,
        string responseText)
    {
        if (IsSensitiveContentBlocked(responseText))
        {
            throw new PosterSensitiveContentException(
                $"{operation}命中内容审核拦截: {(int)response.StatusCode} {response.ReasonPhrase}; url: {requestUrl}; body: {responseText}");
        }

        throw new InvalidOperationException(
            $"{operation}失败: {(int)response.StatusCode} {response.ReasonPhrase}; url: {requestUrl}; body: {responseText}. " +
            "请检查 config.txt 中的 ImageEditEndpoint / ImageEditPath / ImageEditModelId 配置。");
    }

    private static bool IsSensitiveContentBlocked(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        return responseText.Contains("OutputImageSensitiveContentDetected", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("SensitiveContent", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("may contain sensitive information", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("内容审核", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("安全策略", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> CreateTitleMaskAsync(
        string imagePath,
        PosterLayout layout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var source = await Image.LoadAsync<Rgba32>(imagePath, cancellationToken);
            var w = source.Width;
            var h = source.Height;

            using var mask = new Image<Rgba32>(w, h);

            // Fill entire mask with black opaque (preserve everything).
            mask.Mutate(ctx => ctx.Fill(new Rgba32(0, 0, 0, 255)));

            // Compute title rect with 10% padding on each side.
            var padX = (int)Math.Round(w * layout.Width * 0.10f);
            var padY = (int)Math.Round(h * layout.Height * 0.10f);
            var rx = Math.Max(0, (int)Math.Round(w * layout.X) - padX);
            var ry = Math.Max(0, (int)Math.Round(h * layout.Y) - padY);
            var rw = Math.Min(w - rx, (int)Math.Round(w * layout.Width) + padX * 2);
            var rh = Math.Min(h - ry, (int)Math.Round(h * layout.Height) + padY * 2);

            // Clear title region to transparent (alpha=0 means "allow edits here").
            var titleRect = new Rectangle(rx, ry, rw, rh);
            mask.Mutate(ctx => ctx.Fill(new Rgba32(0, 0, 0, 0), titleRect));

            await using var ms = new MemoryStream();
            await mask.SaveAsPngAsync(ms, cancellationToken);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> CreatePosterPromptVariables(
        string? projectTitle,
        string? originalTitle,
        string? tagline,
        string? synopsis)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = projectTitle?.Trim() ?? string.Empty,
            ["project_title"] = projectTitle?.Trim() ?? string.Empty,
            ["original_title"] = originalTitle?.Trim() ?? string.Empty,
            ["tagline"] = tagline?.Trim() ?? string.Empty,
            ["synopsis"] = synopsis?.Trim() ?? string.Empty
        };
    }

    private static string RenderPromptTemplate(string template, IReadOnlyDictionary<string, string> variables)
    {
        var rendered = template;
        foreach (var (key, value) in variables)
        {
            rendered = rendered.Replace("{" + key + "}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string SanitizePosterTitleForSafety(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "精彩短剧";
        }

        var sanitized = title.Trim();
        var replacements = new (string From, string To)[]
        {
            ("造反", "逆袭"),
            ("复仇", "逆袭"),
            ("报复", "逆袭"),
            ("顶娃", "萌娃"),
            ("救活", "回归"),
            ("离婚", "情变"),
            ("出轨", "情变"),
            ("豪门", "世家"),
            ("被顶娃", "被萌娃"),
            ("被救活", "被唤醒"),
            ("死", "逆"),
            ("杀", "战"),
            ("尸", "影"),
            ("血", "光"),
            ("虐", "燃"),
            ("葬", "缘")
        };

        foreach (var (from, to) in replacements)
        {
            sanitized = sanitized.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }

        sanitized = Regex.Replace(sanitized, "[\"'`~!@#$%^&*()+=\\[\\]{}|\\\\:;<>?,./，。！？；、】【（）“”‘’、]", string.Empty);
        sanitized = Regex.Replace(sanitized, "\\s+", string.Empty);

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "精彩短剧";
        }

        return sanitized.Length > 16 ? sanitized[..16] : sanitized;
    }

    private sealed class PosterSensitiveContentException : InvalidOperationException
    {
        public PosterSensitiveContentException(string message)
            : base(message)
        {
        }
    }

    private static string BuildApiUrl(string endpoint, string apiPath)
    {
        var baseUrl = endpoint.TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(apiPath) ? "/images/edits" : apiPath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return baseUrl + path;
    }

    private static string GetDefaultImageEditPath(string endpoint)
    {
        return endpoint.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
            ? "/images/generations"
            : "/images/edits";
    }

    private static float Clamp01(float value) => Clamp(value, 0f, 1f);

    private static float Clamp(float value, float min, float max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static Rgba32 ParseColor(string? hex, Rgba32 fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6)
        {
            return fallback;
        }

        if (byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return new Rgba32(r, g, b, 255);
        }

        return fallback;
    }

    private static string? ExtractJsonObject(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return value[start..(end + 1)];
    }

    private async Task<string> GeneratePosterNameAsync(
        ProjectInfo project,
        string? configFile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configFile))
        {
            throw new InvalidOperationException("启用 AI 海报名时必须提供 configFile。");
        }

        var config = KeyValueConfigReader.Read(configFile);
        var endpoint = GetRequired(config, "ChatModelEndpoint").TrimEnd('/');
        var modelId = GetRequired(config, "ChatModelId");
        var apiKey = GetRequired(config, "ChatModelApiKey");
        var promptVariables = CreatePosterPromptVariables(project.Title, project.OriginalTitle, project.Tagline, project.Synopsis);

        var payload = new
        {
            model = modelId,
            temperature = 0.9,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = RenderPromptTemplate(
                        GetOptional(config, "PosterNameSystemPrompt") ?? DefaultPosterNameSystemPrompt,
                        promptVariables)
                },
                new
                {
                    role = "user",
                    content = RenderPromptTemplate(
                        GetOptional(config, "PosterNameUserPrompt") ?? DefaultPosterNameUserPrompt,
                        promptVariables)
                }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"海报名接口请求失败: {(int)response.StatusCode} {response.ReasonPhrase}; body: {responseText}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("无法解析海报名接口响应。");

        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("海报名接口未返回内容。");
        }

        return SanitizeFileStem(ExtractPosterName(content));
    }

    private static string ExtractPosterName(string value)
    {
        var trimmed = value.Trim().Trim('\"', '\'', '“', '”');
        var firstLine = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? trimmed : firstLine.Trim();
    }

    private static string SanitizeFileStem(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new InvalidOperationException("AI 海报名为空或只包含非法文件名字符。");
        }

        return cleaned;
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> config, string key)
    {
        if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"配置缺少必填字段: {key}");
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> config, string key)
    {
        if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return null;
    }

    private sealed record ChatCompletionResponse(IReadOnlyList<Choice>? Choices);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content);
    private sealed record ImageGenerationResponse(IReadOnlyList<ImageGenerationItem>? Data);
    private sealed record ImageGenerationItem(
        [property: JsonPropertyName("b64_json")] string? B64Json,
        [property: JsonPropertyName("url")] string? Url);
    private sealed record PosterLayout(
        float X,
        float Y,
        float Width,
        float Height,
        float FontScale,
        Rgba32 TextColor,
        Rgba32 BackgroundColor,
        float BackgroundOpacity,
        HorizontalAlignment Align);
    private sealed record PosterLayoutResponse(
        float? X,
        float? Y,
        float? Width,
        float? Height,
        float? FontScale,
        string? TextColor,
        string? BackgroundColor,
        float? BackgroundOpacity,
        string? Align);
}
