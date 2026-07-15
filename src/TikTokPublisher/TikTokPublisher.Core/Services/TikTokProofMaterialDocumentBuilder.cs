using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DrawingBlip = DocumentFormat.OpenXml.Drawing.Blip;

namespace TikTokPublisher.Core.Services;

public sealed class TikTokProofMaterialDocumentBuilder
{
    public const string TemplateCopyrightCompanyName = "武汉星漫光年科技有限公司";
    public const string TemplateDeclarantCompanyName = "武汉速视科技有限公司";
    public const string TemplateDramaTitle = "创业路上闺蜜反目维权";

    private const int ExpectedCopyrightCompanyMatches = 1;
    private const int ExpectedDeclarantCompanyMatches = 2;
    private const int ExpectedDramaTitleMatches = 1;
    private const int ExpectedStatementDateMatches = 1;

    private static readonly Regex StatementDateRegex = new(
        @"(?<!\d)\d{4}年【\d{1,2}】月【\d{1,2}】日",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public TikTokProofMaterialDocumentResult CreateTemporaryDocx(TikTokProofMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var workingRoot = ResolveWorkingRoot(request.TemporaryDirectory);
        var workingDirectory = Path.Combine(workingRoot, $"proof-material-{Guid.NewGuid():N}");
        var outputDocxPath = Path.Combine(workingDirectory, "证明材料.docx");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            CopyTemplateToWorkingDirectory(
                Path.GetFullPath(request.TemplateDocxPath),
                outputDocxPath);
            using var document = WordprocessingDocument.Open(outputDocxPath, true);
            var mainPart = document.MainDocumentPart
                ?? throw new InvalidDataException("证明材料模板缺少主文档部分。");
            var body = mainPart.Document?.Body
                ?? throw new InvalidDataException("证明材料模板缺少正文内容。");

            // Locate and validate every template slot before mutating any text. This avoids
            // a replacement value accidentally being mistaken for a later template marker.
            var copyrightMatches = FindExactMatches(body, TemplateCopyrightCompanyName);
            var declarantMatches = FindExactMatches(body, TemplateDeclarantCompanyName);
            var titleMatches = FindExactMatches(body, TemplateDramaTitle);
            var dateMatches = FindStatementDateMatches(body);
            EnsureExpectedCount("版权公司", ExpectedCopyrightCompanyMatches, copyrightMatches.Count);
            EnsureExpectedCount("本公司/声明人公司", ExpectedDeclarantCompanyMatches, declarantMatches.Count);
            EnsureExpectedCount("改写后剧名", ExpectedDramaTitleMatches, titleMatches.Count);
            EnsureExpectedCount("声明日期", ExpectedStatementDateMatches, dateMatches.Count);
            ApplyReplacementPlans(
            [
                .. copyrightMatches.Select(match => new ReplacementPlan(match, request.CopyrightCompanyName.Trim())),
                .. declarantMatches.Select(match => new ReplacementPlan(match, request.DeclarantCompanyName.Trim())),
                .. titleMatches.Select(match => new ReplacementPlan(match, request.DramaTitle.Trim())),
                .. dateMatches.Select(match => new ReplacementPlan(
                    match,
                    $"{request.StatementDate.Year}年【{request.StatementDate.Month}】月【{request.StatementDate.Day}】日")),
            ]);
            var sealCount = string.IsNullOrWhiteSpace(request.SealImagePath)
                ? 0
                : ReplaceSealImage(mainPart, request.SealImagePath);

            mainPart.Document.Save();
            return new TikTokProofMaterialDocumentResult(
                outputDocxPath,
                workingDirectory,
                new TikTokProofMaterialReplacementCounts(
                    copyrightMatches.Count,
                    declarantMatches.Count,
                    titleMatches.Count,
                    dateMatches.Count,
                    sealCount));
        }
        catch
        {
            TryDeleteDirectory(workingDirectory);
            throw;
        }
    }

    private static void CopyTemplateToWorkingDirectory(string templatePath, string outputDocxPath)
    {
        try
        {
            File.Copy(templatePath, outputDocxPath, overwrite: false);
        }
        catch (IOException ex) when (IsFileSharingViolation(ex))
        {
            throw new IOException(
                $"证明材料 Word 模板正在被其他程序占用：{templatePath}。请关闭 WPS、Word 或其他占用该文件的程序后重试。",
                ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new DirectoryNotFoundException(
                $"证明材料 Word 模板所在目录不存在：{Path.GetDirectoryName(templatePath)}。",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"没有权限读取证明材料 Word 模板：{templatePath}。请检查文件权限后重试。",
                ex);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"复制证明材料 Word 模板失败：{templatePath}。请检查文件是否可读以及磁盘空间是否充足。",
                ex);
        }
    }

