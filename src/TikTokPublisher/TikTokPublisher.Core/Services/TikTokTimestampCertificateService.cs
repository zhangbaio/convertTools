using System.Security.Cryptography;
using System.Text.Json;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// Ports the timestamp-certificate template renderer from weixin-channel-tool.
/// This reproduces that project's local certificate appearance; it does not call a TSA service.
/// </summary>
public static class TikTokTimestampCertificateService
{
    public const string OutputFileName = "可信时间戳认证证书.pdf";
    private const string Declaration = "本文件已经申请可信时间戳认证，{0}拥有该作品的著作权（包括但不限于发表权、署名权、修改权、保护作品完整权、复制权、发行权、出租权、展览权、表演权、放映权、广播权、信息网络传播权、摄制权、改编权、翻译权、汇编权）和法律授予的其他权利，未经授权，任何单位和个人禁止以任何方式使用本文件。";
    private static readonly object FontLock = new();
    private static bool _fontResolverInstalled;

    public static Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var output = Path.Combine(context.WorkflowProjectDir, OutputFileName);
        if (!forceRerun && File.Exists(output) && new FileInfo(output).Length > 100)
        {
            log?.Invoke($"已跳过可信时间戳：本地已存在 {OutputFileName}。");
            return Task.FromResult(output);
        }

        var assets = ResolveAssetsDirectory();
        var template = Path.Combine(assets, "tsa_certificate_template.pdf");
        var layoutPath = Path.Combine(assets, "tsa_certificate_layout.json");
        EnsureFontResolver(assets);

        var now = DateTimeOffset.Now;
        var title = FirstNonEmpty(item.NewTitle, item.Title, new DirectoryInfo(context.WorkflowProjectDir).Name.TrimStart('_'));
        var applicant = ResolveApplicant(context.WorkflowProjectDir, settings, account);
        var fields = new CertificateFields(
            applicant,
            now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss（UTC+8）"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            title,
            $"TSA-01-{now:yyyyMMdd}{RandomNumberGenerator.GetInt32(0, 1000000000):D9}{RandomNumberGenerator.GetInt32(0, 100):D2}");

