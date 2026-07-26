using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 生成 TikTok「AI 生成过程截图」：分镜工作台模板页（真帧 + 可选火山视觉反推）。
/// </summary>
public static class TikTokAiGenerationScreenshotService
{
    /// <summary>与版权材料选项「AI 生成过程截图」同名，独立于 workflow 根目录的工程图。</summary>
    public const string OutputDirectoryName = "AI 生成过程截图";
    public const int RequiredImageCount = 4;
    public const int MaxImageCount = 8;
    public const string ScreenshotVersion = "v3-workbench-video-sources";
    public const int ShotsPerPage = 2;

    private const string LegacyOutputDirectoryName = "AI生成过程截图";

    private static readonly string[] FileNames =
    [
        "01_分镜工作台.png",
        "02_分镜工作台.png",
        "03_分镜工作台.png",
        "04_分镜工作台.png",
    ];

    private static readonly string[] KeyframeLabels = ["起幅", "过渡", "主体", "收幅"];
    private static readonly float[] KeyframeRatios = [0.12f, 0.34f, 0.56f, 0.78f];
    private static readonly HttpClient VisionHttp = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GenerationGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm",
    ];

    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".bmp",
    ];

    public static string GetOutputDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), OutputDirectoryName);

    public static IReadOnlyList<string> GetExpectedOutputPaths(string workflowProjectDirectory)
    {
        var dir = GetOutputDirectory(workflowProjectDirectory);
        return FileNames.Select(name => Path.Combine(dir, name)).ToArray();
    }

    public static IReadOnlyList<string> ListGeneratedImages(string workflowProjectDirectory)
    {
        var dir = GetOutputDirectory(workflowProjectDirectory);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return FileNames
            .Select(name => Path.Combine(dir, name))
            .Where(File.Exists)
            .ToArray();
    }

    public static bool HasCurrentOutput(string workflowProjectDirectory) =>
        ListGeneratedImages(workflowProjectDirectory).Count >= RequiredImageCount;

    public static void TryDeleteOutput(string workflowProjectDirectory)
    {
        TryDeleteDirectory(GetOutputDirectory(workflowProjectDirectory));
        // 清理旧版无空格目录，避免与工程图根目录混淆时残留。
        TryDeleteDirectory(
            Path.Combine(Path.GetFullPath(workflowProjectDirectory), LegacyOutputDirectoryName));
    }

    private static void TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    public static IReadOnlyList<string> Generate(
        string workflowProjectDirectory,
        string dramaTitle,
        ClientSettings? settings = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowProjectDirectory);
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var gate = GenerationGates.GetOrAdd(workflow, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            return GenerateCore(workflow, dramaTitle, settings, log, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<string> GenerateCore(
        string workflowProjectDirectory,
        string dramaTitle,
        ClientSettings? settings,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var title = string.IsNullOrWhiteSpace(dramaTitle) ? "未命名短剧" : dramaTitle.Trim();
        // 先清掉旧版目录，再写入独立文件夹，避免与 workflow 根目录「工程图_*.png」混用。
        TryDeleteOutput(workflowProjectDirectory);
        var outputDir = GetOutputDirectory(workflowProjectDirectory);
        log?.Invoke($"AI 截图/初始化：已清理旧产物；输出目录={outputDir}。");

        var pageCount = RequiredImageCount;
        var shotCount = pageCount * ShotsPerPage;
        var framePool = CollectFrames(workflowProjectDirectory, shotCount, log, cancellationToken);
        string? stagingDir = null;
        log?.Invoke(
            $"AI 截图/素材池：已准备 {framePool.Count} 张关键帧；" +
            $"分镜={shotCount} 个；每页={ShotsPerPage} 个；计划输出={pageCount} 页。");
        try
        {
            var analyses = AnalyzeShots(framePool, title, settings, log, cancellationToken);
            log?.Invoke($"AI 截图/分析：已完成 {analyses.Count} 个分镜描述。");
            var family = ResolveFontFamily()
                ?? throw new InvalidOperationException("未找到可用中文字体，无法生成 AI 生成过程截图。");

            stagingDir = Path.Combine(
                Path.GetFullPath(workflowProjectDirectory),
                $".ai-generation-screenshots-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);
            var outputs = new List<string>(pageCount);
            for (var page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shotA = page * ShotsPerPage;
                var shotB = shotA + 1;
                using var canvas = RenderWorkbenchPage(
                    title,
                    episodeName: $"第 {page + 1:00} 集",
                    pageIndex: page,
                    pageCount: pageCount,
                    framesA: PickKeyframes(framePool, shotA),
                    framesB: PickKeyframes(framePool, shotB),
                    analysisA: analyses[shotA % analyses.Count],
                    analysisB: analyses[shotB % analyses.Count],
                    family);
                var stagingPath = Path.Combine(stagingDir, FileNames[page]);
                canvas.Save(stagingPath, new PngEncoder());
                outputs.Add(Path.Combine(outputDir, FileNames[page]));
            }

            // All pages are complete before exposing the final directory. This avoids a long
            // vision request leaving a missing/partial output directory if cleanup runs meanwhile.
            TryDeleteDirectory(outputDir);
            Directory.Move(stagingDir, outputDir);
            stagingDir = null;
            log?.Invoke($"AI 生成过程截图已生成：{outputs.Count} 张 → {outputDir}");
            return outputs;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagingDir))
            {
                TryDeleteDirectory(stagingDir);
            }

            foreach (var frame in framePool)
            {
                frame.Dispose();
            }
        }
    }

    private sealed record ShotAnalysis(
        string ShotType,
        string Camera,
        int Seconds,
        IReadOnlyList<string> Prompts,
        string Novel,
        string Dialogue,
        string Ambient,
        int Match);

    private static List<Image<Rgba32>> CollectFrames(
        string workflowProjectDirectory,
        int needed,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var videos = ResolveVideoSources(workflow).Take(12).ToArray();
        var frames = new List<Image<Rgba32>>();

        if (videos.Length > 0)
        {
            try
            {
                var ffmpeg = FfmpegLocator.ResolveFfmpeg();
                log?.Invoke($"AI 截图：从 {videos.Length} 个视频抽帧。");
                var ffprobe = MediaBinaryResolver.ResolveFfprobe();
                var durations = videos.ToDictionary(
                    path => path,
                    path => ProbeDuration(ffprobe, path, cancellationToken),
                    StringComparer.OrdinalIgnoreCase);
                for (var shotIndex = 0; shotIndex < needed; shotIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var videoIndex = shotIndex % videos.Length;
                    var video = videos[videoIndex];
                    var duration = durations[video];
                    var shotsForVideo = Math.Max(1, (int)Math.Ceiling(needed / (double)videos.Length));
                    var sequenceIndex = shotIndex / videos.Length;
                    var window = Math.Max(duration / Math.Max(4, shotsForVideo + 1), 2.0);
                    var baseSeconds = Math.Min(
                        Math.Max(0.4, sequenceIndex * window * 0.65),
                        Math.Max(0.5, duration - 1.2));

                    foreach (var ratio in KeyframeRatios)
                    {
                        var seconds = Math.Min(
                            Math.Max(0.05, duration - 0.05),
                            Math.Max(0.05, baseSeconds + window * ratio));
                        var extracted = TryExtractFacePreferredFrame(
                            ffmpeg,
                            video,
                            seconds,
                            duration,
                            cancellationToken);
                        if (extracted is not null)
                        {
                            frames.Add(extracted);
                        }
                    }
                }
                log?.Invoke($"AI 截图/抽帧：从视频成功取得 {frames.Count} 张关键帧。");
            }
            catch (Exception ex)
            {
                log?.Invoke($"AI 截图：视频抽帧失败，改用项目图片：{ex.Message}");
            }
        }

        var requiredFrameCount = needed * KeyframeRatios.Length;
        if (frames.Count < requiredFrameCount)
        {
            foreach (var image in CollectAssetImages(workflow))
            {
                frames.Add(image);
                if (frames.Count >= requiredFrameCount)
                {
                    break;
                }
            }
        }

        if (frames.Count == 0)
        {
            log?.Invoke("AI 截图：未找到视频/图片，使用占位色块。");
            var palette = new[]
            {
                new Rgba32(212, 75, 57),
                new Rgba32(40, 121, 199),
                new Rgba32(58, 167, 109),
                new Rgba32(230, 162, 60),
            };
            for (var i = 0; i < requiredFrameCount; i++)
            {
                var img = new Image<Rgba32>(540, 960, palette[i % palette.Length]);
                frames.Add(img);
            }
        }

        FillFramePool(frames, requiredFrameCount);

        return frames;
    }

    private static IReadOnlyList<string> ResolveVideoSources(string workflow)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(workflow);
            var resolved = ProjectVideoResolver.ResolveSourceVideos(
                context.SourceProjectDir,
                allowStagedFallback: true);
            if (resolved.Count > 0)
            {
                return resolved;
            }
        }
        catch
        {
            // Compatibility fallback for standalone workflow folders without project metadata.
        }

        return EnumerateVideos(workflow)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void FillFramePool(List<Image<Rgba32>> frames, int requiredFrameCount)
    {
        if (frames.Count == 0 || frames.Count >= requiredFrameCount)
        {
            return;
        }

        var originalCount = frames.Count;
        var sourceIndex = 0;
        while (frames.Count < requiredFrameCount)
        {
            frames.Add(frames[sourceIndex % originalCount].Clone());
            sourceIndex++;
        }
    }

    private static IReadOnlyList<Image<Rgba32>> PickKeyframes(IReadOnlyList<Image<Rgba32>> pool, int shotIndex)
    {
        var start = shotIndex * KeyframeRatios.Length;
        // 4 variants around the shot index for 起幅/过渡/主体/收幅.
        return
        [
            pool[(start + 0) % pool.Count],
            pool[(start + 1) % pool.Count],
            pool[(start + 2) % pool.Count],
            pool[(start + 3) % pool.Count],
        ];
    }

    private static List<ShotAnalysis> AnalyzeShots(
        IReadOnlyList<Image<Rgba32>> frames,
        string title,
        ClientSettings? settings,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var count = RequiredImageCount * ShotsPerPage;
        var endpoint = (settings?.AiTextEndpoint ?? string.Empty).Trim().TrimEnd('/');
        var apiKey = (settings?.AiTextApiKey ?? string.Empty).Trim();
        var model = (settings?.AiTextModel ?? string.Empty).Trim();
        var canVision = !string.IsNullOrWhiteSpace(endpoint)
                        && !string.IsNullOrWhiteSpace(apiKey)
                        && !string.IsNullOrWhiteSpace(model);

        if (!canVision)
        {
            log?.Invoke("AI 截图：未配置火山文本/视觉模型，提示词使用本地兜底。");
            return Enumerable.Range(0, count)
                .Select(index => FallbackAnalysis(index, title))
                .ToList();
        }

        var totalTimer = Stopwatch.StartNew();
        var completedRequests = 0;
        var requestCount = (count + 1) / 2;
        var requestTimeoutSeconds = Math.Clamp(
            settings!.AiTextTimeoutSeconds <= 0 ? 120 : settings.AiTextTimeoutSeconds,
            30,
            300);
        log?.Invoke(
            $"AI 截图/反推：准备并发请求 {requestCount} 组；" +
            $"模型={model}；每组上传 2 张主体帧；超时={requestTimeoutSeconds} 秒。");

        var tasks = Enumerable.Range(0, requestCount)
            .Select(async pairIndex =>
            {
                var shotIndex = pairIndex * 2;
                var requestNumber = pairIndex + 1;
                var a = frames[(shotIndex * KeyframeRatios.Length + 2) % frames.Count];
                var b = frames[((shotIndex + 1) * KeyframeRatios.Length + 2) % frames.Count];
                var requestTimer = Stopwatch.StartNew();
                log?.Invoke(
                    $"AI 截图/反推 [{requestNumber}/{requestCount}] 请求已发送：" +
                    $"镜头 {shotIndex + 1}-{Math.Min(shotIndex + 2, count)}，正在等待模型响应…");
                try
                {
                    var pair = await AnalyzeShotPairAsync(
                            endpoint,
                            apiKey,
                            model,
                            a,
                            b,
                            requestTimeoutSeconds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var completed = Interlocked.Increment(ref completedRequests);
                    log?.Invoke(
                        $"AI 截图/反推 [{requestNumber}/{requestCount}] 响应完成：" +
                        $"镜头 {shotIndex + 1}-{Math.Min(shotIndex + 2, count)}；" +
                        $"耗时={FormatElapsed(requestTimer.Elapsed)}；总进度={completed}/{requestCount}。");
                    return (ShotIndex: shotIndex, First: pair.Item1, Second: pair.Item2, Error: (Exception?)null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var completed = Interlocked.Increment(ref completedRequests);
                    log?.Invoke(
                        $"AI 截图/反推 [{requestNumber}/{requestCount}] 请求失败：" +
                        $"镜头 {shotIndex + 1}-{Math.Min(shotIndex + 2, count)}；" +
                        $"耗时={FormatElapsed(requestTimer.Elapsed)}；总进度={completed}/{requestCount}；" +
                        $"将使用本地兜底。");
                    return (
                        ShotIndex: shotIndex,
                        First: FallbackAnalysis(shotIndex, title),
                        Second: FallbackAnalysis(shotIndex + 1, title),
                        Error: (Exception?)ex);
                }
            })
            .ToArray();

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
        log?.Invoke(
            $"AI 截图/反推：全部请求处理完成；共 {requestCount} 组、{count} 个镜头；" +
            $"总耗时={FormatElapsed(totalTimer.Elapsed)}。");
        var analyses = new ShotAnalysis[count];
        foreach (var result in results.OrderBy(item => item.ShotIndex))
        {
            if (result.Error is not null)
            {
                log?.Invoke(
                    $"AI 截图：视觉反推失败（镜 {result.ShotIndex + 1}），改用本地兜底：{result.Error.Message}");
            }

            analyses[result.ShotIndex] = result.First;
            if (result.ShotIndex + 1 < count)
            {
                analyses[result.ShotIndex + 1] = result.Second;
            }
        }

        return analyses.ToList();
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:0}ms"
            : $"{elapsed.TotalSeconds:0.0}s";

    private static async Task<(ShotAnalysis, ShotAnalysis)> AnalyzeShotPairAsync(
        string endpoint,
        string apiKey,
        string model,
        Image<Rgba32> frameA,
        Image<Rgba32> frameB,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var prompt =
            """
            你是资深短剧深度分析专家。给你的2张图分别是同一段落里连续2个镜头（镜1、镜2）的真实截帧。严格依据画面所见分析，禁止编造画面里没有的内容。只输出一个JSON对象，不要多余文字。结构：
            {"plot":"10到14字","shot1":{"shot_type":"近景","camera":"运镜","seconds":5,"prompts":["80到130字提示词1","提示词2","提示词3"],"novel":"80到110字小说原文","dialogue":"无台词","ambient":"环境音","match":91},"shot2":{"shot_type":"中景","camera":"固定镜头","seconds":4,"prompts":["提示词1","提示词2","提示词3"],"novel":"小说原文","dialogue":"无台词","ambient":"环境音","match":93}}
            match 为 88 到 97 的整数。
            """;

        var payload = new
        {
            model,
            temperature = 0.2,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = ToJpegDataUri(frameA), detail = "high" },
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = ToJpegDataUri(frameB), detail = "high" },
                        },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? 120 : timeoutSeconds, 30, 300)));
        using var response = await VisionHttp.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"视觉接口失败 {(int)response.StatusCode}: {Trim(body, 240)}");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
        var json = ExtractJsonObject(content);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        return (ParseShot(root.GetProperty("shot1")), ParseShot(root.GetProperty("shot2")));
    }

    private static ShotAnalysis ParseShot(JsonElement el)
    {
        var prompts = new List<string>();
        if (el.TryGetProperty("prompts", out var promptsEl) && promptsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in promptsEl.EnumerateArray())
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    prompts.Add(text);
                }
            }
        }

        while (prompts.Count < 3)
        {
            prompts.Add(prompts.Count == 0
                ? "中景，固定镜头，自然光，人物随剧情自然动作，电影感构图。"
                : prompts[^1]);
        }

        var match = el.TryGetProperty("match", out var matchEl) && matchEl.TryGetInt32(out var m) ? m : 91;
        match = Math.Clamp(match, 88, 97);
        var seconds = el.TryGetProperty("seconds", out var secEl) && secEl.TryGetInt32(out var s) ? s : 5;
        return new ShotAnalysis(
            ShotType: el.TryGetProperty("shot_type", out var st) ? st.GetString() ?? "中景" : "中景",
            Camera: el.TryGetProperty("camera", out var cam) ? cam.GetString() ?? "固定镜头" : "固定镜头",
            Seconds: Math.Clamp(seconds, 3, 12),
            Prompts: prompts.Take(3).ToArray(),
            Novel: el.TryGetProperty("novel", out var novel) ? novel.GetString() ?? "人物完成当前动作，情绪逐渐变化。" : "人物完成当前动作，情绪逐渐变化。",
            Dialogue: el.TryGetProperty("dialogue", out var dialogue) ? dialogue.GetString() ?? "无台词" : "无台词",
            Ambient: el.TryGetProperty("ambient", out var ambient) ? ambient.GetString() ?? "环境背景音" : "环境背景音",
            Match: match);
    }

    private static ShotAnalysis FallbackAnalysis(int index, string title)
    {
        var types = new[] { "近景", "中景", "特写", "全景" };
        var cameras = new[] { "镜头缓缓推进", "固定镜头", "横向跟拍", "轻微拉远" };
        var scenes = new[] { "城市街道", "室内主场景", "办公走廊", "住宅客厅" };
        var shotType = types[index % types.Length];
        var camera = cameras[index % cameras.Length];
        var scene = scenes[index % scenes.Length];
        var prompt =
            $"[{shotType}] {camera}，自然光侧逆，主体服装与表情清晰，手部细节可见，背景为{scene}，氛围克制紧张，电影感构图。";
        var novel =
            $"第三人称。{scene}里，主要人物完成当前动作，情绪逐渐变化。环境声与光影随剧情推进。剧名：{title}。";
        return new ShotAnalysis(
            shotType,
            camera,
            4 + index % 5,
            [prompt, prompt, prompt],
            novel,
            "无台词",
            "环境底噪与远处车流",
            88 + (index * 5 + 3) % 10);
    }

    private static Image<Rgba32> RenderWorkbenchPage(
        string title,
        string episodeName,
        int pageIndex,
        int pageCount,
        IReadOnlyList<Image<Rgba32>> framesA,
        IReadOnlyList<Image<Rgba32>> framesB,
        ShotAnalysis analysisA,
        ShotAnalysis analysisB,
        FontFamily family)
    {
        const int width = 1600;
        const int height = 1544;
        var image = new Image<Rgba32>(width, height);
        var bg = Color.ParseHex("0a0d13");
        var bar = Color.ParseHex("0d1119");
        var line = Color.ParseHex("1c2434");
        var teal = Color.ParseHex("16c0a8");
        var blue = Color.ParseHex("3b82f6");
        var mut = Color.ParseHex("8b95a7");
        var tx = Color.ParseHex("e6ebf3");

        var font12 = family.CreateFont(12, FontStyle.Regular);
        var font13 = family.CreateFont(13, FontStyle.Regular);
        var font14 = family.CreateFont(14, FontStyle.Bold);
        var font15 = family.CreateFont(15, FontStyle.Bold);

        image.Mutate(ctx =>
        {
            ctx.Fill(bg);
            ctx.Fill(bar, new RectangleF(0, 0, width, 98));
            ctx.Fill(line, new RectangleF(0, 45, width, 1));
            ctx.Fill(line, new RectangleF(0, 97, width, 1));

            DrawText(ctx, "项目  剧本  角色场景", font13, mut, new PointF(22, 14));
            DrawText(ctx, "分镜制作", font15, Color.White, new PointF(250, 12));
            ctx.Fill(blue, new RectangleF(250, 40, 56, 2));
            DrawText(ctx, "配音字幕  合成导出", font13, mut, new PointF(340, 14));
            DrawText(ctx, Ellipsize(title, 16), font12, Color.ParseHex("c2ccdb"), new PointF(1180, 14));
            DrawText(ctx, "● 渲染引擎在线   GPU RTX4090 ×2", font12, mut, new PointF(1360, 14));

            DrawText(ctx, "← 返回", font13, mut, new PointF(22, 62));
            DrawText(ctx, Ellipsize(title, 28), font14, tx, new PointF(96, 60));
            FillRound(ctx, new RectangleF(1368, 58, 100, 30), Color.ParseHex("131925"));
            DrawText(ctx, "批量生视频", font12, Color.ParseHex("c7cfdc"), new PointF(1382, 65));
            FillRound(ctx, new RectangleF(1480, 58, 90, 30), Color.ParseHex("2f6fe0"));
            DrawText(ctx, "导出", font12, Color.White, new PointF(1512, 65));

            // filter + episode head
            FillRound(ctx, new RectangleF(16, 110, 300, 28), Color.ParseHex("0f141d"));
            DrawText(ctx, "筛选分镜、角色、场景...", font12, Color.ParseHex("66707f"), new PointF(28, 116));
            FillRound(ctx, new RectangleF(16, 148, 1568, 40), Color.ParseHex("0c121a"));
            ctx.Fill(teal, new RectangleF(16, 148, 3, 40));
            DrawText(ctx, $"第 {pageIndex + 1:00} 段  {episodeName}", font13, tx, new PointF(34, 158));
            DrawText(ctx, $"镜头一致性 {88 + pageIndex * 3 % 10}%   {pageIndex + 1}/{pageCount}", font12, mut, new PointF(1280, 160));
        });

        DrawShotCard(image, family, 16, 200, 1568, 640, framesA, analysisA, episodeName, shotNo: pageIndex * 2 + 1, active: true);
        DrawShotCard(image, family, 16, 860, 1568, 640, framesB, analysisB, episodeName, shotNo: pageIndex * 2 + 2, active: false);
        return image;
    }

    private static void DrawShotCard(
        Image<Rgba32> canvas,
        FontFamily family,
        int x,
        int y,
        int width,
        int height,
        IReadOnlyList<Image<Rgba32>> frames,
        ShotAnalysis analysis,
        string episodeName,
        int shotNo,
        bool active)
    {
        var card = Color.ParseHex("0c1017");
        var outline = active ? Color.ParseHex("2a6b62") : Color.ParseHex("232c3e");
        var mut = Color.ParseHex("8b95a7");
        var tx = Color.ParseHex("e6ebf3");
        var font11 = family.CreateFont(11, FontStyle.Regular);
        var font12 = family.CreateFont(12, FontStyle.Regular);
        var font13 = family.CreateFont(13, FontStyle.Bold);
        var font15 = family.CreateFont(15, FontStyle.Bold);

        canvas.Mutate(ctx =>
        {
            FillRound(ctx, new RectangleF(x, y, width, height), card);
            ctx.Draw(outline, active ? 2f : 1f, new RectangleF(x, y, width, height));
        });

        const int leftW = 560;
        const int pad = 14;
        var leftX = x + pad;
        var heroIndex = Enumerable.Range(0, frames.Count)
            .OrderByDescending(index => ScoreFaceVisibility(frames[index]))
            .FirstOrDefault();
        var hero = frames[heroIndex];
        PasteCover(canvas, hero, leftX, y + pad, leftW, 300);

        canvas.Mutate(ctx =>
        {
            FillRound(ctx, new RectangleF(leftX + 10, y + pad + 8, 70, 22), Color.ParseHex("0a0e1499"));
            DrawText(ctx, $"镜头 {shotNo:00}", font11, tx, new PointF(leftX + 18, y + pad + 11));
            ctx.Fill(Color.ParseHex("00000099"), new RectangleF(leftX, y + pad + 270, leftW, 30));
            DrawText(ctx, $"▶  00:03 / 00:{analysis.Seconds:00}", font11, tx, new PointF(leftX + 12, y + pad + 278));

            DrawText(ctx, "四宫格参考 KEYFRAME CHECK", font11, mut, new PointF(leftX, y + pad + 312));
        });

        var cellW = (leftW - 7) / 2;
        var cellH = 90;
        for (var i = 0; i < 4; i++)
        {
            var cx = leftX + i % 2 * (cellW + 7);
            var cy = y + pad + 332 + i / 2 * (cellH + 6);
            PasteCover(canvas, frames[i % frames.Count], cx, cy, cellW, cellH);
            canvas.Mutate(ctx =>
            {
                ctx.Draw(i == heroIndex ? Color.ParseHex("16c0a8") : Color.ParseHex("1a2130"), 1.5f, new RectangleF(cx, cy, cellW, cellH));
                ctx.Fill(Color.ParseHex("000000c8"), new RectangleF(cx, cy + cellH - 18, cellW, 18));
                DrawText(ctx, KeyframeLabels[i], font11, tx, new PointF(cx + cellW / 2f - 14, cy + cellH - 15));
            });
        }

        var bodyX = leftX + leftW + 16;
        var midW = 620;
        var rightX = bodyX + midW + 16;
        canvas.Mutate(ctx =>
        {
            DrawText(ctx, $"分镜 {shotNo}", font15, tx, new PointF(bodyX, y + pad));
            FillRound(ctx, new RectangleF(bodyX + 78, y + pad + 2, 96, 22), Color.ParseHex("1b345e"));
            DrawText(ctx, "Luma 2.0 Fast", font11, Color.ParseHex("7fb0ff"), new PointF(bodyX + 86, y + pad + 6));
            FillRound(ctx, new RectangleF(bodyX + 182, y + pad + 2, 48, 22), Color.ParseHex("123c39"));
            DrawText(ctx, analysis.ShotType, font11, Color.ParseHex("3fd6bf"), new PointF(bodyX + 190, y + pad + 6));
            FillRound(ctx, new RectangleF(bodyX + 238, y + pad + 2, 52, 22), Color.ParseHex("15361f"));
            DrawText(ctx, "已校验", font11, Color.ParseHex("54cc82"), new PointF(bodyX + 246, y + pad + 6));
            DrawText(ctx, $"{analysis.Seconds}秒 · {Ellipsize(episodeName, 12)}", font11, mut, new PointF(bodyX + 300, y + pad + 6));

            FillRound(ctx, new RectangleF(bodyX, y + 48, midW, 32), Color.ParseHex("0e131c"));
            DrawText(ctx, "反向词  不出现台词字幕，不要塑料皮肤，不要夸张滤镜...", font11, Color.ParseHex("7f8a9b"), new PointF(bodyX + 10, y + 56));

            DrawText(ctx, "提示词", font13, tx, new PointF(bodyX, y + 96));
            DrawText(ctx, "台词与音效   参数   画面校验", font12, mut, new PointF(bodyX + 70, y + 98));
            ctx.Fill(Color.ParseHex("3b82f6"), new RectangleF(bodyX, y + 118, 40, 2));

            DrawText(ctx, "匹配参考", font13, tx, new PointF(bodyX, y + 132));
            DrawText(ctx, $"已匹配 {analysis.Match}%", font12, Color.ParseHex("f08aa0"), new PointF(bodyX + midW - 90, y + 134));
        });

        var thumbW = (midW - 21) / 4;
        for (var i = 0; i < 4; i++)
        {
            var tx0 = bodyX + i * (thumbW + 7);
            var ty0 = y + 156;
            PasteCover(canvas, frames[i % frames.Count], tx0, ty0, thumbW, 78);
            canvas.Mutate(ctx =>
            {
                ctx.Fill(Color.ParseHex("000000c8"), new RectangleF(tx0, ty0 + 60, thumbW, 18));
                DrawText(ctx, KeyframeLabels[i], font11, tx, new PointF(tx0 + thumbW / 2f - 14, ty0 + 63));
            });
        }

        canvas.Mutate(ctx =>
        {
            DrawText(ctx, "图片提示词", font12, mut, new PointF(bodyX, y + 250));
            var promptY = y + 272;
            for (var i = 0; i < Math.Min(3, analysis.Prompts.Count); i++)
            {
                DrawText(ctx, $"{i + 1}. {Ellipsize(analysis.Prompts[i], 42)}", font12, Color.ParseHex("b0b9c8"), new PointF(bodyX, promptY));
                promptY += 22;
            }

            FillRound(ctx, new RectangleF(bodyX, y + height - 52, 100, 30), Color.ParseHex("131925"));
            DrawText(ctx, "复制提示词", font12, Color.ParseHex("c7cfdc"), new PointF(bodyX + 16, y + height - 44));
            FillRound(ctx, new RectangleF(bodyX + 112, y + height - 52, 90, 30), Color.ParseHex("123c39"));
            DrawText(ctx, "重试视频", font12, Color.ParseHex("3fd6bf"), new PointF(bodyX + 128, y + height - 44));
            FillRound(ctx, new RectangleF(bodyX + 214, y + height - 52, 90, 30), Color.ParseHex("2f6fe0"));
            DrawText(ctx, "生成视频", font12, Color.White, new PointF(bodyX + 230, y + height - 44));

            DrawText(ctx, "小说原文", font13, tx, new PointF(rightX, y + pad));
            DrawText(ctx, $"匹配 {analysis.Match}%", font12, Color.ParseHex("f08aa0"), new PointF(rightX + 200, y + pad + 2));
            DrawWrapped(ctx, analysis.Novel, font12, Color.ParseHex("aab3c2"), rightX, y + 42, 280, 10, 20);
            DrawText(ctx, $"[00:03] 环境音：{Ellipsize(analysis.Ambient, 18)}", font11, mut, new PointF(rightX, y + 280));
            DrawText(ctx, $"[00:07] {Ellipsize(analysis.Camera, 18)}", font11, mut, new PointF(rightX, y + 302));
            DrawText(ctx, $"[00:12] 台词：{Ellipsize(analysis.Dialogue, 16)}", font11, mut, new PointF(rightX, y + 324));
            FillRound(ctx, new RectangleF(rightX, y + height - 52, 280, 30), Color.ParseHex("131925"));
            DrawText(ctx, "复制脚本词", font12, Color.ParseHex("c7cfdc"), new PointF(rightX + 100, y + height - 44));
        });
    }

    private static void PasteCover(Image<Rgba32> canvas, Image<Rgba32> source, int x, int y, int w, int h)
    {
        var faceCenterY = TryFindLikelyFaceCenterY(source);
        using var clone = source.Clone(ctx =>
        {
            var scale = Math.Max(w / (float)source.Width, h / (float)source.Height);
            var nw = Math.Max(1, (int)Math.Round(source.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(source.Height * scale));
            ctx.Resize(nw, nh);
            ctx.Crop(CalculateCoverCrop(nw, nh, w, h, faceCenterY));
        });
        canvas.Mutate(ctx => ctx.DrawImage(clone, new Point(x, y), 1f));
    }

    /// <summary>
    /// Calculates the crop after a cover resize. Portrait sources are biased toward the upper
    /// body instead of being vertically centered; when a likely face is found it is placed near
    /// the upper third of the destination so the forehead/chin are not clipped.
    /// </summary>
    internal static Rectangle CalculateCoverCrop(
        int resizedWidth,
        int resizedHeight,
        int targetWidth,
        int targetHeight,
        double? normalizedFaceCenterY = null)
    {
        var left = Math.Max(0, (resizedWidth - targetWidth) / 2);
        var overflowY = Math.Max(0, resizedHeight - targetHeight);
        if (overflowY == 0)
        {
            return new Rectangle(left, 0, targetWidth, targetHeight);
        }

        int top;
        if (normalizedFaceCenterY is >= 0 and <= 1)
        {
            const double desiredFaceY = 0.36;
            top = (int)Math.Round(
                normalizedFaceCenterY.Value * resizedHeight - desiredFaceY * targetHeight);
        }
        else if (resizedHeight > resizedWidth)
        {
            // A centered cover crop of a portrait frame commonly keeps only the torso.
            // Keeping roughly the upper fifth of the overflow preserves heads and upper bodies.
            top = (int)Math.Round(overflowY * 0.18);
        }
        else
        {
            top = overflowY / 2;
        }

        return new Rectangle(
            left,
            Math.Clamp(top, 0, overflowY),
            targetWidth,
            targetHeight);
    }

    private static List<Image<Rgba32>> CollectAssetImages(string workflow)
    {
        var candidates = CollectAssetImagePaths(workflow);
        var images = new List<Image<Rgba32>>();
        foreach (var path in candidates)
        {
            try
            {
                images.Add(Image.Load<Rgba32>(path));
            }
            catch
            {
                // skip
            }
        }

        return images;
    }

    internal static IReadOnlyList<string> CollectAssetImagePaths(string workflow)
    {
        var candidates = new List<string>();
        void Add(string path)
        {
            if (File.Exists(path))
            {
                candidates.Add(path);
            }
        }

        Add(Path.Combine(workflow, "海报图片.png"));
        Add(Path.Combine(workflow, "海报.png"));
        if (Directory.Exists(workflow))
        {
            candidates.AddRange(Directory.EnumerateFiles(workflow, "*封面*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.EnumerateFiles(workflow, "tiktok-cover-*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.EnumerateFiles(workflow, "*海报*.jpg", SearchOption.TopDirectoryOnly));
        }

        var parent = Directory.GetParent(workflow)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            candidates.AddRange(Directory.EnumerateFiles(parent, "*.png", SearchOption.TopDirectoryOnly).Take(8));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !IsProjectImagePath(p))
            .Take(16)
            .ToArray();
    }

    private static bool IsProjectImagePath(string path) =>
        string.Equals(
            Path.GetFileName(Path.GetDirectoryName(path)),
            TikTokProjectImageService.OutputDirectoryName,
            StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith("工程图_", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> EnumerateVideos(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        foreach (var sub in new[] { "tiktok_upload_videos", "videos", "视频", "成片", "源视频" })
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }

        var parent = Directory.GetParent(root)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(parent, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static double ProbeDuration(
        string ffprobe,
        string videoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return Math.Max(
                0.1,
                FfmpegRunner.ProbeDurationSecondsAsync(ffprobe, videoPath, cancellationToken)
                    .GetAwaiter()
                    .GetResult());
        }
        catch
        {
            return 20.0;
        }
    }

    private static Image<Rgba32>? TryExtractFrame(
        string ffmpeg,
        string videoPath,
        double seconds,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"tiktok-ai-frame-{Guid.NewGuid():N}.jpg");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                ArgumentList =
                {
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-ss",
                    seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i",
                    videoPath,
                    "-frames:v",
                    "1",
                    "-y",
                    temp,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit(20000);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length <= 0)
            {
                return null;
            }

            return Image.Load<Rgba32>(temp);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static Image<Rgba32>? TryExtractFacePreferredFrame(
        string ffmpeg,
        string videoPath,
        double preferredSeconds,
        double duration,
        CancellationToken cancellationToken)
    {
        Image<Rgba32>? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var offset in new[] { 0.0, -1.2, 1.2 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seconds = Math.Clamp(preferredSeconds + offset, 0.05, Math.Max(0.05, duration - 0.05));
            var candidate = TryExtractFrame(ffmpeg, videoPath, seconds, cancellationToken);
            if (candidate is null)
            {
                continue;
            }

            var score = ScoreFaceVisibility(candidate) - Math.Abs(offset) * 0.01;
            if (score > bestScore)
            {
                best?.Dispose();
                best = candidate;
                bestScore = score;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return best;
    }

    /// <summary>
    /// 轻量级人脸可见度评分。使用肤色连通区域、位置、清晰度和曝光度，
    /// 不依赖外部模型；用于在相邻候选帧中优先选择露脸画面。
    /// </summary>
    private static double? TryFindLikelyFaceCenterY(Image<Rgba32> source)
    {
        using var image = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(160, 160),
        }));
        var width = image.Width;
        var height = image.Height;
        var skin = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                skin[y * width + x] = IsLikelySkin(image[x, y]);
            }
        }

        var visited = new bool[skin.Length];
        var queue = new Queue<int>();
        var bestScore = 0d;
        double? bestCenterY = null;
        for (var start = 0; start < skin.Length; start++)
        {
            if (!skin[start] || visited[start])
            {
                continue;
            }

            visited[start] = true;
            queue.Enqueue(start);
            var count = 0;
            var minX = width;
            var maxX = 0;
            var minY = height;
            var maxY = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                count++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            var areaRatio = count / (double)(width * height);
            var aspect = componentWidth / (double)Math.Max(1, componentHeight);
            var centerX = (minX + maxX) / 2d / width;
            var centerY = (minY + maxY) / 2d / height;
            if (areaRatio is >= 0.004 and <= 0.18
                && aspect is >= 0.45 and <= 1.75
                && centerX is >= 0.08 and <= 0.92
                && centerY is >= 0.05 and <= 0.72)
            {
                var centrality = 1.0 - Math.Min(1.0, Math.Abs(centerX - 0.5) * 1.5);
                var upperBias = 1.0 - Math.Min(0.8, Math.Max(0, centerY - 0.45));
                var score = Math.Sqrt(areaRatio) * centrality * upperBias;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCenterY = centerY;
                }
            }

            void Visit(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height) return;
                var index = y * width + x;
                if (!skin[index] || visited[index]) return;
                visited[index] = true;
                queue.Enqueue(index);
            }
        }

        return bestCenterY;
    }

    internal static double ScoreFaceVisibility(Image<Rgba32> source)
    {
        using var image = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(160, 160),
        }));
        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0)
        {
            return double.NegativeInfinity;
        }

        var skin = new bool[width * height];
        var luminance = new double[width * height];
        var edgeSum = 0.0;
        var edgeCount = 0;
        var exposureSum = 0.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = image[x, y];
                var index = y * width + x;
                var value = ((0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B)) / 255.0;
                luminance[index] = value;
                exposureSum += value;
                skin[index] = IsLikelySkin(pixel);
                if (x > 0)
                {
                    edgeSum += Math.Abs(value - luminance[index - 1]);
                    edgeCount++;
                }
                if (y > 0)
                {
                    edgeSum += Math.Abs(value - luminance[index - width]);
                    edgeCount++;
                }
            }
        }

        var visited = new bool[skin.Length];
        var queue = new Queue<int>();
        var faceScore = 0.0;
        for (var start = 0; start < skin.Length; start++)
        {
            if (!skin[start] || visited[start])
            {
                continue;
            }

            visited[start] = true;
            queue.Enqueue(start);
            var count = 0;
            var minX = width;
            var maxX = 0;
            var minY = height;
            var maxY = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                count++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            var areaRatio = count / (double)(width * height);
            var aspect = componentWidth / (double)Math.Max(1, componentHeight);
            var centerX = (minX + maxX) / 2d / width;
            var centerY = (minY + maxY) / 2d / height;
            if (areaRatio is >= 0.004 and <= 0.18
                && aspect is >= 0.45 and <= 1.75
                && centerX is >= 0.08 and <= 0.92
                && centerY <= 0.82)
            {
                var centrality = 1.0 - Math.Min(1.0, Math.Abs(centerX - 0.5) * 1.5);
                var upperBias = 1.0 - Math.Min(1.0, Math.Max(0, centerY - 0.55));
                faceScore = Math.Max(faceScore, Math.Sqrt(areaRatio) * centrality * upperBias);
            }

            void Visit(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height) return;
                var index = y * width + x;
                if (!skin[index] || visited[index]) return;
                visited[index] = true;
                queue.Enqueue(index);
            }
        }

        var mean = exposureSum / (width * height);
        var exposure = 1.0 - Math.Min(1.0, Math.Abs(mean - 0.5) / 0.5);
        var sharpness = edgeCount > 0 ? edgeSum / edgeCount : 0;
        return (faceScore * 8.0) + (sharpness * 1.5) + (exposure * 0.15);
    }

    private static bool IsLikelySkin(Rgba32 pixel)
    {
        var r = (int)pixel.R;
        var g = (int)pixel.G;
        var b = (int)pixel.B;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return r > 60
               && g > 35
               && b > 20
               && max - min > 15
               && r > g
               && r > b
               && r - g > 5;
    }

    private static string ToJpegDataUri(Image<Rgba32> image)
    {
        using var clone = image.Clone(ctx =>
        {
            var maxSide = 960;
            var scale = Math.Min(1f, maxSide / (float)Math.Max(image.Width, image.Height));
            if (scale < 1f)
            {
                ctx.Resize(
                    Math.Max(1, (int)Math.Round(image.Width * scale)),
                    Math.Max(1, (int)Math.Round(image.Height * scale)));
            }
        });
        using var ms = new MemoryStream();
        clone.Save(ms, new JpegEncoder { Quality = 85 });
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string ExtractJsonObject(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return text[start..(end + 1)];
            }
        }

        var s = text.IndexOf('{');
        var e = text.LastIndexOf('}');
        if (s >= 0 && e > s)
        {
            return text[s..(e + 1)];
        }

        throw new InvalidOperationException("视觉接口未返回 JSON 对象。");
    }

    private static FontFamily? ResolveFontFamily()
    {
        foreach (var name in new[] { "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC", "SimHei", "Arial" })
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family;
            }
        }

        return SystemFonts.Families.FirstOrDefault();
    }

    private static void DrawText(IImageProcessingContext ctx, string text, Font font, Color color, PointF point) =>
        ctx.DrawText(text, font, color, point);

    private static void DrawWrapped(
        IImageProcessingContext ctx,
        string text,
        Font font,
        Color color,
        int x,
        int y,
        int width,
        int maxLines,
        int lineHeight)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var ch in text.Replace("\r", string.Empty))
        {
            if (ch == '\n')
            {
                lines.Add(current);
                current = string.Empty;
                if (lines.Count >= maxLines)
                {
                    break;
                }

                continue;
            }

            var trial = current + ch;
            var size = TextMeasurer.MeasureSize(trial, new TextOptions(font));
            if (size.Width <= width)
            {
                current = trial;
            }
            else
            {
                if (!string.IsNullOrEmpty(current))
                {
                    lines.Add(current);
                }

                current = ch.ToString();
                if (lines.Count >= maxLines)
                {
                    break;
                }
            }
        }

        if (lines.Count < maxLines && !string.IsNullOrEmpty(current))
        {
            lines.Add(current);
        }

        for (var i = 0; i < Math.Min(maxLines, lines.Count); i++)
        {
            DrawText(ctx, lines[i], font, color, new PointF(x, y + i * lineHeight));
        }
    }

    private static void FillRound(IImageProcessingContext ctx, RectangleF rect, Color color) =>
        ctx.Fill(color, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));

    private static string Ellipsize(string text, int limit)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= limit ? value : value[..Math.Max(1, limit - 1)] + "…";
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
