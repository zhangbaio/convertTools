using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 生成 TikTok「原始文件或素材文件信息」所需的 4 张证明截图（模板合成，不依赖真实编辑器）。
/// </summary>
public static class TikTokSourceFileInfoScreenshotService
{
    /// <summary>独立子目录，避免与 workflow 根目录海报/工程图混放。</summary>
    public const string OutputDirectoryName = "原始文件信息截图";
    public const int RequiredImageCount = 4;
    public const string ScreenshotVersion = "v2-dedicated-folder";

    private const string LegacyOutputDirectoryName = "原始文件或素材文件信息";

    private static readonly string[] FileNames =
    [
        "01_角色素材台.png",
        "02_场景工程台.png",
        "03_素材资源目录.png",
        "04_剧本素材清单.png",
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

    public static IReadOnlyList<string> Generate(
        string workflowProjectDirectory,
        string dramaTitle,
        string? companyName = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowProjectDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var title = string.IsNullOrWhiteSpace(dramaTitle) ? "未命名短剧" : dramaTitle.Trim();
        var company = string.IsNullOrWhiteSpace(companyName) ? "制作方" : companyName.Trim();
        // 先清掉旧版目录，再写入独立文件夹。
        TryDeleteOutput(workflowProjectDirectory);
        var outputDir = GetOutputDirectory(workflowProjectDirectory);
        Directory.CreateDirectory(outputDir);

        var assets = CollectAssetImages(workflowProjectDirectory);
        try
        {
            log?.Invoke(
                assets.Count > 0
                    ? $"原始文件截图：已收集 {assets.Count} 张项目素材用于合成。"
                    : "原始文件截图：未找到项目图片，将使用占位色块合成。");

            var family = ResolveFontFamily()
                ?? throw new InvalidOperationException("未找到可用中文字体，无法生成原始文件或素材文件信息截图。");

            var outputs = new List<string>(RequiredImageCount);
            using var characterShot = RenderEditorShot(
                title,
                assets,
                family,
                mode: EditorShotMode.Characters,
                watermark: company);
            cancellationToken.ThrowIfCancellationRequested();
            var path1 = Path.Combine(outputDir, FileNames[0]);
            characterShot.Save(path1, new PngEncoder());
            outputs.Add(path1);

            using var sceneShot = RenderEditorShot(
                title,
                assets,
                family,
                mode: EditorShotMode.Scene,
                watermark: company);
            cancellationToken.ThrowIfCancellationRequested();
            var path2 = Path.Combine(outputDir, FileNames[1]);
            sceneShot.Save(path2, new PngEncoder());
            outputs.Add(path2);

            using var explorerShot = RenderExplorerShot(title, assets, family);
            cancellationToken.ThrowIfCancellationRequested();
            var path3 = Path.Combine(outputDir, FileNames[2]);
            explorerShot.Save(path3, new PngEncoder());
            outputs.Add(path3);

            using var docsShot = RenderDualDocumentShot(title, assets, family, company);
            cancellationToken.ThrowIfCancellationRequested();
            var path4 = Path.Combine(outputDir, FileNames[3]);
            docsShot.Save(path4, new PngEncoder());
            outputs.Add(path4);

            log?.Invoke($"原始文件信息截图已生成：{outputs.Count} 张 → {outputDir}");
            return outputs;
        }
        finally
        {
            foreach (var asset in assets)
            {
                asset.Dispose();
            }
        }
    }

    public static void TryDeleteOutput(string workflowProjectDirectory)
    {
        TryDeleteDirectory(GetOutputDirectory(workflowProjectDirectory));
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

    private enum EditorShotMode
    {
        Characters,
        Scene,
    }

    private static List<Image<Rgba32>> CollectAssetImages(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var candidates = new List<string>();
        void AddIfExists(string path)
        {
            if (File.Exists(path))
            {
                candidates.Add(path);
            }
        }

        AddIfExists(Path.Combine(workflow, "海报图片.png"));
        AddIfExists(Path.Combine(workflow, "海报.png"));
        if (Directory.Exists(workflow))
        {
            candidates.AddRange(
                Directory.EnumerateFiles(workflow, "工程图_*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(
                Directory.EnumerateFiles(workflow, "*封面*.png", SearchOption.TopDirectoryOnly));
            candidates.AddRange(
                Directory.EnumerateFiles(workflow, "*海报*.jpg", SearchOption.TopDirectoryOnly));
        }

        var parent = Directory.GetParent(workflow)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            candidates.AddRange(
                Directory.EnumerateFiles(parent, "*.png", SearchOption.TopDirectoryOnly).Take(8));
        }

        var images = new List<Image<Rgba32>>();
        foreach (var path in candidates
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                     .Take(12))
        {
            try
            {
                images.Add(Image.Load<Rgba32>(path));
            }
            catch
            {
                // skip unreadable assets
            }
        }

        return images;
    }

    private static Image<Rgba32> RenderEditorShot(
        string title,
        IReadOnlyList<Image<Rgba32>> assets,
        FontFamily family,
        EditorShotMode mode,
        string watermark)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height);
        var chrome = Color.ParseHex("2B2B2B");
        var panel = Color.ParseHex("3A3A3A");
        var stage = mode == EditorShotMode.Characters
            ? Color.ParseHex("2EC4B6")
            : Color.ParseHex("D9E4F0");
        var accent = Color.ParseHex("4C8DFF");

        image.Mutate(ctx =>
        {
            ctx.Fill(chrome);
            // left tools
            ctx.Fill(panel, new RectangleF(0, 36, 56, height - 36 - 150));
            // left layers
            ctx.Fill(panel, new RectangleF(56, 36, 220, height - 36 - 150));
            // right props
            ctx.Fill(panel, new RectangleF(width - 260, 36, 260, height - 36 - 150));
            // stage
            var stageRect = new RectangleF(286, 48, width - 286 - 270, height - 48 - 170);
            ctx.Fill(stage, stageRect);
            // timeline
            ctx.Fill(Color.ParseHex("222222"), new RectangleF(0, height - 150, width, 150));
            ctx.Fill(Color.ParseHex("1A1A1A"), new RectangleF(0, 0, width, 36));

            var titleFont = family.CreateFont(16, FontStyle.Regular);
            var smallFont = family.CreateFont(13, FontStyle.Regular);
            var tinyFont = family.CreateFont(11, FontStyle.Regular);
            ctx.DrawText($"场景 1  ·  {TrimForUi(title, 28)}", titleFont, Color.WhiteSmoke, new PointF(16, 8));
            ctx.DrawText("32%", tinyFont, Color.LightGray, new PointF(width - 70, 10));

            var layerNames = mode == EditorShotMode.Characters
                ? new[] { "角色_女主", "角色_男主", "服饰部件", "特效层", "背景参考" }
                : new[] { "背景", "建筑", "前景", "参考线", "灯光" };
            for (var i = 0; i < layerNames.Length; i++)
            {
                var y = 52 + i * 34;
                ctx.Fill(i % 2 == 0 ? Color.ParseHex("454545") : Color.ParseHex("3F3F3F"),
                    new RectangleF(64, y, 200, 30));
                ctx.DrawText($"图层 {i + 1}  {layerNames[i]}", smallFont, Color.WhiteSmoke, new PointF(72, y + 6));
            }

            for (var i = 0; i < 8; i++)
            {
                var y = 52 + i * 40;
                ctx.DrawText($"属性 {i + 1}", tinyFont, Color.Gainsboro, new PointF(width - 240, y));
                ctx.Fill(Color.ParseHex("555555"), new RectangleF(width - 240, y + 18, 200, 14));
            }

            // timeline ticks
            for (var frame = 0; frame <= 100; frame += 5)
            {
                var x = 280 + frame * 8;
                if (x > width - 40) break;
                ctx.DrawLine(Color.Gray, 1, new PointF(x, height - 128), new PointF(x, height - 118));
                if (frame % 10 == 0)
                {
                    ctx.DrawText(frame.ToString(), tinyFont, Color.Gray, new PointF(x - 6, height - 112));
                }
            }

            ctx.Fill(accent, new RectangleF(280, height - 96, 4, 70));
            ctx.DrawText("▶  ⏮  ⏭  🔁", smallFont, Color.WhiteSmoke, new PointF(width / 2f - 40, height - 140));

            if (mode == EditorShotMode.Scene)
            {
                // guide lines
                ctx.DrawLine(Color.ParseHex("4C8DFF"), 1,
                    new PointF(stageRect.X + 40, stageRect.Y + 20),
                    new PointF(stageRect.Right - 40, stageRect.Y + 20));
                ctx.DrawLine(Color.ParseHex("4C8DFF"), 1,
                    new PointF(stageRect.X + 40, stageRect.Y + 20),
                    new PointF(stageRect.X + 40, stageRect.Bottom - 20));
                ctx.DrawText($"{TrimForUi(title, 18)} · 场景原画", titleFont, Color.ParseHex("1F3A5F"),
                    new PointF(stageRect.X + 56, stageRect.Y + 28));
            }
        });

        PasteAssetsOntoStage(
            image,
            assets,
            new Rectangle(296, 60, width - 296 - 280, height - 60 - 190),
            mode == EditorShotMode.Characters ? 6 : 1,
            keepAspect: true);

        image.Mutate(ctx =>
        {
            var markFont = family.CreateFont(14, FontStyle.Regular);
            for (var y = 80; y < height - 160; y += 120)
            {
                for (var x = 320; x < width - 280; x += 260)
                {
                    ctx.DrawText(watermark, markFont, Color.FromRgba(255, 255, 255, 55), new PointF(x, y));
                }
            }
        });

        return image;
    }