        log?.Invoke($"生成可信时间戳：申请人 {fields.Applicant}，文件名称 {fields.FileName}，认证码 {fields.AuthCode[..8]}…");
        Render(template, layoutPath, output, fields, ct);
        log?.Invoke($"已生成可信时间戳：{output}（中英双页，模板版式）");
        return Task.FromResult(output);
    }

    internal static void Render(string template, string layoutPath, string output, CertificateFields fields, CancellationToken ct)
    {
        if (!File.Exists(template)) throw new FileNotFoundException("未找到可信时间戳模板 PDF。", template);
        if (!File.Exists(layoutPath)) throw new FileNotFoundException("未找到可信时间戳版式配置。", layoutPath);
        EnsureFontResolver(Path.GetDirectoryName(Path.GetFullPath(template))!);
        var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(layoutPath), JsonOptions)
                     ?? throw new InvalidDataException("可信时间戳版式配置无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        TikTokProofMaterialPdfRenderService.TryDelete(output);

        using var document = PdfReader.Open(template, PdfDocumentOpenMode.Modify);
        foreach (var pageSpec in layout.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (pageSpec.PageIndex < 0 || pageSpec.PageIndex >= document.PageCount) continue;
            var page = document.Pages[pageSpec.PageIndex];
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var fontFamily = pageSpec.Lang.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? TimestampFontResolver.SerifFamily
                : TimestampFontResolver.SansFamily;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["certificate_no"] = fields.CertificateNo,
                ["applicant"] = fields.Applicant,
                ["apply_time"] = fields.ApplyTime,
                ["auth_code"] = fields.AuthCode,
                ["file_name"] = fields.FileName,
                ["declaration"] = string.Format(Declaration, fields.Applicant),
            };

            if (pageSpec.Fields.TryGetValue("certificate_no", out var cert))
            {
                var erase = cert.EraseBoxPt ?? cert.BoxPt;
                graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(247, 250, 252)),
                    erase[0] - 1, erase[1] - 1, erase[2] - erase[0] + 2, erase[3] - erase[1] + 2);
            }

            foreach (var key in FieldOrder)
            {
                if (!pageSpec.Fields.TryGetValue(key, out var config)) continue;
                var text = key == "protection_type" ? config.Text ?? string.Empty : values.GetValueOrDefault(key) ?? string.Empty;
                if (text.Length == 0) continue;
                DrawField(graphics, config, text, fontFamily);
            }
        }
        document.Save(output);
    }

    private static void DrawField(XGraphics graphics, Field config, string text, string family)
    {
        var box = config.BoxPt;
        var size = config.FontPt <= 0 ? 12 : config.FontPt;
        var font = new XFont(family, size, XFontStyleEx.Regular);
        var rect = new XRect(box[0], box[1], box[2] - box[0], box[3] - box[1]);
        if (!string.Equals(config.FitBy, "wrap", StringComparison.OrdinalIgnoreCase) && rect.Height <= size * 2.2)
        {
            var measured = graphics.MeasureString(text, font).Width;
            if (measured > rect.Width && rect.Width > 0)
            {
                size = Math.Max(5, size * rect.Width / measured);
                font = new XFont(family, size, XFontStyleEx.Regular);
            }
            graphics.DrawString(text, font, XBrushes.Black, new XPoint(box[0], box[3] - Math.Max(0.6, size * .08)));
            return;
        }

        var lineStep = config.LineStepPt > 0 ? config.LineStepPt : size * 1.35;
        var lines = Wrap(graphics, text, font, rect.Width);
        for (var i = 0; i < lines.Count; i++)
            graphics.DrawString(lines[i], font, XBrushes.Black, new XPoint(rect.X, rect.Y + size + i * lineStep));
    }

    private static List<string> Wrap(XGraphics graphics, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    internal static string ResolveApplicant(
        string projectDir,
        ClientSettings settings,
        TikTokAccountProfile? account)
    {
        var infoPath = Path.Combine(projectDir, "短剧信息.txt");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(infoPath))
        {
            foreach (var line in File.ReadLines(infoPath))
            {
                var index = line.IndexOfAny(['：', ':']);
                if (index > 0) values[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }
        return FirstNonEmpty(account?.TiktokTimestampApplicantName,
            account?.TiktokProofDeclarantCompanyName,
            settings.TiktokProofDeclarantCompanyName,
            values.GetValueOrDefault("制作公司"), values.GetValueOrDefault("报审机构名称"),
            values.GetValueOrDefault("报审机构"), "未填写报审机构");
    }

    private static string ResolveAssetsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "timestamp-certificate"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TikTokPublisher.Core", "Resources", "TimestampCertificate")),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "TikTokPublisher", "TikTokPublisher.Core", "Resources", "TimestampCertificate"),
        };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "tsa_certificate_template.pdf")))
               ?? throw new DirectoryNotFoundException("未找到随程序附带的可信时间戳模板资源。");
    }

    private static void EnsureFontResolver(string assets)
    {
        lock (FontLock)
        {
            if (_fontResolverInstalled) return;
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new TimestampFontResolver(assets);
            _fontResolverInstalled = true;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static readonly string[] FieldOrder = ["certificate_no", "applicant", "apply_time", "auth_code", "protection_type", "file_name", "declaration"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal sealed record CertificateFields(string Applicant, string ApplyTime, string AuthCode, string FileName, string CertificateNo);
    internal sealed class Layout { public List<PageSpec> Pages { get; set; } = []; }
    internal sealed class PageSpec
    {
        public int PageIndex { get; set; }
        public string Lang { get; set; } = "zh";
        public Dictionary<string, Field> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    internal sealed class Field
    {
        public double[] BoxPt { get; set; } = [];
        public double[]? EraseBoxPt { get; set; }
        public double FontPt { get; set; }
        public double LineStepPt { get; set; }
        public string FitBy { get; set; } = string.Empty;
        public string? Text { get; set; }
    }

    private sealed class TimestampFontResolver(string assets) : IFontResolver
    {
        public const string SerifFamily = "TimestampNotoSerif";
        public const string SansFamily = "TimestampNotoSans";
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            familyName == SansFamily ? new FontResolverInfo("timestamp-sans") : new FontResolverInfo("timestamp-serif");
        // PDFsharp cannot embed the CFF outlines in the reference OTF reliably. The bundled
        // Noto Serif TTF covers the same full Chinese/Latin charset and keeps every field vector.
        public byte[]? GetFont(string faceName) =>
            File.ReadAllBytes(Path.Combine(assets, "NotoSerifSC-Light.ttf"));
    }
}
