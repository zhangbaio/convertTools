using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 生成与人工制作项目一致的原始素材包。角色定妆图必须来自已配置的图片模型，
/// 不能以视频抽帧或程序色块替代。
/// </summary>
public static partial class TikTokReferenceSourcePackageService
{
    public const string DirectoryName = "参考格式原始素材包";
    public const string CharacterDirectoryName = "角色";
    public const string VideoDirectoryName = "videos";
    public const string MaterialDirectoryName = "素材文件";
    public const string CharacterWorkbenchFileName = "角色矢量图.png";
    public const string SceneDesignFileName1 = "场景设计图1.png";
    public const string SceneDesignFileName2 = "场景设计图2.png";
    public const string StateFileName = ".reference-source-package.json";
    public const string Version = "v1-image-model-characters";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public static string GetRoot(string workflowProjectDirectory) =>
        Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            DirectoryName);

    private static string GetStatePath(string workflowProjectDirectory) =>
        Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            StateFileName);

    public static bool HasCurrentOutput(string workflowProjectDirectory)
    {
        var root = GetRoot(workflowProjectDirectory);
        var state = GetStatePath(workflowProjectDirectory);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        return File.Exists(state) &&
               File.Exists(Path.Combine(root, CharacterWorkbenchFileName)) &&
               File.Exists(Path.Combine(root, SceneDesignFileName1)) &&
               File.Exists(Path.Combine(root, SceneDesignFileName2)) &&
               Directory.Exists(characterDir) &&
               Directory.EnumerateFiles(characterDir).Count(IsImage) >= 3;
    }

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var root = GetRoot(context.WorkflowProjectDir);
        var title = FirstNonEmpty(item.NewTitle, item.Title, item.OriginalTitle, Path.GetFileName(context.SourceProjectDir));
        var originalTitle = FirstNonEmpty(item.OriginalTitle, item.DisplayName, title);
        var intro = ResolveIntro(item, context);
        var script = ReadProjectScript(context, title, intro);
        var sourceFingerprint = ComputeSourceFingerprint(title, intro, script, settings);
        if (!forceRerun && HasCurrentOutput(context.WorkflowProjectDir) &&
            HasMatchingFingerprint(context.WorkflowProjectDir, sourceFingerprint))
        {
            log?.Invoke($"参考格式原始素材包已存在，复用：{root}");
            return root;
        }

        EnsureImageModelConfigured(settings);
        TryDeleteDirectory(root);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        var videoDir = Path.Combine(root, VideoDirectoryName);
        var materialDir = Path.Combine(root, MaterialDirectoryName, "001");
        Directory.CreateDirectory(characterDir);
        Directory.CreateDirectory(videoDir);
        Directory.CreateDirectory(materialDir);

        var characters = ExtractCharacterProfiles(script, intro).Take(6).ToArray();
        if (characters.Length < 3 || characters.All(character => IsGenericCharacterName(character.Name)))
        {
            if (characters.All(character => IsGenericCharacterName(character.Name)))
                characters = [];
            characters = AddFallbackCharacters(characters, intro).Take(3).ToArray();
        }

        log?.Invoke($"参考格式素材包：识别 {characters.Length} 个主要角色，开始调用图片模型生成真人定妆图。");
        var generatedCharacters = new List<GeneratedCharacter>(characters.Length);
        foreach (var (character, index) in characters.Select((value, index) => (value, index)))
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(characterDir, $"{SanitizeFileName(character.Name)}.png");
            log?.Invoke($"角色图片 {index + 1}/{characters.Length}：{character.Name}（图片模型）");
            var bytes = await GenerateImageWithRetryAsync(
                BuildCharacterPrompt(character), settings, character.Name, ct).ConfigureAwait(false);
            await SaveNormalizedPngAsync(bytes, output, 768, 1024, ct).ConfigureAwait(false);
            generatedCharacters.Add(new GeneratedCharacter(character, output));
        }

        var videos = ProjectVideoResolver.ResolveSourceVideos(context.SourceProjectDir).ToArray();
        LinkVideos(videos, videoDir, materialDir, title, ct);
        await WriteProjectFilesAsync(
            root, context, title, originalTitle, intro, script, item, videos.Length, ct).ConfigureAwait(false);

        await RefreshDerivedImagesAsync(context.WorkflowProjectDir, log, ct).ConfigureAwait(false);

        var statePath = GetStatePath(context.WorkflowProjectDir);
        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(new
            {
                version = Version,
                sourceFingerprint,
                title,
                imageProvider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider),
                imageModel = ResolveModelId(settings),
                generatedAt = DateTimeOffset.Now,
                characters = generatedCharacters.Select(x => new
                {
                    x.Profile.Name,
                    x.Profile.Description,
                    file = Path.GetFileName(x.Path),
                    source = "image-model",
                }),
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);
        TrySetHidden(statePath);

        log?.Invoke($"参考格式原始素材包生成完成：{root}");
        return root;
    }

    public static async Task RefreshDerivedImagesAsync(
        string workflowProjectDirectory,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(workflowProjectDirectory);
        var root = GetRoot(context.WorkflowProjectDir);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        if (!Directory.Exists(characterDir))
            throw new DirectoryNotFoundException($"缺少角色图片目录：{characterDir}");
        var characters = Directory.EnumerateFiles(characterDir)
            .Where(IsImage)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new GeneratedCharacter(
                new CharacterProfile(Path.GetFileNameWithoutExtension(path), "图片模型生成角色"),
                path))
            .ToArray();
        if (characters.Length == 0)
            throw new InvalidOperationException("角色图片目录为空，无法生成角色工作台。");

        var sceneSources = FindSceneSources(context, root).Take(8).ToList();
        if (sceneSources.Count < 4)
        {
            var videos = ProjectVideoResolver.ResolveSourceVideos(context.SourceProjectDir).ToArray();
            sceneSources = (await ExtractSceneFramesAsync(root, videos, log, ct).ConfigureAwait(false)).ToList();
        }

        RenderCharacterWorkbench(
            Path.Combine(root, CharacterWorkbenchFileName), characters, sceneSources);
        RenderSceneDesignSheet(
            Path.Combine(root, SceneDesignFileName1),
            Path.GetFileName(context.WorkflowProjectDir).TrimStart('_'),
            "主要场景设计参考",
            sceneSources.Take(4).ToArray());
        RenderSceneDesignSheet(
            Path.Combine(root, SceneDesignFileName2),
            Path.GetFileName(context.WorkflowProjectDir).TrimStart('_'),
            "补充场景与光线参考",
            sceneSources.Skip(4).Take(4).ToArray());
        TrySetHidden(GetStatePath(context.WorkflowProjectDir));
        log?.Invoke($"参考格式素材包：已用 {sceneSources.Count} 张真实场景帧刷新角色工作台和场景设计图。");
    }

    internal static IReadOnlyList<CharacterProfile> ExtractCharacterProfiles(string script, string intro = "")
    {
        var profiles = new Dictionary<string, CharacterProfile>(StringComparer.Ordinal);
        foreach (var raw in Regex.Split(script ?? string.Empty, "\\r?\\n"))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', '△');
            if (line.Length is < 4 or > 500) continue;
            var match = CharacterDefinitionRegex().Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (IsNonCharacterName(name)) continue;
            profiles.TryAdd(name, new CharacterProfile(name, line));
        }

        if (profiles.Count < 3)
        {
            foreach (var raw in Regex.Split(script ?? string.Empty, "\\r?\\n"))
            {
                var match = DialogueRegex().Match(raw.Trim());
                if (!match.Success) continue;
                var name = match.Groups["name"].Value.Trim();
                if (IsNonCharacterName(name)) continue;
                profiles.TryAdd(name, new CharacterProfile(name, $"主要短剧角色。剧情参考：{intro}"));
                if (profiles.Count >= 6) break;
            }
        }

        return profiles.Values.ToArray();
    }

    internal static string BuildCharacterPrompt(CharacterProfile profile) =>
        "Use case: photorealistic-natural\n" +
        "Asset type: 中国短剧角色真人定妆参考图\n" +
        $"Subject: {profile.Name}。{profile.Description}\n" +
        "Style/medium: 真实真人影视剧演员定妆摄影，电影级写实照片，不是插画，不是动漫，不是3D\n" +
        "Composition/framing: 竖版3:4，单人，正面全身或四分之三全身，人物居中，完整头部和手脚\n" +
        "Scene/backdrop: 干净的浅灰色摄影棚无缝背景\n" +
        "Lighting/mood: 柔和专业棚拍光线，自然真实皮肤、头发和服装纹理\n" +
        "Constraints: 虚构中国成年人；严格遵循角色年龄、身份、气质和服装；画面中仅一人；无文字、无Logo、无水印\n" +
        "Avoid: 现实明星或公众人物脸、儿童、卡通、动漫、插画、塑料皮肤、过度磨皮、多余手指、多人、拼贴、字幕";

    internal static CharacterProfile[] AddFallbackCharacters(
        IReadOnlyList<CharacterProfile> existing,
        string intro)
    {
        var result = existing.ToList();
        foreach (Match match in IntroCharacterListRegex().Matches(intro ?? string.Empty))
        {
            var listedNames = match.Groups["names"].Value
                .Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in listedNames.Where(name => !IsNonCharacterName(name)))
            {
                if (result.Any(item => item.Name == name)) continue;
                result.Add(new CharacterProfile(
                    name,
                    $"剧情简介中明确出现的主要角色。时代、身份、服装与气质必须符合以下剧情：{intro}"));
                if (result.Count >= 6) return result.ToArray();
            }
        }

        var names = new[] { "女主", "男主", "主要配角" };
        foreach (var name in names)
        {
            if (result.Any(item => item.Name == name)) continue;
            result.Add(new CharacterProfile(name, $"现代都市中国短剧主要角色，根据剧情简介塑造：{intro}"));
            if (result.Count >= 3) break;
        }
        return result.ToArray();
    }

    private static async Task<byte[]> GenerateImageWithRetryAsync(
        string prompt,
        ClientSettings settings,
        string roleName,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await GenerateImageAsync(prompt, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"角色“{roleName}”图片模型生成失败：{last?.Message}", last);
    }

    private static async Task<byte[]> GenerateImageAsync(
        string prompt,
        ClientSettings settings,
        CancellationToken ct)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        var endpoint = provider == "ofox_image2"
            ? FirstNonEmpty(settings.OfoxImage2Endpoint, ClientSettingsDefaults.OfoxImage2Endpoint)
            : FirstNonEmpty(settings.ImageModelEndpoint, ClientSettingsDefaults.ImageModelEndpoint);
        var model = ResolveModelId(settings);
        var apiKey = provider == "ofox_image2" ? settings.OfoxImage2ApiKey : settings.ImageModelApiKey;
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = provider == "ofox_image2"
                ? NormalizeOfoxPortraitSize(settings.OfoxImage2Size)
                : PosterImageConfigHelper.DoubaoImageSizeForRatio(settings.DoubaoImageResolution, "3:4"),
        };
        if (provider == "ofox_image2")
            payload["quality"] = FirstNonEmpty(settings.OfoxImage2Quality, "medium");
        else
        {
            payload["response_format"] = "b64_json";
            payload["watermark"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"角色真人定妆图生成失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；" +
                $"请检查系统设置中的图片模型、Endpoint 和 API Key。响应：{Truncate(body, 1200)}");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("图片模型成功返回，但响应中没有 data 图片数据。");
        var first = data[0];
        if (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
            return Convert.FromBase64String(b64.GetString()!);
        if (first.TryGetProperty("url", out var imageUrl) && Uri.TryCreate(imageUrl.GetString(), UriKind.Absolute, out var uri))
            return await Http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        throw new InvalidOperationException("图片模型响应中没有可解析的 b64_json 或 url。");
    }

    private static async Task SaveNormalizedPngAsync(
        byte[] bytes,
        string output,
        int width,
        int height,
        CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(bytes);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
        }));
        await image.SaveAsPngAsync(output, ct).ConfigureAwait(false);
    }

    private static void RenderCharacterWorkbench(
        string output,
        IReadOnlyList<GeneratedCharacter> characters,
        IReadOnlyList<string> sourceFrames)
    {
        using var canvas = new Image<Rgba32>(2342, 1280, Color.ParseHex("0e0f11"));
        var family = ResolveFont();
        var titleFont = family.CreateFont(24, FontStyle.Bold);
        var labelFont = family.CreateFont(15, FontStyle.Regular);
        canvas.Mutate(ctx => ctx.DrawText("角色设计工作台", titleFont, Color.White, new PointF(34, 24)));

        var columns = Math.Min(3, Math.Max(1, characters.Count));
        for (var index = 0; index < characters.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var x = 120 + column * 735;
            var y = 105 + row * 550;
            DrawNode(canvas, characters[index].Path, x, y, 225, 355);
            canvas.Mutate(ctx =>
            {
                ctx.DrawText(characters[index].Profile.Name, titleFont, Color.White, new PointF(x, y + 370));
                ctx.DrawLine(Color.ParseHex("34383f"), 2, new PointF(x + 225, y + 177), new PointF(x + 330, y + 177));
            });
            var reference = sourceFrames.Count == 0 ? null : sourceFrames[index % sourceFrames.Count];
            if (reference is not null)
                DrawNode(canvas, reference, x + 330, y + 55, 170, 300);
            else
                canvas.Mutate(ctx => ctx.DrawText("暂无成片参考", labelFont, Color.ParseHex("79808b"), new PointF(x + 340, y + 175)));
        }
        canvas.SaveAsPng(output);
    }

    private static void DrawNode(Image<Rgba32> canvas, string path, int x, int y, int width, int height)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));
            canvas.Mutate(ctx =>
            {
                ctx.Fill(Color.ParseHex("24272c"), new RectangleF(x - 5, y - 5, width + 10, height + 10));
                ctx.DrawImage(image, new Point(x, y), 1f);
            });
        }
        catch
        {
            canvas.Mutate(ctx => ctx.Fill(Color.ParseHex("24272c"), new RectangleF(x, y, width, height)));
        }
    }

    private static void RenderSceneDesignSheet(
        string output,
        string title,
        string heading,
        IReadOnlyList<string> sources)
    {
        using var canvas = new Image<Rgba32>(2435, 1254, Color.White);
        var family = ResolveFont();
        var titleFont = family.CreateFont(25, FontStyle.Bold);
        var bodyFont = family.CreateFont(18);
        canvas.Mutate(ctx =>
        {
            ctx.DrawText(title, titleFont, Color.ParseHex("16181c"), new PointF(45, 30));
            ctx.DrawText(heading, bodyFont, Color.ParseHex("343941"), new PointF(45, 72));
        });
        var usable = sources.Where(File.Exists).Take(4).ToArray();
        if (usable.Length == 0)
        {
            canvas.Mutate(ctx => ctx.DrawText(
                "当前项目没有可用的真实场景参考图；角色图片仍由图片模型生成。",
                bodyFont, Color.ParseHex("69717d"), new PointF(70, 180)));
            canvas.SaveAsPng(output);
            return;
        }

        for (var index = 0; index < usable.Length; index++)
            DrawNode(canvas, usable[index], 45, 125 + index * 260, 500, 230);
        DrawNode(canvas, usable[0], 670, 125, 1710, 960);
        canvas.Mutate(ctx => ctx.DrawText(
            "场景参考来自项目真实画面，用于角色、光线与空间一致性设计。",
            bodyFont, Color.ParseHex("343941"), new PointF(670, 1120)));
        canvas.SaveAsPng(output);
    }

    private static async Task WriteProjectFilesAsync(
        string root,
        ProjectWorkspaceContext context,
        string title,
        string originalTitle,
        string intro,
        string script,
        QueueProjectItem item,
        int videoCount,
        CancellationToken ct)
    {
        var infoDir = Path.Combine(root, SanitizeFileName(title));
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "短剧信息.txt"),
            $"原剧名：{originalTitle}\n新剧名：{title}\n集数：{Math.Max(item.EpisodeCount, videoCount)}\n简介：{intro}\n",
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "简介.txt"), intro + Environment.NewLine,
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "详细简介.txt"),
            $"剧名：{title}\n\n作者：制作方\n\n类型：{item.GenreCategory}\n\n集数：{Math.Max(item.EpisodeCount, videoCount)}\n\n简介：{intro}\n\n发布时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n",
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, $"{SanitizeFileName(title)} 剧本.txt"), script,
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        var metadata = ReadMetadataObject(context);
        metadata["projectKey"] = title;
        metadata["sourceName"] = originalTitle;
        metadata["displayName"] = title;
        metadata["title"] = title;
        metadata["originalTitle"] = originalTitle;
        metadata["intro"] = intro;
        metadata["episodeCount"] = Math.Max(item.EpisodeCount, videoCount);
        metadata["workflowProjectDir"] = context.WorkflowProjectDir;
        metadata["sourceProjectDir"] = context.SourceProjectDir;
        await File.WriteAllTextAsync(Path.Combine(infoDir, "shortdrama-project.json"),
            metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        var poster = FindPoster(context);
        if (poster is not null)
        {
            using var image = Image.Load<Rgba32>(poster);
            await image.SaveAsJpegAsync(Path.Combine(infoDir, "海报图片.jpg"), ct).ConfigureAwait(false);
            await image.SaveAsJpegAsync(Path.Combine(infoDir, $"{SanitizeFileName(title)}.jpg"), ct).ConfigureAwait(false);
        }
    }

    private static void LinkVideos(
        IReadOnlyList<string> videos,
        string videoDir,
        string materialDir,
        string title,
        CancellationToken ct)
    {
        for (var index = 0; index < videos.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(videos[index]);
            LinkOrCopy(videos[index], Path.Combine(videoDir, $"{SanitizeFileName(title)}-第{index + 1}集{extension}"));
            if (index < 40)
                LinkOrCopy(videos[index], Path.Combine(materialDir, $"001-{index + 1}{extension}"));
        }
    }

    private static void LinkOrCopy(string source, string target)
    {
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (!OperatingSystem.IsWindows() || !CreateHardLink(target, source, IntPtr.Zero))
                throw new IOException($"无法创建硬链接：{source}");
        }
        catch
        {
            if (new FileInfo(source).Length <= 64L * 1024 * 1024)
                File.Copy(source, target);
            else
                File.WriteAllText(target + ".索引.txt", source, new UTF8Encoding(false));
        }
    }

    private static string ReadProjectScript(ProjectWorkspaceContext context, string title, string intro)
    {
        var roots = new[] { context.WorkflowProjectDir, context.SourceProjectDir };
        var textFile = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path).Contains("剧本", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
        if (textFile is not null)
            return File.ReadAllText(textFile);

        var docx = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.docx", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path).Contains("剧本", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (docx is not null)
        {
            using var document = WordprocessingDocument.Open(docx, false);
            var paragraphs = document.MainDocumentPart?.Document.Body?
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
                .Select(p => p.InnerText.Trim())
                .Where(text => text.Length > 0) ?? [];
            return string.Join(Environment.NewLine, paragraphs);
        }

        return $"{title}\n人物设定\n女主：现代都市短剧女主角。\n男主：现代都市短剧男主角。\n主要配角：推动剧情发展的成年配角。\n\n剧情简介：{intro}";
    }

    private static IEnumerable<string> FindSceneSources(ProjectWorkspaceContext context, string packageRoot)
    {
        return new[] { context.WorkflowProjectDir, context.SourceProjectDir }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(IsImage)
            .Where(path => !path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            .Where(path => path.Contains("场景", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("首帧", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("抽帧原图", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>> ExtractSceneFramesAsync(
        string root,
        IReadOnlyList<string> videos,
        Action<string>? log,
        CancellationToken ct)
    {
        var outputDir = Path.Combine(root, MaterialDirectoryName, "场景参考");
        Directory.CreateDirectory(outputDir);
        foreach (var old in Directory.EnumerateFiles(outputDir, "场景参考_*.jpg"))
            try { File.Delete(old); } catch { }
        if (videos.Count == 0) return [];

        var selected = videos.Count <= 8
            ? videos
            : Enumerable.Range(0, 8)
                .Select(index => videos[(int)Math.Round(index * (videos.Count - 1d) / 7d)])
                .ToArray();
        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        var outputs = new List<string>(selected.Count);
        foreach (var (video, index) in selected.Select((path, index) => (path, index)))
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(outputDir, $"场景参考_{index + 1:D2}.jpg");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y", "-ss", "00:00:01.500",
                         "-i", video, "-frames:v", "1", "-vf", "scale=1280:-2", "-q:v", "2", output,
                     })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 1024)
                outputs.Add(output);
            else
                log?.Invoke($"场景抽帧失败：{Path.GetFileName(video)}；{Truncate(stderr, 240)}");
        }
        return outputs;
    }

    private static JsonObject ReadMetadataObject(ProjectWorkspaceContext context)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(context.WorkflowProjectDir, "shortdrama-project.json"),
                     Path.Combine(context.SourceProjectDir, "shortdrama-project.json"),
                 })
        {
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject value)
                    return value;
            }
            catch { }
        }
        return new JsonObject();
    }

    private static string ResolveIntro(QueueProjectItem item, ProjectWorkspaceContext context)
    {
        if (!string.IsNullOrWhiteSpace(item.Description)) return item.Description.Trim();
        var metadata = ReadMetadataObject(context);
        return metadata["intro"]?.GetValue<string>()?.Trim() ?? "";
    }

    private static string? FindPoster(ProjectWorkspaceContext context) =>
        new[] { context.WorkflowProjectDir, context.SourceProjectDir }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .FirstOrDefault(path => IsImage(path) &&
                                    (Path.GetFileName(path).Contains("海报", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(path).Contains("poster", StringComparison.OrdinalIgnoreCase)));

    private static void EnsureImageModelConfigured(ClientSettings settings)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        var configured = provider == "ofox_image2"
            ? !string.IsNullOrWhiteSpace(settings.OfoxImage2Endpoint) &&
              !string.IsNullOrWhiteSpace(settings.OfoxImage2ApiKey) &&
              !string.IsNullOrWhiteSpace(settings.OfoxImage2ModelId)
            : !string.IsNullOrWhiteSpace(settings.ImageModelEndpoint) &&
              !string.IsNullOrWhiteSpace(settings.ImageModelApiKey) &&
              !string.IsNullOrWhiteSpace(settings.ImageModelId);
        if (!configured)
            throw new InvalidOperationException(
                "生成参考格式原始文件信息需要图片模型生成真人角色图；请先在系统设置中完整配置豆包或 Ofox Image2。不会使用视频抽帧冒充角色模型图。");
    }

    private static string ResolveModelId(ClientSettings settings) =>
        PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider) == "ofox_image2"
            ? FirstNonEmpty(settings.OfoxImage2ModelId, ClientSettingsDefaults.OfoxImage2ModelId)
            : settings.ImageModelId.Trim();

    private static string NormalizeOfoxPortraitSize(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "1024x1536" or "auto" ? normalized : "1024x1536";
    }

    private static string ComputeSourceFingerprint(
        string title,
        string intro,
        string script,
        ClientSettings settings)
    {
        var value = string.Join('\n',
            Version,
            title,
            intro,
            script,
            PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider),
            ResolveModelId(settings));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool HasMatchingFingerprint(string workflowProjectDirectory, string expected)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(GetStatePath(workflowProjectDirectory)));
            return document.RootElement.TryGetProperty("sourceFingerprint", out var value) &&
                   string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static FontFamily ResolveFont()
    {
        foreach (var name in new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Arial" })
            if (SystemFonts.TryGet(name, out var family)) return family;
        return SystemFonts.Collection.Families.First();
    }

    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    private static bool IsNonCharacterName(string value) =>
        value.Length is < 2 or > 12 ||
        value is "人物" or "场景" or "时间" or "地点" or "旁白" or "音效" or "音乐" or "BGM" or "OS" ||
        value.Contains("简介", StringComparison.Ordinal) || value.Contains("类型", StringComparison.Ordinal);

    private static bool IsGenericCharacterName(string value) =>
        value is "女主" or "男主" or "主要配角" or "配角" or "主角";

    private static string SanitizeFileName(string value) =>
        string.Concat(FirstNonEmpty(value, "未命名").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private static void TrySetHidden(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch { }
    }

    [GeneratedRegex(@"^(?<name>[^\s（(：:]{2,12})\s*[（(][^）)]{0,80}[）)]\s*[：:]?.+$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterDefinitionRegex();

    [GeneratedRegex(@"^(?<name>[^\s：:]{2,12})\s*[：:]\s*.+$", RegexOptions.CultureInvariant)]
    private static partial Regex DialogueRegex();

    [GeneratedRegex(@"(?:(?<=成员)|(?<=包括)|(?<=人物)|(?<=角色)|(?<=，)|(?<=。)|^)(?<names>[\p{IsCJKUnifiedIdeographs}]{2,6}(?:、[\p{IsCJKUnifiedIdeographs}]{2,6}){1,5})(?:三人|四人|五人|等人|一行人)", RegexOptions.CultureInvariant)]
    private static partial Regex IntroCharacterListRegex();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    internal sealed record CharacterProfile(string Name, string Description);
    private sealed record GeneratedCharacter(CharacterProfile Profile, string Path);
}
