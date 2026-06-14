using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using ShortDrama.Infrastructure.Images;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Imaging;

internal static class CostReportImageRenderer
{
    private static readonly FontCollection BundledFontCollection = new();
    private static readonly Lazy<FontFamily?> BundledFontFamily = new(LoadBundledFontFamily);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<string> RenderAsync(
        CostReportBuildRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ValidateAssets(request.ConfigDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var configMap = LoadConfigMap(request.ConfigDir);
        var template = await TryLoadTemplateAsync(request.ConfigDir, configMap, cancellationToken);
        var sealPath = ReportImagePreprocessor.PrepareSealImage(Path.Combine(request.ConfigDir, "seal.png"));
        var signPath = ReportImagePreprocessor.PrepareSignImage(Path.Combine(request.ConfigDir, "sign.png"));

        try
        {
            if (template is not null)
            {
                return await RenderTemplateAsync(
                    request,
                    configMap,
                    template,
                    signPath,
                    sealPath,
                    outputPath,
                    cancellationToken);
            }

            return await RenderLegacyAsync(request, signPath, sealPath, outputPath, cancellationToken);
        }
        finally
        {
            TryDeleteTemporaryFile(sealPath);
            TryDeleteTemporaryFile(signPath);
        }
    }

    private static async Task<string> RenderTemplateAsync(
        CostReportBuildRequest request,
        IReadOnlyDictionary<string, string> configMap,
        CostReportTemplate template,
        string signPath,
        string sealPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgba32>(template.BaseImagePath, cancellationToken);
        var family = ResolveFontFamily() ?? throw new InvalidOperationException("未找到可用中文字体，无法生成成本报表图片。");

        if (image.Width != template.Layout.CanvasWidth || image.Height != template.Layout.CanvasHeight)
        {
            image.Mutate(ctx => ctx.Resize(template.Layout.CanvasWidth, template.Layout.CanvasHeight));
        }

        image.Mutate(ctx =>
        {
            foreach (var region in template.Layout.EraseRegions)
            {
                ctx.Fill(Color.White, new RectangleF(region.X, region.Y, region.Width, region.Height));
            }
        });

        var overlayData = BuildOverlayData(request, configMap);

        image.Mutate(ctx =>
        {
            DrawTemplateText(ctx, template.Layout.TextFields, "projectTitle", overlayData.ProjectTitle, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "duration", overlayData.DurationText, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "cost", overlayData.CostText, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "actorRatio", overlayData.ActorRatioText, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "companyName", overlayData.CompanyName, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "legalRepresentative", overlayData.LegalRepresentative, family);
            DrawTemplateText(ctx, template.Layout.TextFields, "reportDate", overlayData.ReportDate, family);
        });

        if (template.Layout.ImageFields.TryGetValue("seal", out var sealSpec))
        {
            using var seal = await Image.LoadAsync<Rgba32>(sealPath, cancellationToken);
            seal.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size((int)Math.Round(sealSpec.Width), (int)Math.Round(sealSpec.Height)),
                Mode = ResizeMode.Max
            }));

