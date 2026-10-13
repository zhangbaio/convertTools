using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using ShortDrama.Core.Interfaces;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouCommitmentService
{
    private readonly IDocumentRenderService _documentRenderer;
    public KuaishouCommitmentService(IDocumentRenderService documentRenderer) => _documentRenderer = documentRenderer;

    public async Task<string> ResolveAsync(
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(data.CommitmentPdfPath) && File.Exists(data.CommitmentPdfPath))
            return data.CommitmentPdfPath;
        if (string.IsNullOrWhiteSpace(config.CommitmentTemplateDocxPath) || !File.Exists(config.CommitmentTemplateDocxPath))
            return data.CommitmentPdfPath;

        Directory.CreateDirectory(data.WorkflowDirectory);
        var outputDocx = Path.Combine(data.WorkflowDirectory, "快手承诺函.docx");
        File.Copy(config.CommitmentTemplateDocxPath, outputDocx, true);
        using (var document = WordprocessingDocument.Open(outputDocx, true))
        {
            var mainPart = document.MainDocumentPart
                           ?? throw new InvalidOperationException("承诺函模板缺少主文档。");
            var body = mainPart.Document.Body
                       ?? throw new InvalidOperationException("承诺函模板缺少正文。");
            var company = First(config.ProductionOrganization, config.KuaishouNickname, config.RealName, "承诺方");
            var recipient = First(config.CommitmentRecipientCompanyName, "北京晨钟科技有限公司");
            ReplaceAcrossParagraphs(body, "【北京晨钟科技有限公司】", $"【{recipient}】");
            ReplaceAcrossParagraphs(body, "【公司】", $"【{company}】");
            ReplaceAcrossParagraphs(body, "【剧名】", $"【{data.Title}】");
            if (!string.IsNullOrWhiteSpace(config.CommitmentSealPath) && File.Exists(config.CommitmentSealPath))
                AppendSeal(mainPart, body, config.CommitmentSealPath);
            mainPart.Document.Save();
        }

        var pdf = await _documentRenderer.ConvertDocxToPdfAsync(outputDocx, data.WorkflowDirectory, cancellationToken);
        if (!File.Exists(pdf)) throw new FileNotFoundException("承诺函 PDF 生成后不存在。", pdf);
        return pdf;
    }

    private static void ReplaceAcrossParagraphs(Body body, string oldValue, string newValue)
    {
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            var texts = paragraph.Descendants<Text>().ToArray();
            if (texts.Length == 0) continue;
            var combined = string.Concat(texts.Select(text => text.Text));
            if (!combined.Contains(oldValue, StringComparison.Ordinal)) continue;
            texts[0].Text = combined.Replace(oldValue, newValue, StringComparison.Ordinal);
            foreach (var text in texts.Skip(1)) text.Text = string.Empty;
        }
    }

    private static void AppendSeal(MainDocumentPart mainPart, Body body, string sealPath)
    {
        var imagePartType = Path.GetExtension(sealPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            _ => ImagePartType.Png,
        };
        var imagePart = mainPart.AddImagePart(imagePartType);
        using (var stream = File.OpenRead(sealPath)) imagePart.FeedData(stream);
        var relationshipId = mainPart.GetIdOfPart(imagePart);
        const long width = 1_600_000L;
        const long height = 1_600_000L;
        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = 1U, Name = "承诺函印章" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(sealPath) },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = width, Cy = height }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
        body.AppendChild(new Paragraph(new Run(drawing)));
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
