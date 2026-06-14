using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Images;

namespace ShortDrama.Infrastructure.Office;

public sealed class OpenXmlCostReportTemplateService : ICostReportTemplateService
{
    private readonly ILogger<OpenXmlCostReportTemplateService> _logger;

    public OpenXmlCostReportTemplateService(ILogger<OpenXmlCostReportTemplateService> logger)
    {
        _logger = logger;
    }

    public async Task<string> BuildDocxAsync(CostReportBuildRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        Directory.CreateDirectory(request.OutputDir);

        var outputDocxPath = Path.Combine(request.OutputDir, "成本报表原文件.docx");
        PrepareWritableOutputPath(outputDocxPath);
        File.Copy(request.TemplateDocxPath, outputDocxPath, overwrite: true);
        EnsureWritableFile(outputDocxPath);

        var sealPath = ReportImagePreprocessor.PrepareSealImage(Path.Combine(request.ConfigDir, "seal.png"));
        var signPath = Path.Combine(request.ConfigDir, "sign.png");

        try
        {
            using var document = WordprocessingDocument.Open(outputDocxPath, true);
            var mainPart = document.MainDocumentPart ?? throw new InvalidOperationException("Word 文档缺少 MainDocumentPart。");

            ReplaceMainTableValues(mainPart, request.Project);
            ReplaceReportOrgText(mainPart, request.Project.CompanyName);
            RemoveApprovalPlaceholderText(mainPart);
            ReplaceEmbeddedImages(mainPart, sealPath, signPath);
            SubstituteFontsForLibreOffice(document);

            mainPart.Document.Save();

            _logger.LogInformation("Generated cost report docx: {Path}", outputDocxPath);
            await Task.CompletedTask;
            return outputDocxPath;
        }
        finally
        {
            TryDeleteTemporaryFile(sealPath);
        }
    }