            image.Mutate(ctx => ctx.DrawImage(seal, new Point((int)Math.Round(sealSpec.X), (int)Math.Round(sealSpec.Y)), sealSpec.Opacity));
        }

        if (template.Layout.ImageFields.TryGetValue("sign", out var signSpec))
        {
            using var sign = await Image.LoadAsync<Rgba32>(signPath, cancellationToken);
            sign.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size((int)Math.Round(signSpec.Width), (int)Math.Round(signSpec.Height)),
                Mode = ResizeMode.Max
            }));

            image.Mutate(ctx => ctx.DrawImage(sign, new Point((int)Math.Round(signSpec.X), (int)Math.Round(signSpec.Y)), signSpec.Opacity));
        }

        await image.SaveAsync(outputPath, new PngEncoder(), cancellationToken);
        return outputPath;
    }

    private static void DrawTemplateText(
        IImageProcessingContext ctx,
        IReadOnlyDictionary<string, TextFieldSpec> fields,
        string key,
        string text,
        FontFamily family)
    {
        if (!fields.TryGetValue(key, out var spec) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var color = Color.Parse(spec.Color);
        var layout = LayoutText(text.Trim(), spec, family);
        foreach (var line in layout.Lines)
        {
            var bounds = MeasureTextBounds(line.Text, layout.Font);
            var x = spec.X + spec.PaddingLeft;
            if (spec.Align.Equals("center", StringComparison.OrdinalIgnoreCase))
            {
                x = Math.Max(spec.X + spec.PaddingLeft, spec.X + (spec.Width - bounds.Width) / 2f);
            }
            else if (spec.Align.Equals("right", StringComparison.OrdinalIgnoreCase))
            {
                x = Math.Max(spec.X + spec.PaddingLeft, spec.X + spec.Width - spec.PaddingRight - bounds.Width);
            }

            ctx.DrawText(line.Text, layout.Font, color, new PointF(x, line.Y - bounds.Top));
        }
    }

    private static TextLayoutResult LayoutText(string text, TextFieldSpec spec, FontFamily family)
    {
        var fallbackMin = Math.Min(spec.FontSize, spec.MinFontSize > 0 ? spec.MinFontSize : spec.FontSize);
        for (var fontSize = spec.FontSize; fontSize >= fallbackMin; fontSize -= 1f)
        {
            var font = CreateFont(family, fontSize, FontStyle.Regular);
            var lines = WrapText(text, font, Math.Max(8, spec.Width - spec.PaddingLeft - spec.PaddingRight));
            var lineHeight = MeasureText("测试", font).Height * 1.2f;
            var totalHeight = lines.Count * lineHeight;
            if (totalHeight > spec.Height)
            {
                continue;
            }

            var top = spec.Y + Math.Max(0, (spec.Height - totalHeight) / 2f);
            var positioned = new List<PositionedLine>(lines.Count);
            for (var index = 0; index < lines.Count; index++)
            {
                positioned.Add(new PositionedLine(lines[index], top + index * lineHeight));
            }

            return new TextLayoutResult(font, positioned);
        }

        var fallbackFont = CreateFont(family, fallbackMin, FontStyle.Regular);
        return new TextLayoutResult(fallbackFont, [new PositionedLine(text, spec.Y)]);
    }

    private static List<string> WrapText(string text, Font font, float maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var current = string.Empty;
            foreach (var ch in paragraph)
            {
                var candidate = string.IsNullOrEmpty(current) ? ch.ToString() : current + ch;
                if (!string.IsNullOrEmpty(current) && MeasureText(candidate, font).Width > maxWidth)
                {
                    result.Add(current);
                    current = ch.ToString();
                }
                else
                {
                    current = candidate;
                }
            }

            if (!string.IsNullOrEmpty(current))
            {
                result.Add(current);
            }
            else if (paragraph.Length == 0)
            {
                result.Add(string.Empty);
            }
        }

        return result.Count == 0 ? [string.Empty] : result;
    }

    private static SizeF MeasureText(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SizeF(0, font.Size);
        }

        var bounds = MeasureTextBounds(text, font);
        return new SizeF(bounds.Width, bounds.Height);
    }

    private static FontRectangle MeasureTextBounds(string text, Font font)
    {
        return TextMeasurer.MeasureBounds(text, new TextOptions(font));
    }

    private static CostReportOverlayData BuildOverlayData(
        CostReportBuildRequest request,
        IReadOnlyDictionary<string, string> configMap)
    {
        var actorRatio = GetConfigValue(
            configMap,
            "CostReportActorPayRatio",
            "ActorPayRatio",
            "ActorPayRatioText");

        if (string.IsNullOrWhiteSpace(actorRatio))
        {
            actorRatio = "15%";
        }

        var legalRepresentative = GetConfigValue(
            configMap,
            "CostReportLegalRepresentative",
            "LegalRepresentative",
            "LegalRepresentativeOrEditor");

        var reportDate = GetConfigValue(configMap, "CostReportDate");
        if (string.IsNullOrWhiteSpace(reportDate))
        {
            var today = DateTime.Now;
            reportDate = $"{today:yyyy} 年 {today.Month} 月 {today.Day} 日";
        }

        return new CostReportOverlayData(
            request.Project.Title,
            $"{request.Project.EpisodeCount}集，共{request.Project.TotalMinutes}分钟",
            $"{request.Project.CostAmountWan:0.####}万元",
            actorRatio.Trim(),
            request.Project.CompanyName,
            legalRepresentative?.Trim() ?? string.Empty,
            reportDate.Trim());
    }

    private static IReadOnlyDictionary<string, string> LoadConfigMap(string configDir)
    {
        var configPath = Path.Combine(configDir, "config.txt");
        if (!File.Exists(configPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return KeyValueConfigReader.Read(configPath);
    }

    private static string? GetConfigValue(IReadOnlyDictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<CostReportTemplate?> TryLoadTemplateAsync(
        string configDir,
        IReadOnlyDictionary<string, string> configMap,
        CancellationToken cancellationToken)
    {
        var baseImagePath = ResolveTemplateResourcePath(
            configDir,
            configMap,
            configKeys: ["CostReportBaseImagePath", "CostReportBackgroundImagePath", "CostReportTemplateImagePath"],
            configFileName: "cost-report-base.png",
            bundledFileName: "cost-report-base.png");

        var layoutPath = ResolveTemplateResourcePath(
            configDir,
            configMap,
            configKeys: ["CostReportLayoutPath"],
            configFileName: "cost-report-layout.json",
            bundledFileName: "cost-report-layout.json");

        if (baseImagePath is null || layoutPath is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(layoutPath);
        var layout = await JsonSerializer.DeserializeAsync<CostReportLayout>(stream, JsonOptions, cancellationToken);
        if (layout is null)
        {
            return null;
        }

        return new CostReportTemplate(baseImagePath, layout);
    }

    private static string? ResolveTemplateResourcePath(
        string configDir,
        IReadOnlyDictionary<string, string> configMap,
        string[] configKeys,
        string configFileName,
        string bundledFileName)
    {
        foreach (var key in configKeys)
        {
            if (!configMap.TryGetValue(key, out var configured) || string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            var resolved = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(configDir, configured));
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        var configDefault = Path.Combine(configDir, configFileName);
        if (File.Exists(configDefault))
        {
            return configDefault;
        }

        foreach (var candidate in EnumerateBundledAssetCandidates(bundledFileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBundledAssetCandidates(string fileName)
    {
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "Assets", fileName);
            yield return Path.Combine(root, "src", "ShortDrama.Infrastructure", "Assets", fileName);
            yield return Path.Combine(root, "shortdrama", "src", "ShortDrama.Infrastructure", "Assets", fileName);
        }
    }

    private static async Task<string> RenderLegacyAsync(
        CostReportBuildRequest request,
        string signPath,
        string sealPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var image = new Image<Rgba32>(1600, 1200, Color.White);
        var family = ResolveFontFamily() ?? throw new InvalidOperationException("未找到可用中文字体，无法生成成本报表图片。");
        var titleFont = CreateFont(family, 42, FontStyle.Bold);
        var headingFont = CreateFont(family, 26, FontStyle.Bold);
        var bodyFont = CreateFont(family, 24, FontStyle.Regular);
        var smallFont = CreateFont(family, 20, FontStyle.Regular);

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White);
            ctx.DrawText("微短剧成本配置比例情况报告", titleFont, Color.Black, new PointF(420, 70));

            var tableX = 120f;
            var tableY = 190f;
            var tableWidth = 1360f;
            var rowHeight = 92f;
            var col1 = 260f;
            var col2 = tableWidth - col1;

            DrawLegacyCell(ctx, tableX, tableY, col1, rowHeight, "项目", headingFont, true);
            DrawLegacyCell(ctx, tableX + col1, tableY, col2, rowHeight, "内容", headingFont, true);

            DrawLegacyCell(ctx, tableX, tableY + rowHeight, col1, rowHeight, "微短剧名称", bodyFont, false);
            DrawLegacyCell(ctx, tableX + col1, tableY + rowHeight, col2, rowHeight, request.Project.Title, bodyFont, false);

            DrawLegacyCell(ctx, tableX, tableY + rowHeight * 2, col1, rowHeight, "集数和总时长", bodyFont, false);
            DrawLegacyCell(ctx, tableX + col1, tableY + rowHeight * 2, col2, rowHeight, $"{request.Project.EpisodeCount}集，共{request.Project.TotalMinutes}分钟", bodyFont, false);

            DrawLegacyCell(ctx, tableX, tableY + rowHeight * 3, col1, rowHeight, "总投资额", bodyFont, false);
            DrawLegacyCell(ctx, tableX + col1, tableY + rowHeight * 3, col2, rowHeight, $"{request.Project.CostAmountWan:0.####}万元", bodyFont, false);

            DrawLegacyCell(ctx, tableX, tableY + rowHeight * 4, col1, rowHeight, "报审机构", bodyFont, false);
            DrawLegacyCell(ctx, tableX + col1, tableY + rowHeight * 4, col2, rowHeight, request.Project.CompanyName, bodyFont, false);

            var noteY = tableY + rowHeight * 5 + 40;
            ctx.DrawText("说明：本图片由系统直接生成，用于提交“成本配置比例情况报告”材料。", smallFont, Color.Parse("#444444"), new PointF(tableX, noteY));
            ctx.DrawText("请确认剧名、时长、成本和报审机构信息无误。", smallFont, Color.Parse("#444444"), new PointF(tableX, noteY + 38));

            ctx.DrawText("报审机构（盖章）：", headingFont, Color.Black, new PointF(tableX, 860));
            ctx.DrawText(request.Project.CompanyName, bodyFont, Color.Black, new PointF(tableX + 240, 864));

            ctx.DrawText($"生成日期：{DateTime.Now:yyyy年MM月dd日}", bodyFont, Color.Black, new PointF(tableX, 1010));
        });

        using (var seal = await Image.LoadAsync<Rgba32>(sealPath, cancellationToken))
        using (var sign = await Image.LoadAsync<Rgba32>(signPath, cancellationToken))
        {
            seal.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(220, 220),
                Mode = ResizeMode.Max
            }));
            sign.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(260, 110),
                Mode = ResizeMode.Max
            }));

            image.Mutate(ctx =>
            {
                ctx.DrawImage(seal, new Point(1110, 760), 0.95f);
                ctx.DrawImage(sign, new Point(1130, 905), 1f);
            });
        }

        await image.SaveAsync(outputPath, new PngEncoder(), cancellationToken);
        return outputPath;
    }

    private static void DrawLegacyCell(
        IImageProcessingContext ctx,
        float x,
        float y,
        float width,
        float height,
        string text,
        Font font,
        bool header)
    {
        var fill = header ? Color.Parse("#F5F7FA") : Color.White;
        ctx.Fill(fill, new RectangleF(x, y, width, height));
        ctx.Draw(Color.Parse("#2F3542"), 2, new RectangleF(x, y, width, height));
        ctx.DrawText(text, font, Color.Black, new PointF(x + 18, y + (header ? 26 : 28)));
    }

    private static FontFamily? ResolveFontFamily()
    {
        string[] candidates =
        [
            "Songti SC", "STSong", "SimSun", "Source Han Serif SC",
            "Noto Serif CJK SC", "Heiti SC", "STHeiti", "Microsoft YaHei",
            "Noto Sans CJK SC", "Noto Sans SC", "WenQuanYi Micro Hei",
            "Arial Unicode MS", "PingFang SC", "Arial"
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
                // Try next candidate.
            }
        }

        return BundledFontFamily.Value;
    }

    private static Font CreateFont(FontFamily family, float size, FontStyle style)
    {
        try
        {
            return family.CreateFont(size, style);
        }
        catch
        {
            return family.CreateFont(size, FontStyle.Regular);
        }
    }

    private static FontFamily? LoadBundledFontFamily()
    {
        foreach (var path in EnumerateBundledFontCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return BundledFontCollection.Add(path);
            }
            catch
            {
                // Try next bundled font candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBundledFontCandidates()
    {
        var fontFiles = new[]
        {
            "HYKaiTiJ.ttf",
            "FZFSK.ttf",
            "HYQiHei-55J.ttf",
            "FZSSK.ttf"
        };

        foreach (var root in EnumerateSearchRoots())
        {
            var directFontsDir = Path.Combine(root, "tools", "fonts");
            foreach (var file in fontFiles)
            {
                yield return Path.Combine(directFontsDir, file);
                yield return Path.Combine(root, "shortdrama", "tools", "fonts", file);
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }

    private static void ValidateAssets(string configDir)
    {
        if (!Directory.Exists(configDir))
        {
            throw new DirectoryNotFoundException($"配置目录不存在: {configDir}");
        }

        var sealPath = Path.Combine(configDir, "seal.png");
        var signPath = Path.Combine(configDir, "sign.png");
        if (!File.Exists(sealPath))
        {
            throw new FileNotFoundException($"未找到盖章图片: {sealPath}");
        }

        if (!File.Exists(signPath))
        {
            throw new FileNotFoundException($"未找到签名图片: {signPath}");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private sealed record CostReportTemplate(string BaseImagePath, CostReportLayout Layout);

    private sealed record CostReportOverlayData(
        string ProjectTitle,
        string DurationText,
        string CostText,
        string ActorRatioText,
        string CompanyName,
        string LegalRepresentative,
        string ReportDate);

    private sealed class CostReportLayout
    {
        public int CanvasWidth { get; init; }
        public int CanvasHeight { get; init; }
        public List<RectangleSpec> EraseRegions { get; init; } = [];
        public Dictionary<string, TextFieldSpec> TextFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ImageFieldSpec> ImageFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private class RectangleSpec
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
    }

    private sealed class TextFieldSpec
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public float FontSize { get; init; }
        public float MinFontSize { get; init; }
        public string Color { get; init; } = "#000000";
        public string Align { get; init; } = "left";
        public float PaddingLeft { get; init; } = 0;
        public float PaddingRight { get; init; } = 0;
    }

    private sealed class ImageFieldSpec
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public float Opacity { get; init; } = 1f;
    }

    private sealed record PositionedLine(string Text, float Y);
    private sealed record TextLayoutResult(Font Font, List<PositionedLine> Lines);
}
