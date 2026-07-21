using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Core.Services;
using ShortDrama.Infrastructure;
using ShortDrama.Infrastructure.Config;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer : IPosterRenamer
{
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif"];
    private static readonly TimeSpan PosterLayoutRequestTimeout = TimeSpan.FromSeconds(120);
    private const string DefaultPosterLayoutDetectPrompt = """
你是短剧海报版式分析助手。请识别海报上“所有现有剧名/标题相关文字”的整体最小外接矩形，并返回 JSON。
要求：
1. 只返回 JSON，不要解释。
2. 所有坐标和尺寸都用 0 到 1 的比例。
3. 标题相关文字包括主标题、副标题、季数标记（如“第三季”“第X季”），以及与剧名同属一组的宣传短句。
4. 凡是会随剧名替换而需要一并去掉的旧文字行，都要纳入同一个矩形；不要漏掉季数、副标题或同组短句。
5. 这个矩形要刚好覆盖全部标题文字行、尽量贴合，不要框进无关画面或空白区域。
6. 颜色返回十六进制，如 #F6E85A。
7. backgroundOpacity 取 0 到 1。若可以仅靠新标题覆盖旧标题，请返回 0。
8. 新标题风格默认使用短剧海报常见的大号黄色字体、黑色粗描边，位置优先在底部。

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

只分析当前海报里已经存在的标题文字组，不要自己设计新位置。
需要替换成的新剧名：{title}
""";
    private const string PosterLayoutJsonContract = """
    最终响应必须只包含一个 JSON 对象，且必须完整包含以下字段，禁止省略、禁止返回 null、禁止 Markdown：
    {"x":0.18,"y":0.73,"width":0.64,"height":0.12,"fontScale":0.08,"textColor":"#F6E85A","backgroundColor":"#1A1A1A","backgroundOpacity":0,"align":"center"}
    x、y、width、height、fontScale、backgroundOpacity 必须是 0 到 1 的 JSON 数字；width、height 必须大于 0，且 x+width、y+height 不得超出图片边界；align 只能是 left、center 或 right。
    上述示例数值仅说明格式，必须根据当前图片中的实际标题位置测量后填写，不能照抄示例。
    """;
    private const string DefaultPosterInpaintPrompt = """
这是海报文字清理与改标题任务。保持人物、脸部、服装、背景、构图、尺寸、比例和光影不变。
删除原图中除目标新剧名外的全部文字和小字，包括人物名、演员名、作者、改编来源、宣传语、季数、字幕、水印、Logo文字和角标，并用背景自然补全。
最后只添加一次目标新剧名“{title}”。最终成品中唯一允许出现的可读文字就是这个目标剧名，不得留下其他文字或文字残影。
目标剧名必须使用标准、清晰、易识别的简体中文印刷粗体，逐字准确。
""";
    private const string DefaultPosterInpaintSafeRetryPrompt = """
这是安全合规的海报文字清理任务。人物、背景、服装、道具、光影和构图保持不变。
清除旧标题以及人物名、演员名、作者、改编来源、宣传语、季数、字幕、水印、Logo文字和角标等全部其他文字。
最后只写一次“{title}”，不得出现任何其他可读文字。标题使用普通、标准、清晰的简体中文印刷粗体。
""";
    private const string DefaultPosterGenerationPrompt = """
参考输入海报执行精确的文字清理和新标题生成。保持人物、脸部、服装、背景、构图、尺寸、比例、颜色、光影和清晰度不变。
删除原图中的全部可见文字，包括旧标题、副标题、人物或角色姓名、演员名、作者、改编及来源说明、宣传语、季数、字幕、水印、Logo文字和角标，并用背景自然补全。
清理后只添加一次新剧名“{title}”。最终成品唯一允许出现的可读文字就是这个新剧名，不得保留或新增其他文字。
新剧名必须使用标准、清晰、逐字准确的简体中文印刷粗体，位置沿用原主标题区域。
""";
    private const string PosterTextGuardrails = """
标题文字必须满足以下硬性要求：
1. 只能使用标准简体中文。
2. 不允许繁体字、异体字、错别字。
3. 优先使用普通、清晰、审核友好的中文标题字，接近黑体、微软雅黑或常见无衬线粗体更好，但不是硬性要求完全一致。
4. 不允许手写体、书法体、草书、篆书、花体字、艺术字、空心字、立体字、金属字、火焰字、变形字。
5. 不允许夸张描边、夸张装饰、纹理字、裂纹字、毛笔飞白、故意残缺笔画。
6. 文字应当清晰、规整、易识别，不要出现明显电影特效字或过度设计字。
7. 可以有轻微描边，但必须克制、整齐、易读，不能为了设计感牺牲识别度。
8. 优先保证标题清晰、端正、易识别，即使风格略普通也可以。
9. 如果模型无法稳定复现海报风格，请退回到最普通、最清晰、最易识别的中文粗体字。
10. 最终标题必须与目标标题“{title}”逐字一致，不能增删改任何一个字。
11. 禁止使用相似字冒充目标字，禁止缺笔、粘连、断裂、夸张变形、装饰笔画。
12. 如果“海报风格”和“清晰易读、审核友好”冲突，必须优先选择清晰易读、审核友好的写法。
13. 高风险字如“继、媳、鬓、馨、骤、瓷、赢、寡、赘”等，必须使用常见、标准、易识别的简体印刷字写法。
14. 不能为了保持原海报字体风格而牺牲某个字的标准写法。
""";
    private const string DefaultPosterGenerationSafeRetryPrompt = """
参考输入海报执行安全合规的文字清理。保持人物、背景、服装、动作、表情、光影、构图和清晰度不变。
删除原图中除目标剧名外的全部文字和小字，包括人物名、演员名、作者、改编来源、宣传语、季数、字幕、水印、Logo文字和角标。
最后只写一次“{title}”，不得出现任何其他可读文字。标题使用普通、标准、清晰的简体中文印刷粗体。
""";
    private const string PosterFinalTextPolicy = """
最终文字规则（最高优先级）：
1. 成品中唯一允许出现的可读文字是目标新剧名“{title}”，且只能出现一次。
2. 必须删除其他所有中文、英文、拼音、字母、数字和文字残影，包括人物或角色姓名、演员名、作者、改编或来源说明、版权或出品信息、宣传语、副标题、季数、字幕、水印、Logo文字和角标。
3. 文字删除区域必须用周围背景自然补全；不得因此改变人物、脸部、服装、道具、背景、构图、尺寸、比例、颜色、光影和清晰度。
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

        var project = await _projectInfoParser.ParsePosterAsync(request.ProjectDir, cancellationToken);
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

        if (string.IsNullOrWhiteSpace(request.ConfigFile) || !File.Exists(request.ConfigFile))
        {
            throw new InvalidOperationException("AI 海报图片生成必须提供有效的 configFile。");
        }

        var config = KeyValueConfigReader.Read(request.ConfigFile);
        var posterMode = NormalizePosterMode(GetOptional(config, "PosterMode"));
        switch (posterMode)
        {
            case "video_frame":
                await GenerateCoverFromPosterAsync(
                    request, inputPath, outputPath, posterName, request.ConfigFile, cancellationToken);
                break;
            case "poster_ai_edit":
                try
                {
                    await GenerateCoverFromPosterAsync(
                        request, inputPath, outputPath, posterName, request.ConfigFile, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log(request, $"封面单次AI编辑失败，回退到原始海报模式：{ex.Message}");
                    await RenderPosterAsync(inputPath, outputPath, posterName, request.ConfigFile, request, cancellationToken);
                }
                break;
            case "poster_ai_erase_pil_title":
                await GenerateErasePilTitlePosterAsync(
                    request, inputPath, outputPath, posterName, request.ConfigFile, cancellationToken);
                break;
            default:
                await RenderPosterAsync(inputPath, outputPath, posterName, request.ConfigFile, request, cancellationToken);
                break;
        }

        _logger.LogInformation("Updated poster: {Input} -> {Output}", inputPath, outputPath);
        return new PosterRenameResult(inputPath, outputPath, posterName);
    }

    private async Task RenderPosterAsync(
        string inputPath,
        string outputPath,
        string posterName,
        string? configFile,
        PosterRenameRequest request,
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
            await TryGeneratePosterWithVerificationAsync(
                configFile,
                renderInputPath,
                outputPath,
                posterName,
                layout,
                request,
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
        if (await CanIdentifyImageAsync(inputPath, cancellationToken))
            return inputPath;

        var tempPngPath = Path.Combine(
            Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(inputPath)}.{Guid.NewGuid():N}.png");
        await ConvertImageToPngWithFfmpegAsync(inputPath, tempPngPath, cancellationToken);
        return tempPngPath;
    }

    private static async Task<bool> CanIdentifyImageAsync(string inputPath, CancellationToken cancellationToken)
    {
        try
        {
            var info = await Image.IdentifyAsync(inputPath, cancellationToken);
            return info is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task ConvertImageToPngWithFfmpegAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpegBinary();
        var result = await _processRunner.RunAsync(
            ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, outputPath],
            Path.GetDirectoryName(inputPath),
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException(
                $"海报图片格式转换失败: {Path.GetFileName(inputPath)}（{result.StandardError.Trim()}）");
        }
    }

    private static string ResolveFfmpegBinary()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fullPath = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (File.Exists(bundled))
            return bundled;

        var packaged = BundledToolResolver.TryResolveBinary("ffmpeg");
        if (packaged is not null)
            return packaged;

        throw new InvalidOperationException("海报图片格式转换失败: 未找到可用的 ffmpeg，无法转换 HEIC/HEIF 图片");
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
        var mediaType = GuessMediaType(extension);

        var configuredPromptTemplate = GetOptional(config, "PosterLayoutDetectPrompt")
            ?? DefaultPosterLayoutDetectPrompt;
        var promptVariables = CreatePosterPromptVariables(title, title, null, null);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var promptTemplate = attempt == 0
                ? configuredPromptTemplate
                : DefaultPosterLayoutDetectPrompt;
            var prompt = $"{RenderPromptTemplate(promptTemplate, promptVariables).Trim()}\n\n{PosterLayoutJsonContract.Trim()}";
            try
            {
                var aiLayout = await RequestPosterLayoutAsync(
                    endpoint,
                    modelId,
                    apiKey,
                    imageBase64,
                    mediaType,
                    prompt,
                    cancellationToken).ConfigureAwait(false);
                return CreateValidatedPosterLayout(aiLayout);
            }
            catch (PosterLayoutResponseException ex) when (attempt == 0)
            {
                _logger.LogWarning(
                    ex,
                    "AI海报布局响应缺失或不合理，使用内置完整布局提示重试。 image={Image}",
                    imagePath);
            }
        }

        throw new PosterLayoutResponseException("AI 海报布局检测重试后仍未返回有效布局。");
    }

    private async Task<PosterLayoutResponse> RequestPosterLayoutAsync(
        string endpoint,
        string modelId,
        string apiKey,
        string imageBase64,
        string mediaType,
        string prompt,
        CancellationToken cancellationToken)
    {
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
        timeoutCts.CancelAfter(PosterLayoutRequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"AI 海报布局检测接口请求超过 {PosterLayoutRequestTimeout.TotalSeconds:0} 秒，" +
                "请检查 AI 文本模型 Endpoint、API Key、模型可用性或网络连接。",
                ex);
        }

        using (response)
        {
            string responseText;
            try
            {
                responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"AI 海报布局检测接口响应读取超过 {PosterLayoutRequestTimeout.TotalSeconds:0} 秒。",
                    ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    AiApiErrorMessage.Create("AI 海报布局检测接口", response.StatusCode, response.ReasonPhrase, responseText));
            }

            var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText, JsonOptions);
            var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new PosterLayoutResponseException("AI 海报布局检测未返回内容。");
            }

            var json = ExtractJsonObject(content);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new PosterLayoutResponseException($"AI 海报布局检测未返回合法 JSON: {content}");
            }

            try
            {
                return JsonSerializer.Deserialize<PosterLayoutResponse>(json, JsonOptions)
                    ?? throw new PosterLayoutResponseException("AI 海报布局检测返回的 JSON 无法解析。");
            }
            catch (JsonException ex)
            {
                throw new PosterLayoutResponseException($"AI 海报布局检测返回的 JSON 无法解析：{ex.Message}");
            }
        }
    }

    private static PosterLayout CreateValidatedPosterLayout(PosterLayoutResponse aiLayout)
    {
        if (!PosterLayoutDetectionPolicy.TryValidateCoordinates(
                aiLayout.X,
                aiLayout.Y,
                aiLayout.Width,
                aiLayout.Height,
                out var invalidReason))
        {
            throw new PosterLayoutResponseException($"AI 海报布局坐标无效：{invalidReason}");
        }

        var x = aiLayout.X!.Value;
        var y = aiLayout.Y!.Value;
        var width = aiLayout.Width!.Value;
        var height = aiLayout.Height!.Value;
        var fontScale = aiLayout.FontScale is { } rawFontScale && float.IsFinite(rawFontScale)
            ? Clamp(rawFontScale, 0.05f, 0.14f)
            : 0.08f;
        var backgroundOpacity = aiLayout.BackgroundOpacity is { } rawOpacity && float.IsFinite(rawOpacity)
            ? Clamp01(rawOpacity)
            : 0f;
        return new PosterLayout(
            x,
            y,
            width,
            height,
            fontScale,
            ParseColor(aiLayout.TextColor, new Rgba32(246, 232, 90, 255)),
            ParseColor(aiLayout.BackgroundColor, new Rgba32(26, 26, 26, 255)),
            backgroundOpacity,
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
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var sourceInfo = await Image.IdentifyAsync(inputPath, cancellationToken)
            ?? throw new InvalidOperationException($"无法读取原海报尺寸信息: {inputPath}");
        var imageSize = provider == "doubao"
            ? PosterCoverFrameSizeHelper.ResolveFrameApiSize(sourceInfo.Width, sourceInfo.Height, config)
            : NormalizeImageSize(GetOptional(config, "ImageSize"), provider);
        var imageQuality = NormalizeImageQuality(GetOptional(config, "ImageQuality"));
        var extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        var mediaType = GuessMediaType(extension);

        var useGenerationApi = apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase);
        var promptVariables = CreatePosterPromptVariables(title, title, null, null);
        var primaryPrompt = MergePromptWithGuardrails(
            useGenerationApi
                ? RenderPromptTemplate(GetOptional(config, "PosterGenerationPrompt") ?? DefaultPosterGenerationPrompt, promptVariables)
                : RenderPromptTemplate(GetOptional(config, "PosterInpaintPrompt") ?? DefaultPosterInpaintPrompt, promptVariables),
            title);
        var safeRetryPrompt = MergePromptWithGuardrails(
            useGenerationApi
                ? RenderPromptTemplate(GetOptional(config, "PosterGenerationSafeRetryPrompt") ?? DefaultPosterGenerationSafeRetryPrompt, promptVariables)
                : RenderPromptTemplate(GetOptional(config, "PosterInpaintSafeRetryPrompt") ?? DefaultPosterInpaintSafeRetryPrompt, promptVariables),
            title);

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
                provider,
                imageQuality,
                imageSize,
                cancellationToken,
                editFullImage: true);
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
                    provider,
                    imageQuality,
                    imageSize,
                    cancellationToken,
                    editFullImage: true);
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
                var sanitizedSafeRetryPrompt = MergePromptWithGuardrails(useGenerationApi
                    ? RenderPromptTemplate(GetOptional(config, "PosterGenerationSafeRetryPrompt") ?? DefaultPosterGenerationSafeRetryPrompt, sanitizedPromptVariables)
                    : RenderPromptTemplate(GetOptional(config, "PosterInpaintSafeRetryPrompt") ?? DefaultPosterInpaintSafeRetryPrompt, sanitizedPromptVariables),
                    sanitizedTitle);
                bytes = await GeneratePosterWithAiPromptAsync(
                    requestUrl,
                    apiPath,
                    modelId,
                    apiKey,
                    inputPath,
                    mediaType,
                    layout,
                    sanitizedSafeRetryPrompt,
                    provider,
                    imageQuality,
                    imageSize,
                    cancellationToken,
                    editFullImage: true);
            }
        }

        await using var buffer = new MemoryStream(bytes);
        using var generated = await Image.LoadAsync<Rgba32>(buffer, cancellationToken);
        if (generated.Width != sourceInfo.Width || generated.Height != sourceInfo.Height)
            ResizeToCanvasPreservingAspect(generated, sourceInfo.Width, sourceInfo.Height);

        await generated.SaveAsync(outputPath, cancellationToken);
    }

    private static void ResizeToCanvasPreservingAspect(Image<Rgba32> image, int targetWidth, int targetHeight)
    {
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(1, targetWidth), Math.Max(1, targetHeight)),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
        }));
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
        string provider,
        string imageQuality,
        string imageSize,
        CancellationToken cancellationToken,
        bool editFullImage = false)
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
                provider,
                imageQuality,
                imageSize,
                cancellationToken);
        }

        if (editFullImage)
        {
            return await GeneratePosterWithEditFormNoMaskAsync(
                requestUrl,
                modelId,
                apiKey,
                inputPath,
                mediaType,
                prompt,
                provider,
                imageQuality,
                imageSize,
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
            provider,
            imageQuality,
            imageSize,
            cancellationToken);
    }

    private async Task<byte[]> GeneratePosterWithGenerationsJsonAsync(
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
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(inputPath, cancellationToken));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["prompt"] = prompt,
            ["image"] = $"data:{mediaType};base64,{imageBase64}",
        };
        if (!string.IsNullOrWhiteSpace(imageSize))
            payload["size"] = imageSize;
        if (IsOpenAiImageProvider(provider))
        {
            if (!string.IsNullOrWhiteSpace(imageQuality))
                payload["quality"] = imageQuality;
        }
        else
        {
            payload["response_format"] = "b64_json";
            payload["watermark"] = false;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            ThrowPosterApiFailure("AI 海报图片生成", requestUrl, response, responseText);
        }

        return await ReadImageResponseBytesAsync(responseText, timeoutCts.Token)
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
        string provider,
        string imageQuality,
        string imageSize,
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

        using var maskStream = new MemoryStream(maskBytes);
        var maskContent = new StreamContent(maskStream);
        maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(maskContent, "mask", "mask.png");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            ThrowPosterApiFailure("AI 海报图片编辑", requestUrl, response, responseText);
        }

        return await ReadImageResponseBytesAsync(responseText, timeoutCts.Token)
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

        throw new InvalidOperationException(AiApiErrorMessage.Create(
            operation,
            response.StatusCode,
            response.ReasonPhrase,
            responseText,
            "请检查 config.json 中的 ImageEditEndpoint / ImageEditPath / ImageEditModelId 配置。"));
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

            // Compute title rect with padding aligned to Python poster_generation_service.create_title_mask.
            var rx = Math.Max(0, (int)Math.Round(w * layout.X));
            var ry = Math.Max(0, (int)Math.Round(h * layout.Y));
            var rw = Math.Min(w - rx, (int)Math.Round(w * layout.Width));
            var rh = Math.Min(h - ry, (int)Math.Round(h * layout.Height));
            var padX = Math.Max(18, (int)Math.Round(rw * 0.08f));
            var padY = Math.Max(18, (int)Math.Round(rh * 0.45f));
            var left = Math.Max(0, rx - padX);
            var top = Math.Max(0, ry - padY);
            var right = Math.Min(w, rx + rw + padX);
            var bottom = Math.Min(h, ry + rh + padY);
            var titleRect = new Rectangle(left, top, right - left, bottom - top);
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

    private static string MergePromptWithGuardrails(string prompt, string title)
    {
        var variables = CreatePosterPromptVariables(title, title, null, null);
        return $"{prompt.Trim()}\n\n{RenderPromptTemplate(PosterFinalTextPolicy, variables)}\n\n{RenderPromptTemplate(PosterTextGuardrails, variables)}".Trim();
    }

    private static string GuessMediaType(string extension) => extension switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        "heic" => "image/heic",
        "heif" => "image/heif",
        _ => "image/png",
    };

    private static string NormalizeImageProvider(string? value)
    {
        var provider = (value ?? "doubao").Trim().ToLowerInvariant();
        return provider switch
        {
            "openai_image2" => "ofox_image2",
            "gemini" => "doubao",
            "ofox_image2" => "ofox_image2",
            _ => "doubao",
        };
    }

    private static bool IsOpenAiImageProvider(string provider) =>
        NormalizeImageProvider(provider) == "ofox_image2";

    private static string NormalizeImageQuality(string? value)
    {
        var quality = (value ?? "").Trim().ToLowerInvariant();
        return quality is "low" or "medium" or "high" or "auto" ? quality : "";
    }

    private static string NormalizeImageSize(string? value, string provider)
    {
        var size = (value ?? "").Trim();
        if (IsOpenAiImageProvider(provider))
            return string.IsNullOrWhiteSpace(size) ? "auto" : size.ToLowerInvariant();

        var normalized = size.ToUpperInvariant();
        return normalized is "2K" or "4K" ? normalized : size;
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
        PosterProjectInfo project,
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
            throw new InvalidOperationException(
                AiApiErrorMessage.Create("AI 海报名接口", response.StatusCode, response.ReasonPhrase, responseText));
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

    private sealed class PosterLayoutResponseException : InvalidOperationException
    {
        public PosterLayoutResponseException(string message)
            : base(message)
        {
        }
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

internal static class PosterLayoutDetectionPolicy
{
    internal static bool TryValidateCoordinates(
        float? x,
        float? y,
        float? width,
        float? height,
        out string reason)
    {
        if (x is null || y is null || width is null || height is null)
        {
            reason = "x、y、width、height 必须全部存在且不能为 null";
            return false;
        }

        if (!float.IsFinite(x.Value) ||
            !float.IsFinite(y.Value) ||
            !float.IsFinite(width.Value) ||
            !float.IsFinite(height.Value))
        {
            reason = "坐标和尺寸必须是有限数字";
            return false;
        }

        if (x.Value < 0 || x.Value >= 1 || y.Value < 0 || y.Value >= 1)
        {
            reason = "x、y 必须位于 [0, 1) 范围内";
            return false;
        }

        if (width.Value < 0.01f || width.Value > 1 || height.Value < 0.01f || height.Value > 1)
        {
            reason = "width、height 必须位于 [0.01, 1] 范围内";
            return false;
        }

        const float edgeTolerance = 0.001f;
        if (x.Value + width.Value > 1 + edgeTolerance ||
            y.Value + height.Value > 1 + edgeTolerance)
        {
            reason = "标题矩形超出图片边界";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