    private static bool IsFileSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void ValidateRequest(TikTokProofMaterialRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TemplateDocxPath))
        {
            throw new ArgumentException("证明材料 Word 模板路径不能为空。", nameof(request));
        }

        var templatePath = Path.GetFullPath(request.TemplateDocxPath);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("未找到证明材料 Word 模板。", templatePath);
        }

        if (!string.Equals(Path.GetExtension(templatePath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("证明材料模板必须是 .docx 文件。");
        }

        ValidateReplacementValue(request.CopyrightCompanyName, "版权公司");
        ValidateReplacementValue(request.DeclarantCompanyName, "本公司/声明人公司");
        ValidateReplacementValue(request.DramaTitle, "改写后剧名");

        if (string.IsNullOrWhiteSpace(request.SealImagePath) &&
            !string.Equals(
                request.DeclarantCompanyName.Trim(),
                TemplateDeclarantCompanyName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("声明公司与模板印章不一致，请配置印章。");
        }

        if (!string.IsNullOrWhiteSpace(request.SealImagePath))
        {
            var sealPath = Path.GetFullPath(request.SealImagePath);
            if (!File.Exists(sealPath))
            {
                throw new FileNotFoundException("未找到证明材料印章图片。", sealPath);
            }

            if (new FileInfo(sealPath).Length == 0)
            {
                throw new InvalidDataException("证明材料印章图片为空文件。");
            }
        }
    }

    private static void ValidateReplacementValue(string? value, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{displayName}不能为空。");
        }

        if (value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{displayName}不能包含换行符或空字符。");
        }
    }

    private static List<ParagraphMatch> FindExactMatches(OpenXmlElement root, string value) =>
        FindMatches(root, text => FindAllExact(text, value));

    private static List<ParagraphMatch> FindStatementDateMatches(OpenXmlElement root) =>
        FindMatches(
            root,
            text => StatementDateRegex.Matches(text)
                .Select(match => new MatchRange(match.Index, match.Index + match.Length))
                .ToArray());

    private static List<ParagraphMatch> FindMatches(
        OpenXmlElement root,
        Func<string, IReadOnlyList<MatchRange>> findInParagraph)
    {
        var matches = new List<ParagraphMatch>();
        foreach (var paragraph in root.Descendants<Paragraph>())
        {
            var paragraphText = string.Concat(paragraph.Descendants<Text>().Select(node => node.Text));
            foreach (var range in findInParagraph(paragraphText))
            {
                matches.Add(new ParagraphMatch(paragraph, range.Start, range.End));
            }
        }

        return matches;
    }

    private static IReadOnlyList<MatchRange> FindAllExact(string text, string value)
    {
        var ranges = new List<MatchRange>();
        var offset = 0;
        while (offset <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            ranges.Add(new MatchRange(index, index + value.Length));
            offset = index + value.Length;
        }

        return ranges;
    }

    private static void EnsureExpectedCount(string displayName, int expected, int actual)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"证明材料模板中的{displayName}应命中 {expected} 处，实际命中 {actual} 处；已停止生成以避免产生错误法律文档。");
        }
    }

    private static void ApplyReplacementPlans(IReadOnlyList<ReplacementPlan> plans)
    {
        foreach (var group in plans.GroupBy(plan => plan.Match.Paragraph))
        {
            foreach (var plan in group.OrderByDescending(item => item.Match.Start))
            {
                ReplaceTextRange(
                    plan.Match.Paragraph,
                    plan.Match.Start,
                    plan.Match.End,
                    plan.Replacement);
            }
        }
    }

    private static void ReplaceTextRange(Paragraph paragraph, int start, int end, string replacement)
    {
        var textNodes = paragraph.Descendants<Text>().ToArray();
        var segments = new List<TextSegment>(textNodes.Length);
        var offset = 0;
        foreach (var textNode in textNodes)
        {
            var value = textNode.Text ?? string.Empty;
            segments.Add(new TextSegment(textNode, offset, offset + value.Length));
            offset += value.Length;
        }

        var firstIndex = segments.FindIndex(segment => segment.Start <= start && start < segment.End);
        var lastIndex = segments.FindIndex(segment => segment.Start < end && end <= segment.End);
        if (firstIndex < 0 || lastIndex < 0)
        {
            throw new InvalidDataException("证明材料模板文本节点范围异常，无法安全替换。");
        }

        var first = segments[firstIndex];
        var last = segments[lastIndex];
        var firstText = first.Node.Text ?? string.Empty;
        var lastText = last.Node.Text ?? string.Empty;
        var prefix = firstText[..(start - first.Start)];
        var suffix = lastText[(end - last.Start)..];

        SetText(first.Node, prefix + replacement + (firstIndex == lastIndex ? suffix : string.Empty));
        for (var index = firstIndex + 1; index < lastIndex; index++)
        {
            SetText(segments[index].Node, string.Empty);
        }

        if (firstIndex != lastIndex)
        {
            SetText(last.Node, suffix);
        }
    }

    private static void SetText(Text node, string value)
    {
        node.Text = value;
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
        {
            node.Space = SpaceProcessingModeValues.Preserve;
        }
    }

    private static int ReplaceSealImage(MainDocumentPart mainPart, string sealImagePath)
    {
        var blips = mainPart.Document.Descendants<DrawingBlip>()
            .Where(blip => blip.Embed?.Value is not null)
            .ToArray();
        var relationshipIds = blips
            .Select(blip => blip.Embed!.Value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (relationshipIds.Length != 1)
        {
            throw new InvalidDataException(
                $"证明材料模板应包含 1 个可替换印章图片，实际找到 {relationshipIds.Length} 个图片关系。");
        }

        var relationshipId = relationshipIds[0];
        if (mainPart.GetPartById(relationshipId) is not ImagePart oldImagePart)
        {
            throw new InvalidDataException("证明材料模板的印章关系未指向有效图片。");
        }

        var imagePath = Path.GetFullPath(sealImagePath);
        var preparedImage = TikTokProofSealImageProcessor.Prepare(imagePath);
        var imageType = ResolveImagePartType(preparedImage.Extension);
        var sourceContentType = ResolveImageContentType(preparedImage.Extension);
        if (string.Equals(oldImagePart.ContentType, sourceContentType, StringComparison.OrdinalIgnoreCase))
        {
            using var source = new MemoryStream(preparedImage.Bytes, writable: false);
            oldImagePart.FeedData(source);
            return relationshipIds.Length;
        }

        var newImagePart = mainPart.AddImagePart(imageType);
        using (var source = new MemoryStream(preparedImage.Bytes, writable: false))
        {
            newImagePart.FeedData(source);
        }

        var newRelationshipId = mainPart.GetIdOfPart(newImagePart);
        foreach (var blip in blips.Where(blip => string.Equals(blip.Embed?.Value, relationshipId, StringComparison.Ordinal)))
        {
            blip.Embed = newRelationshipId;
        }

        mainPart.DeletePart(oldImagePart);
        return relationshipIds.Length;
    }

    private static PartTypeInfo ResolveImagePartType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImagePartType.Png,
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            ".gif" => ImagePartType.Gif,
            ".bmp" => ImagePartType.Bmp,
            ".tif" or ".tiff" => ImagePartType.Tiff,
            ".emf" => ImagePartType.Emf,
            ".wmf" => ImagePartType.Wmf,
            ".svg" => ImagePartType.Svg,
            _ => throw new InvalidDataException("印章图片格式不受支持，请使用 PNG、JPG、GIF、BMP、TIFF、EMF、WMF 或 SVG。"),
        };

    private static string ResolveImageContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".emf" => "image/x-emf",
            ".wmf" => "image/x-wmf",
            ".svg" => "image/svg+xml",
            _ => string.Empty,
        };

    private static string ResolveWorkingRoot(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        return Path.Combine(Path.GetTempPath(), "TikTokPublisher", "proof-material");
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for generated temporary files.
        }
    }

    private sealed record TextSegment(Text Node, int Start, int End);

    private sealed record MatchRange(int Start, int End);

    private sealed record ParagraphMatch(Paragraph Paragraph, int Start, int End);

    private sealed record ReplacementPlan(ParagraphMatch Match, string Replacement);
}