    private static void ValidateRequest(CostReportBuildRequest request)
    {
        if (!File.Exists(request.TemplateDocxPath))
        {
            throw new FileNotFoundException($"模板不存在: {request.TemplateDocxPath}");
        }

        if (!Directory.Exists(request.ProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {request.ProjectDir}");
        }

        if (!Directory.Exists(request.ConfigDir))
        {
            throw new DirectoryNotFoundException($"配置目录不存在: {request.ConfigDir}");
        }

        var sealPath = Path.Combine(request.ConfigDir, "seal.png");
        var signPath = Path.Combine(request.ConfigDir, "sign.png");

        if (!File.Exists(sealPath))
        {
            throw new FileNotFoundException($"未找到盖章图片: {sealPath}");
        }

        if (!File.Exists(signPath))
        {
            throw new FileNotFoundException($"未找到签名图片: {signPath}");
        }

        if (request.Project.CostAmountWan >= 100m)
        {
            throw new InvalidOperationException($"总投资额必须小于100万元，当前为 {request.Project.CostAmountWan} 万元。");
        }
    }

    private static void PrepareWritableOutputPath(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        EnsureWritableFile(path);
        File.Delete(path);
    }

    private static void EnsureWritableFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);
        }
        catch (PlatformNotSupportedException)
        {
            // Ignore on platforms without Unix mode support.
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
            // Ignore cleanup failures for temporary image files.
        }
    }

    private static void ReplaceMainTableValues(MainDocumentPart mainPart, ProjectInfo project)
    {
        var table = FindCostReportTable(mainPart)
            ?? throw new InvalidOperationException("模板中未找到包含成本报表表头的主表格。");

        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count < 2)
        {
            throw new InvalidOperationException("模板主表格结构不符合预期。");
        }

        var headerRow = rows[0];
        var headerCells = headerRow.Elements<TableCell>().ToList();
        var headerMap = BuildHeaderIndexMap(headerCells);

        if (!headerMap.TryGetValue("微短剧名称", out var titleIndex) ||
            !headerMap.TryGetValue("集数和总时长", out var durationIndex) ||
            !headerMap.TryGetValue("总投资额", out var costIndex))
        {
            throw new InvalidOperationException("模板表头缺少必要列：微短剧名称 / 集数和总时长 / 总投资额。");
        }

        var dataRow = FindFirstDataRow(rows.Skip(1), new[] { titleIndex, durationIndex, costIndex })
            ?? throw new InvalidOperationException("模板中未找到可写入的成本报表数据行。");

        var cells = dataRow.Elements<TableCell>().ToList();

        SetCellText(cells[titleIndex], project.Title);
        SetCellText(cells[durationIndex], $"{project.EpisodeCount}集，共{project.TotalMinutes}分钟");
        SetCellText(cells[costIndex], $"{project.CostAmountWan:0.####}万元");
    }

    private static void ReplaceReportOrgText(MainDocumentPart mainPart, string companyName)
    {
        var paragraphs = mainPart.Document.Body?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>();

        foreach (var paragraph in paragraphs)
        {
            if (!paragraph.InnerText.Contains("报审机构：", StringComparison.Ordinal))
            {
                continue;
            }

            var runs = paragraph.Elements<Run>().ToList();
            var labelRunIndex = runs.FindIndex(run =>
                (run.InnerText ?? string.Empty).Contains("报审机构：", StringComparison.Ordinal));

            if (labelRunIndex < 0 || labelRunIndex + 1 >= runs.Count)
            {
                ReplaceParagraphTextByPattern(paragraph, "报审机构：", "（盖章）", companyName);
                return;
            }

            ReplaceRunTextPreserveStyle(runs[labelRunIndex + 1], companyName);
            return;
        }

        throw new InvalidOperationException("模板中未找到“报审机构”字段。");
    }

    private static void ReplaceEmbeddedImages(MainDocumentPart mainPart, string sealPath, string signPath)
    {
        var placements = CollectImagePlacements(mainPart);

        if (TryReplaceKnownRelationship(mainPart, "rId4", sealPath))
        {
            NormalizeImagePresentation(mainPart, "rId4", ImageKind.Seal);
        }

        if (TryReplaceKnownRelationship(mainPart, "rId5", signPath))
        {
            NormalizeImagePresentation(mainPart, "rId5", ImageKind.Sign);
        }

        if (TryResolveKnownRelationships(mainPart, placements))
        {
            return;
        }

        var anchorParagraph = FindParagraphContaining(mainPart, "报审机构：");
        var signPlacement = placements.FirstOrDefault(item =>
            item.ParagraphText.Contains("报审机构：", StringComparison.Ordinal));

        signPlacement ??= placements
            .Where(item => item.WidthEmu > 0 && item.HeightEmu > 0)
            .OrderByDescending(item => (double)item.WidthEmu / item.HeightEmu)
            .FirstOrDefault();

        var sealPlacement = placements.FirstOrDefault(item =>
            anchorParagraph is not null &&
            item.Paragraph == anchorParagraph.PreviousSibling<Paragraph>());

        sealPlacement ??= placements
            .Where(item => signPlacement is null || item.RelationshipId != signPlacement.RelationshipId)
            .OrderByDescending(item => item.WidthEmu * item.HeightEmu)
            .FirstOrDefault();

        if (sealPlacement is null || signPlacement is null)
        {
            throw new InvalidOperationException("模板中未找到可替换的签章图片位置。");
        }

        ReplaceImagePart(mainPart, sealPlacement.RelationshipId, sealPath);
        ReplaceImagePart(mainPart, signPlacement.RelationshipId, signPath);
        NormalizeImagePresentation(mainPart, sealPlacement.RelationshipId, ImageKind.Seal);
        NormalizeImagePresentation(mainPart, signPlacement.RelationshipId, ImageKind.Sign);
    }

    private static void RemoveApprovalPlaceholderText(MainDocumentPart mainPart)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["（盖章）"] = "\u3000\u3000\u3000\u3000",
            ["(盖章)"] = "\u3000\u3000\u3000\u3000",
            ["（文字）"] = "\u3000\u3000\u3000\u3000",
            ["(文字)"] = "\u3000\u3000\u3000\u3000",
            ["（签字）"] = "\u3000\u3000\u3000\u3000",
            ["(签字)"] = "\u3000\u3000\u3000\u3000"
        };

        foreach (var paragraph in mainPart.Document.Body?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>())
        {
            foreach (var text in paragraph.Descendants<Text>())
            {
                var value = text.Text ?? string.Empty;
                var cleaned = value;

                foreach (var placeholder in placeholders)
                {
                    cleaned = cleaned.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal);
                }

                if (!string.Equals(cleaned, value, StringComparison.Ordinal))
                {
                    text.Space = SpaceProcessingModeValues.Preserve;
                    text.Text = cleaned;
                }
            }
        }
    }

    private static void SubstituteFontsForLibreOffice(WordprocessingDocument document)
    {
        // Map: fonts that LibreOffice on macOS cannot find → installed WPS alternatives
        var fontMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["方正小标宋简体"] = "方正书宋_GBK",
            ["FZXiaoBiaoSong-B05S"] = "方正书宋_GBK",
            ["仿宋_GB2312"] = "方正仿宋_GBK",
            ["仿宋"] = "方正仿宋_GBK",
            ["FangSong_GB2312"] = "方正仿宋_GBK",
            ["FangSong"] = "方正仿宋_GBK",
            ["宋体"] = "方正书宋_GBK",
            ["SimSun"] = "方正书宋_GBK",
            ["NSimSun"] = "方正书宋_GBK",
            ["黑体"] = "汉仪旗黑-55简",
            ["SimHei"] = "汉仪旗黑-55简",
            ["楷体_GB2312"] = "汉仪楷体简",
            ["楷体"] = "汉仪楷体简",
            ["KaiTi"] = "汉仪楷体简",
        };

        // Replace in all RunFonts in the document body
        var mainPart = document.MainDocumentPart;
        if (mainPart is null) return;

        foreach (var runFonts in mainPart.Document.Body?.Descendants<RunFonts>() ?? Enumerable.Empty<RunFonts>())
        {
            SubstituteRunFonts(runFonts, fontMap);
        }

        // Also replace in styles (document default + named styles)
        var stylesPart = mainPart.StyleDefinitionsPart;
        if (stylesPart is not null)
        {
            foreach (var runFonts in stylesPart.Styles?.Descendants<RunFonts>() ?? Enumerable.Empty<RunFonts>())
            {
                SubstituteRunFonts(runFonts, fontMap);
            }
        }

        // Also replace in document defaults
        var settingsPart = mainPart.DocumentSettingsPart;
        // DocDefaults are in styles, already covered above.
    }

    private static void SubstituteRunFonts(RunFonts runFonts, IReadOnlyDictionary<string, string> fontMap)
    {
        if (runFonts.Ascii?.Value is { } ascii && fontMap.TryGetValue(ascii, out var asciiReplacement))
            runFonts.Ascii = new StringValue(asciiReplacement);

        if (runFonts.HighAnsi?.Value is { } highAnsi && fontMap.TryGetValue(highAnsi, out var highAnsiReplacement))
            runFonts.HighAnsi = new StringValue(highAnsiReplacement);

        if (runFonts.EastAsia?.Value is { } eastAsia && fontMap.TryGetValue(eastAsia, out var eastAsiaReplacement))
            runFonts.EastAsia = new StringValue(eastAsiaReplacement);

        if (runFonts.ComplexScript?.Value is { } cs && fontMap.TryGetValue(cs, out var csReplacement))
            runFonts.ComplexScript = new StringValue(csReplacement);
    }

    private static void ReplaceImagePart(MainDocumentPart mainPart, string relationshipId, string imagePath)
    {
        var existingPart = mainPart.GetPartById(relationshipId) as ImagePart
            ?? throw new InvalidOperationException($"未找到图片关系: {relationshipId}");

        var expectedContentType = ResolveImageContentType(imagePath);
        var part = existingPart.ContentType == expectedContentType
            ? existingPart
            : RecreateImagePart(mainPart, existingPart, relationshipId, expectedContentType);

        using var stream = File.OpenRead(imagePath);
        part.FeedData(stream);
    }

    private static ImagePart RecreateImagePart(
        MainDocumentPart mainPart,
        ImagePart existingPart,
        string relationshipId,
        string contentType)
    {
        mainPart.DeletePart(existingPart);
        return mainPart.AddImagePart(contentType, relationshipId);
    }

    private static string ResolveImageContentType(string imagePath)
    {
        return Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            _ => "image/png"
        };
    }

    private static bool TryReplaceKnownRelationship(MainDocumentPart mainPart, string relationshipId, string imagePath)
    {
        try
        {
            ReplaceImagePart(mainPart, relationshipId, imagePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveKnownRelationships(MainDocumentPart mainPart, IReadOnlyCollection<ImagePlacement> placements)
    {
        return placements.Any(item => item.RelationshipId == "rId4") &&
               placements.Any(item => item.RelationshipId == "rId5");
    }

    private static void SetCellText(TableCell cell, string value)
    {
        var paragraph = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
        var firstRun = paragraph.Elements<Run>().FirstOrDefault();

        if (firstRun is null)
        {
            paragraph.RemoveAllChildren();
            paragraph.AppendChild(CreateRunWithText(value, templateRun: null));
            return;
        }

        ReplaceRunTextPreserveStyle(firstRun, value);

        foreach (var extraRun in paragraph.Elements<Run>().Skip(1).ToList())
        {
            extraRun.Remove();
        }
    }

    private static void ReplaceRunTextPreserveStyle(Run run, string value)
    {
        var runProperties = CloneAndNormalizeRunProperties(run.RunProperties);
        run.RemoveAllChildren();

        if (runProperties is not null)
        {
            run.AppendChild(runProperties);
        }

        run.AppendChild(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Run CreateRunWithText(string value, Run? templateRun)
    {
        var run = new Run();
        var runProperties = CloneAndNormalizeRunProperties(templateRun?.RunProperties);
        if (runProperties is not null)
        {
            run.AppendChild(runProperties);
        }

        run.AppendChild(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static RunProperties? CloneAndNormalizeRunProperties(RunProperties? source)
    {
        if (source is null)
        {
            return null;
        }

        var cloned = (RunProperties)source.CloneNode(true);
        var fonts = cloned.RunFonts;
        if (fonts is not null)
        {
            var preferredFont = fonts.EastAsia?.Value
                ?? fonts.Ascii?.Value
                ?? fonts.HighAnsi?.Value
                ?? fonts.ComplexScript?.Value;

            if (!string.IsNullOrWhiteSpace(preferredFont))
            {
                fonts.EastAsia ??= new StringValue(preferredFont);
                fonts.Ascii ??= new StringValue(preferredFont);
                fonts.HighAnsi ??= new StringValue(preferredFont);
                fonts.ComplexScript ??= new StringValue(preferredFont);
            }
        }

        if (cloned.Color is null)
        {
            cloned.Color = new Color { Val = "000000" };
        }
        else
        {
            cloned.Color.Val = "000000";
        }

        if (cloned.FontSize is not null && cloned.FontSizeComplexScript is null)
        {
            cloned.FontSizeComplexScript = new FontSizeComplexScript
            {
                Val = cloned.FontSize.Val?.Value
            };
        }

        return cloned;
    }

    private static Table? FindCostReportTable(MainDocumentPart mainPart)
    {
        return mainPart.Document.Body?
            .Elements<Table>()
            .FirstOrDefault(table =>
            {
                var headerText = table.Elements<TableRow>().FirstOrDefault()?.InnerText ?? string.Empty;
                return headerText.Contains("微短剧名称", StringComparison.Ordinal) &&
                       headerText.Contains("集数和总时长", StringComparison.Ordinal) &&
                       headerText.Contains("总投资额", StringComparison.Ordinal);
            });
    }

    private static Dictionary<string, int> BuildHeaderIndexMap(IReadOnlyList<TableCell> headerCells)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < headerCells.Count; index++)
        {
            var text = headerCells[index].InnerText.Trim();

            if (text.Contains("微短剧名称", StringComparison.Ordinal))
            {
                result["微短剧名称"] = index;
            }

            if (text.Contains("集数和总时长", StringComparison.Ordinal))
            {
                result["集数和总时长"] = index;
            }

            if (text.Contains("总投资额", StringComparison.Ordinal))
            {
                result["总投资额"] = index;
            }
        }

        return result;
    }

    private static TableRow? FindFirstDataRow(IEnumerable<TableRow> rows, IReadOnlyList<int> requiredIndexes)
    {
        foreach (var row in rows)
        {
            var cells = row.Elements<TableCell>().ToList();
            if (requiredIndexes.Any(index => index >= cells.Count))
            {
                continue;
            }

            return row;
        }

        return null;
    }

    private static Paragraph? FindParagraphContaining(MainDocumentPart mainPart, string text)
    {
        return mainPart.Document.Body?
            .Descendants<Paragraph>()
            .FirstOrDefault(paragraph => paragraph.InnerText.Contains(text, StringComparison.Ordinal));
    }

    private static void ReplaceParagraphTextByPattern(
        Paragraph paragraph,
        string prefix,
        string suffix,
        string replacement)
    {
        var runs = paragraph.Elements<Run>().ToList();
        var suffixRunIndex = runs.FindIndex(run => (run.InnerText ?? string.Empty).Contains(suffix, StringComparison.Ordinal));

        if (suffixRunIndex < 0)
        {
            throw new InvalidOperationException("未找到“报审机构”后缀定位文本。");
        }

        var companyRuns = runs
            .SkipWhile(run => !(run.InnerText ?? string.Empty).Contains(prefix, StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(run => !(run.InnerText ?? string.Empty).Contains(suffix, StringComparison.Ordinal))
            .ToList();

        if (companyRuns.Count == 0)
        {
            throw new InvalidOperationException("未找到可替换的报审机构文本区间。");
        }

        var templateRun = companyRuns[0];
        ReplaceRunTextPreserveStyle(templateRun, replacement);

        foreach (var extraRun in companyRuns.Skip(1))
        {
            extraRun.Remove();
        }
    }

    private static List<ImagePlacement> CollectImagePlacements(MainDocumentPart mainPart)
    {
        var placements = new List<ImagePlacement>();

        foreach (var paragraph in mainPart.Document.Body?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>())
        {
            foreach (var drawing in paragraph.Descendants<Drawing>())
            {
                var relationshipId = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId))
                {
                    continue;
                }

                var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
                placements.Add(new ImagePlacement(
                    relationshipId,
                    paragraph,
                    paragraph.InnerText,
                    extent?.Cx?.Value ?? 0L,
                    extent?.Cy?.Value ?? 0L));
            }
        }

        return placements;
    }

    private static void NormalizeImagePresentation(
        MainDocumentPart mainPart,
        string relationshipId,
        ImageKind kind)
    {
        var drawings = mainPart.Document.Body?
            .Descendants<Drawing>()
            .Where(drawing => drawing.Descendants<A.Blip>().Any(blip => blip.Embed?.Value == relationshipId))
            .ToList()
            ?? [];

        foreach (var drawing in drawings)
        {
            foreach (var sourceRect in drawing.Descendants<A.SourceRectangle>().ToList())
            {
                sourceRect.Remove();
            }

            foreach (var blip in drawing.Descendants<A.Blip>())
            {
                // The template's original sign/seal images include color transforms
                // such as clrChange and lum that produce dirty backgrounds after replacement.
                blip.RemoveAllChildren();
                ApplyTransparentColor(blip);
            }

            NormalizeDrawingLayer(drawing, kind);
            var scale = kind == ImageKind.Seal ? 0.76 : 1.0;
            ResizeDrawing(drawing, scale);
        }
    }

    private static void NormalizeDrawingLayer(Drawing drawing, ImageKind kind)
    {
        foreach (var anchor in drawing.Descendants<DW.Anchor>())
        {
            anchor.BehindDoc = false;
            anchor.AllowOverlap = true;
            anchor.RelativeHeight = (UInt32Value)(kind == ImageKind.Seal ? 251700224U : 251600224U);

            if (kind == ImageKind.Sign)
            {
                MoveAnchorHorizontally(anchor, -420000L);
            }
        }
    }

    private static void MoveAnchorHorizontally(DW.Anchor anchor, long deltaEmu)
    {
        var position = anchor.HorizontalPosition;
        if (position?.PositionOffset is null)
        {
            return;
        }

        var currentText = position.PositionOffset.Text ?? "0";
        if (!long.TryParse(currentText, out var current))
        {
            return;
        }

        position.PositionOffset.Text = (current + deltaEmu).ToString();
    }

    private static void ApplyTransparentColor(A.Blip blip)
    {
        blip.AppendChild(new A.ColorChange(
            new A.RgbColorModelHex { Val = "FFFFFF" },
            new A.RgbColorModelHex { Val = "FFFFFF" })
        {
            UseAlpha = true
        });
    }

    private static void ResizeDrawing(Drawing drawing, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.0001)
        {
            return;
        }

        foreach (var extent in drawing.Descendants<DW.Extent>())
        {
            extent.Cx = ScaleEmu(extent.Cx?.Value ?? 0L, scale);
            extent.Cy = ScaleEmu(extent.Cy?.Value ?? 0L, scale);
        }

        foreach (var transform in drawing.Descendants<A.Transform2D>())
        {
            if (transform.Extents is null)
            {
                continue;
            }

            transform.Extents.Cx = ScaleEmu(transform.Extents.Cx?.Value ?? 0L, scale);
            transform.Extents.Cy = ScaleEmu(transform.Extents.Cy?.Value ?? 0L, scale);
        }

        foreach (var picture in drawing.Descendants<PIC.Picture>())
        {
            var shapeProperties = picture.ShapeProperties;
            var transform = shapeProperties?.Transform2D;

            if (transform?.Extents is null)
            {
                continue;
            }

            transform.Extents.Cx = ScaleEmu(transform.Extents.Cx?.Value ?? 0L, scale);
            transform.Extents.Cy = ScaleEmu(transform.Extents.Cy?.Value ?? 0L, scale);
        }
    }

    private static Int64Value ScaleEmu(long value, double scale)
    {
        return new Int64Value((long)Math.Round(value * scale, MidpointRounding.AwayFromZero));
    }

    private sealed record ImagePlacement(
        string RelationshipId,
        Paragraph Paragraph,
        string ParagraphText,
        long WidthEmu,
        long HeightEmu);

    private enum ImageKind
    {
        Seal,
        Sign
    }
}
