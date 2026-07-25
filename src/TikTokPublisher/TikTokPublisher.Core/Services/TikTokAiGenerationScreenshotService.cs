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
    public const string ScreenshotVersion = "v2-dedicated-folder";
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
        cancellationToken.ThrowIfCancellationRequested();

        var title = string.IsNullOrWhiteSpace(dramaTitle) ? "未命名短剧" : dramaTitle.Trim();
        // 先清掉旧版目录，再写入独立文件夹，避免与 workflow 根目录「工程图_*.png」混用。
        TryDeleteOutput(workflowProjectDirectory);
        var outputDir = GetOutputDirectory(workflowProjectDirectory);
        Directory.CreateDirectory(outputDir);
        log?.Invoke($"AI 截图/初始化：已清理旧产物；输出目录={outputDir}。");

        var pageCount = RequiredImageCount;
        var shotCount = pageCount * ShotsPerPage;
        var framePool = CollectFrames(workflowProjectDirectory, shotCount, log, cancellationToken);
        log?.Invoke(
            $"AI 截图/素材池：已准备 {framePool.Count} 张关键帧；" +
            $"分镜={shotCount} 个；每页={ShotsPerPage} 个；计划输出={pageCount} 页。");
        try
        {
            var analyses = AnalyzeShots(framePool, title, settings, log, cancellationToken);
            log?.Invoke($"AI 截图/分析：已完成 {analyses.Count} 个分镜描述。");
            var family = ResolveFontFamily()
                ?? throw new InvalidOperationException("未找到可用中文字体，无法生成 AI 生成过程截图。");

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
                var path = Path.Combine(outputDir, FileNames[page]);
                canvas.Save(path, new PngEncoder());
                outputs.Add(path);
            }

            log?.Invoke($"AI 生成过程截图已生成：{outputs.Count} 张 → {outputDir}");
            return outputs;
        }
        finally
        {
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
        var videos = EnumerateVideos(workflow).Take(12).ToArray();
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
                        var extracted = TryExtractFrame(ffmpeg, video, seconds, cancellationToken);
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

        while (frames.Count < requiredFrameCount)
        {
            frames.Add(frames[frames.Count % Math.Max(1, frames.Count)].Clone());
        }

        return frames;
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
        var count = Math.Max(RequiredImageCount * ShotsPerPage, frames.Count);
        var analyses = new List<ShotAnalysis>(count);
        var endpoint = (settings?.AiTextEndpoint ?? string.Empty).Trim().TrimEnd('/');
        var apiKey = (settings?.AiTextApiKey ?? string.Empty).Trim();
        var model = (settings?.AiTextModel ?? string.Empty).Trim();
        var canVision = !string.IsNullOrWhiteSpace(endpoint)
                        && !string.IsNullOrWhiteSpace(apiKey)
                        && !string.IsNullOrWhiteSpace(model);

        if (!canVision)
        {
            log?.Invoke("AI 截图：未配置火山文本/视觉模型，提示词使用本地兜底。");
        }

        for (var i = 0; i < count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!canVision)
            {
                analyses.Add(FallbackAnalysis(i, title));
                i++;
                continue;
            }

            var a = frames[(i * KeyframeRatios.Length + 2) % frames.Count];
            var b = frames[((i + 1) * KeyframeRatios.Length + 2) % frames.Count];
            try
            {
                var pair = AnalyzeShotPairAsync(endpoint, apiKey, model, a, b, settings!.AiTextTimeoutSeconds, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                analyses.Add(pair.Item1);
                if (i + 1 < count)
                {
                    analyses.Add(pair.Item2);
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"AI 截图：视觉反推失败（镜 {i + 1}），改用本地兜底：{ex.Message}");
                analyses.Add(FallbackAnalysis(i, title));
                if (i + 1 < count)
                {
                    analyses.Add(FallbackAnalysis(i + 1, title));
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
        }

        return analyses;
    }

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
        var hero = frames[Math.Min(2, frames.Count - 1)];
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
                ctx.Draw(i == 2 ? Color.ParseHex("16c0a8") : Color.ParseHex("1a2130"), 1.5f, new RectangleF(cx, cy, cellW, cellH));
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
        using var clone = source.Clone(ctx =>
        {
            var scale = Math.Max(w / (float)source.Width, h / (float)source.Height);
            var nw = Math.Max(1, (int)Math.Round(source.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(source.Height * scale));
            ctx.Resize(nw, nh);
            var left = Math.Max(0, (nw - w) / 2);
            var top = Math.Max(0, (nh - h) / 2);
            ctx.Crop(new Rectangle(left, top, w, h));
        });
        canvas.Mutate(ctx => ctx.DrawImage(clone, new Point(x, y), 1f));
    }

    private static List<Image<Rgba32>> CollectAssetImages(string workflow)
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
            candidates.AddRange(Directory.EnumerateFiles(workflow, "工程图_*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(TikTokProjectImageService.ListGeneratedImages(workflow));
            candidates.AddRange(Directory.EnumerateFiles(workflow, "*封面*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(Directory.EnumerateFiles(workflow, "*海报*.jpg", SearchOption.TopDirectoryOnly));
        }

        var parent = Directory.GetParent(workflow)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            candidates.AddRange(Directory.EnumerateFiles(parent, "*.png", SearchOption.TopDirectoryOnly).Take(8));
        }

        var images = new List<Image<Rgba32>>();
        foreach (var path in candidates
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                     .Take(16))
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

    private static IEnumerable<string> EnumerateVideos(string root)
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

        foreach (var sub in new[] { "videos", "视频", "成片", "源视频" })
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