    private static Image<Rgba32> RenderExplorerShot(
        string title,
        IReadOnlyList<Image<Rgba32>> assets,
        FontFamily family)
    {
        const int width = 1280;
        const int height = 800;
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("F3F3F3"));
            ctx.Fill(Color.White, new RectangleF(0, 0, width, 48));
            ctx.Fill(Color.ParseHex("EFEFEF"), new RectangleF(0, 48, 220, height - 48));
            ctx.Fill(Color.White, new RectangleF(220, 48, width - 220, height - 48));

            var titleFont = family.CreateFont(15, FontStyle.Regular);
            var smallFont = family.CreateFont(13, FontStyle.Regular);
            ctx.DrawText("文件资源管理器", titleFont, Color.Black, new PointF(16, 14));
            ctx.DrawText($"E:\\Projects\\{SanitizePathSegment(title)}\\素材", smallFont, Color.ParseHex("333333"),
                new PointF(240, 16));
            ctx.DrawText("新建  剪切  复制  粘贴  重命名  删除  排序  查看", smallFont, Color.ParseHex("444444"),
                new PointF(240, 56));

            string[] nav = ["主文件夹", "桌面", "下载", "文档", "图片", "视频", "此电脑"];
            for (var i = 0; i < nav.Length; i++)
            {
                ctx.DrawText(nav[i], smallFont, Color.ParseHex("222222"), new PointF(24, 80 + i * 36));
            }
        });

        var slots = new[]
        {
            new Rectangle(250, 110, 180, 180),
            new Rectangle(460, 110, 180, 180),
            new Rectangle(670, 110, 180, 180),
            new Rectangle(880, 110, 180, 180),
            new Rectangle(250, 360, 180, 180),
            new Rectangle(460, 360, 180, 180),
        };

        using var wordIcon = CreateDocIcon(family, "W", Color.ParseHex("2B579A"));
        using var certIcon = CreateDocIcon(family, "证", Color.ParseHex("C0392B"));
        var labels = new[]
        {
            $"{SanitizePathSegment(title)}_01.mp4",
            $"{SanitizePathSegment(title)}_海报.png",
            $"{SanitizePathSegment(title)}_剧本.docx",
            "授权合作协议.pdf",
            "角色原画",
            "raw_footage",
        };

        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (i < 2 && assets.Count > 0)
            {
                DrawThumb(image, assets[i % assets.Count], slot);
            }
            else if (i == 2)
            {
                DrawThumb(image, wordIcon, slot);
            }
            else if (i == 3)
            {
                DrawThumb(image, certIcon, slot);
            }
            else if (i == 4 && assets.Count > 1)
            {
                DrawThumb(image, assets[1 % assets.Count], slot);
            }
            else
            {
                image.Mutate(ctx =>
                {
                    ctx.Fill(Color.ParseHex("FFE9A8"), slot);
                    ctx.Draw(Color.ParseHex("D0A84A"), 2, slot);
                });
            }

            image.Mutate(ctx =>
            {
                var font = family.CreateFont(12, FontStyle.Regular);
                ctx.DrawText(TrimForUi(labels[i], 18), font, Color.Black,
                    new PointF(slot.X, slot.Bottom + 8));
            });
        }

        return image;
    }

    private static Image<Rgba32> RenderDualDocumentShot(
        string title,
        IReadOnlyList<Image<Rgba32>> assets,
        FontFamily family,
        string company)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("D8D8D8"));
            DrawWpsWindow(ctx, family, new Rectangle(20, 30, 690, 840), $"{title} · 剧本大纲.docx");
            DrawWpsWindow(ctx, family, new Rectangle(730, 30, 690, 840), $"{title} · 素材清单.docx");
        });

        var leftLines = BuildScriptLines(title);
        var rightLines = BuildInventoryLines(title, assets.Count, company);
        DrawDocumentBody(image, family, new Rectangle(40, 110, 520, 720), leftLines, Color.ParseHex("FFF0F5"));
        DrawDocumentBody(image, family, new Rectangle(750, 110, 520, 720), rightLines, Color.ParseHex("F0FFF4"));

        image.Mutate(ctx =>
        {
            var markFont = family.CreateFont(18, FontStyle.Regular);
            for (var y = 140; y < 800; y += 140)
            {
                ctx.DrawText(company, markFont, Color.FromRgba(120, 120, 120, 70), new PointF(120, y));
                ctx.DrawText(company, markFont, Color.FromRgba(120, 120, 120, 70), new PointF(860, y));
            }
        });

        return image;
    }

    private static void DrawWpsWindow(
        IImageProcessingContext ctx,
        FontFamily family,
        Rectangle bounds,
        string title)
    {
        ctx.Fill(Color.White, bounds);
        ctx.Fill(Color.ParseHex("F5F5F5"), new RectangleF(bounds.X, bounds.Y, bounds.Width, 70));
        ctx.Draw(Color.ParseHex("B0B0B0"), 1, bounds);
        var font = family.CreateFont(14, FontStyle.Regular);
        var tiny = family.CreateFont(12, FontStyle.Regular);
        ctx.DrawText("WPS 文字", font, Color.ParseHex("1F7A4D"), new PointF(bounds.X + 12, bounds.Y + 8));
        ctx.DrawText(TrimForUi(title, 34), tiny, Color.ParseHex("333333"), new PointF(bounds.X + 12, bounds.Y + 32));
        ctx.DrawText("文件  开始  插入  页面  审阅", tiny, Color.ParseHex("444444"),
            new PointF(bounds.X + 12, bounds.Y + 52));
        // style pane
        ctx.Fill(Color.ParseHex("FAFAFA"),
            new RectangleF(bounds.Right - 140, bounds.Y + 80, 120, bounds.Height - 100));
        ctx.DrawText("样式和格式", tiny, Color.ParseHex("333333"),
            new PointF(bounds.Right - 130, bounds.Y + 90));
    }

    private static void DrawDocumentBody(
        Image<Rgba32> image,
        FontFamily family,
        Rectangle bounds,
        IReadOnlyList<string> lines,
        Color highlight)
    {
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White, bounds);
            var font = family.CreateFont(13, FontStyle.Regular);
            var y = bounds.Y + 8f;
            for (var i = 0; i < lines.Count; i++)
            {
                if (y > bounds.Bottom - 20) break;
                if (i is 3 or 8 or 14)
                {
                    ctx.Fill(highlight, new RectangleF(bounds.X + 4, y - 2, bounds.Width - 8, 20));
                }

                ctx.DrawText(lines[i], font, Color.ParseHex("222222"), new PointF(bounds.X + 10, y));
                y += 22;
            }
        });
    }

    private static IReadOnlyList<string> BuildScriptLines(string title) =>
    [
        $"《{title}》剧本大纲",
        "第1集  开场",
        "场景：室内 / 日",
        "人物：女主、男主",
        "1. 女主推门进入，表情紧张。",
        "2. 男主回头，对白：你终于来了。",
        "3. 插入回忆闪回 3 秒。",
        "第2集  冲突",
        "场景：街道 / 夜",
        "人物：女主、配角A",
        "1. 争吵升级，镜头推近。",
        "2. 配角A递出关键证据。",
        "第3集  反转",
        "场景：天台 / 日",
        "1. 真相揭露。",
        "2. 情感高潮，音乐渐强。",
        "制作备注：保留 raw 对白轨，便于后期改写。",
    ];

    private static IReadOnlyList<string> BuildInventoryLines(string title, int assetCount, string company) =>
    [
        $"{title} · 素材文件清单",
        $"制作方：{company}",
        "序号  文件名                         大小      修改时间",
        $"01    {SanitizePathSegment(title)}_01.mp4     186MB   2026-07-01",
        $"02    {SanitizePathSegment(title)}_02.mp4     192MB   2026-07-01",
        $"03    {SanitizePathSegment(title)}_剧本.docx   1.2MB   2026-07-02",
        "04    character_main.ai             48MB    2026-06-28",
        "05    scene_palace.psd              120MB   2026-06-29",
        "06    bgm_theme.wav                 22MB    2026-06-30",
        $"07    工程图素材包 ({Math.Max(assetCount, 4)}张)     —       2026-07-03",
        "08    raw/A001_C001.mov             2.1GB   2026-06-27",
        "09    raw/A001_C002.mov             1.8GB   2026-06-27",
        "10    授权合作协议.pdf               860KB   2026-07-04",
        "校验：文件信息完整，可用于版权辅助材料提交。",
    ];

    private static void PasteAssetsOntoStage(
        Image<Rgba32> canvas,
        IReadOnlyList<Image<Rgba32>> assets,
        Rectangle stage,
        int maxCount,
        bool keepAspect)
    {
        if (assets.Count == 0)
        {
            canvas.Mutate(ctx =>
            {
                for (var i = 0; i < Math.Min(maxCount, 4); i++)
                {
                    var color = Color.FromRgb((byte)(80 + i * 30), (byte)(60 + i * 20), (byte)(120 + i * 25));
                    var rect = new RectangleF(
                        stage.X + 30 + (i % 3) * 220,
                        stage.Y + 40 + (i / 3) * 260,
                        180,
                        220);
                    ctx.Fill(color, rect);
                    ctx.Draw(Color.White, 2, rect);
                }
            });
            return;
        }

        var count = Math.Min(maxCount, Math.Max(1, assets.Count));
        for (var i = 0; i < count; i++)
        {
            var cols = modeCols(count);
            var cellW = stage.Width / cols;
            var cellH = stage.Height / ((count + cols - 1) / cols);
            var col = i % cols;
            var row = i / cols;
            var target = new Rectangle(
                stage.X + col * cellW + 16,
                stage.Y + row * cellH + 16,
                Math.Max(80, cellW - 32),
                Math.Max(80, cellH - 32));
            DrawThumb(canvas, assets[i % assets.Count], target, keepAspect);
            if (i == 0)
            {
                canvas.Mutate(ctx => ctx.Draw(Color.White, 3, target));
            }
        }

        static int modeCols(int n) => n <= 1 ? 1 : n <= 4 ? 2 : 3;
    }

    private static void DrawThumb(
        Image<Rgba32> canvas,
        Image<Rgba32> source,
        Rectangle target,
        bool keepAspect = true)
    {
        using var clone = source.Clone(ctx =>
        {
            if (keepAspect)
            {
                ctx.Resize(new ResizeOptions
                {
                    Size = new Size(target.Width, target.Height),
                    Mode = ResizeMode.Crop,
                });
            }
            else
            {
                ctx.Resize(target.Width, target.Height);
            }
        });
        canvas.Mutate(ctx => ctx.DrawImage(clone, new Point(target.X, target.Y), 1f));
    }

    private static Image<Rgba32> CreateDocIcon(FontFamily family, string glyph, Color color)
    {
        var image = new Image<Rgba32>(160, 160);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White);
            ctx.Fill(color, new RectangleF(30, 20, 100, 120));
            var font = family.CreateFont(48, FontStyle.Bold);
            ctx.DrawText(glyph, font, Color.White, new PointF(52, 50));
        });
        return image;
    }

    private static FontFamily? ResolveFontFamily()
    {
        string[] candidates =
        [
            "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun",
            "PingFang SC", "Noto Sans CJK SC", "Noto Sans SC", "Arial Unicode MS", "Arial",
        ];
        foreach (var name in candidates)
        {
            if (!SystemFonts.TryGet(name, out var family))
            {
                continue;
            }

            try
            {
                var probe = family.CreateFont(12, FontStyle.Regular);
                TextMeasurer.MeasureBounds("已", new TextOptions(probe));
                return family;
            }
            catch
            {
                // try next
            }
        }

        return SystemFonts.Families.Any() ? SystemFonts.Families.First() : null;
    }

    private static string TrimForUi(string text, int maxChars)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..Math.Max(1, maxChars - 1)] + "…";
    }

    private static string SanitizePathSegment(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (text ?? string.Empty).Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "短剧项目" : cleaned;
    }
}
