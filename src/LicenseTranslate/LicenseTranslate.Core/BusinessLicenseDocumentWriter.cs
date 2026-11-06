using System.IO.Compression;
using System.Text;

namespace LicenseTranslate.Core;

public static class BusinessLicenseDocumentWriter
{
    public static void Write(string path, BusinessLicenseDraft draft)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var sheet = BusinessLicenseSheet.From(draft);
        using var templateStream = new MemoryStream(BusinessLicenseAssets.Read("license-template.docx"));
        using var template = new ZipArchive(templateStream, ZipArchiveMode.Read);
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var entry in template.Entries)
        {
            var created = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var source = entry.Open();
            using var target = created.Open();
            if (string.Equals(entry.FullName, "word/document.xml", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(source, Encoding.UTF8);
                using var writer = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(AddSeals(Fill(reader.ReadToEnd(), sheet)));
            }
            else if (string.Equals(entry.FullName, "word/_rels/document.xml.rels", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(source, Encoding.UTF8);
                using var writer = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(AddSealRelationships(reader.ReadToEnd()));
            }
            else
            {
                source.CopyTo(target);
            }
        }

        WriteEntry(zip, "word/media/seal-left.png", BusinessLicenseAssets.Read("seal_left.png"));
        WriteEntry(zip, "word/media/seal-right.png", BusinessLicenseAssets.Read("seal_right.png"));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var target = entry.Open();
        target.Write(bytes);
    }

    private static string AddSealRelationships(string xml)
    {
        const string relationships = "</Relationships>";
        var addition = """
            <Relationship Id="rIdSealLeft" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/seal-left.png"/><Relationship Id="rIdSealRight" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/seal-right.png"/>
            """;
        var index = xml.IndexOf(relationships, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("执照模板缺少图片关系。");
        return xml.Insert(index, addition);
    }

    private static string AddSeals(string xml)
    {
        const string section = "<w:sectPr>";
        var index = xml.IndexOf(section, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("执照模板缺少页面设置。");
        return xml.Insert(index, SealParagraph());
    }

    private static string SealParagraph() =>
        "<w:p><w:r>" + Seal("rIdSealLeft", 20, "Seal left", 286.9, 449.6, 114.5, 118) + "</w:r><w:r>"
        + Seal("rIdSealRight", 21, "Seal right", 408.5, 447.5, 114.3, 115) + "</w:r></w:p>";

    private static string Seal(string relationshipId, int id, string name, double x, double y, double width, double height)
    {
        var left = Emu(x);
        var top = Emu(y);
        var cx = Emu(width);
        var cy = Emu(height);
        return $"""
            <w:drawing><wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="251659264" behindDoc="0" locked="0" layoutInCell="0" allowOverlap="1"><wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="page"><wp:posOffset>{left}</wp:posOffset></wp:positionH><wp:positionV relativeFrom="page"><wp:posOffset>{top}</wp:posOffset></wp:positionV><wp:extent cx="{cx}" cy="{cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/><wp:wrapNone/><wp:docPr id="{id}" name="{name}"/><wp:cNvGraphicFramePr><a:graphicFrameLocks xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" noChangeAspect="1"/></wp:cNvGraphicFramePr><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="{id}" name="{name}"/><pic:cNvPicPr><a:picLocks noChangeAspect="1"/></pic:cNvPicPr></pic:nvPicPr><pic:blipFill><a:blip r:embed="{relationshipId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:anchor></w:drawing>
            """;
    }

    private static long Emu(double points) => (long)Math.Round(points * 12700d);

    public static string OutputPath(BusinessLicenseDraft draft, string extension)
    {
        var folder = Path.GetDirectoryName(draft.SourcePath) ?? "";
        var person = string.IsNullOrWhiteSpace(draft.PersonName) ? Path.GetFileNameWithoutExtension(draft.SourcePath) : draft.PersonName.Trim();
        var stem = "营业执照 " + person;
        var path = Path.Combine(folder, stem + extension);
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{stem}{index}{extension}");
            index++;
        }
        return path;
    }

    private static string Fill(string xml, BusinessLicenseSheet sheet)
    {
        xml = Token(xml, "{{CREDIT}}", sheet.Credit);
        xml = Token(xml, "{{NAME}}", sheet.Name);
        xml = Token(xml, "{{CAPITAL}}", sheet.ExtraValue);
        xml = Token(xml, "{{TYPE}}", sheet.Type);
        xml = Token(xml, "{{ESTABLISHED}}", sheet.Established);
        xml = Token(xml, "{{PERSON}}", sheet.Person);
        xml = Token(xml, "{{ADDRESS}}", sheet.Address);
        xml = Token(xml, "{{SCOPE}}", sheet.Scope);
        xml = Token(xml, "{{AUTHORITY}}", sheet.Authority);
        xml = Token(xml, "{{ISSUED}}", sheet.Issued);
        xml = Token(xml, "{{EXTRA_LABEL}}", sheet.ExtraLabel);
        xml = Token(xml, "{{DATE_LABEL}}", sheet.DateLabel);
        xml = Token(xml, "{{PERSON_LABEL}}", sheet.PersonLabel);
        xml = Token(xml, "{{ADDRESS_LABEL}}", sheet.AddressLabel);
        xml = Token(xml, "{{DUPLICATE}}", sheet.Duplicate);
        return xml;
    }

    private static string Token(string xml, string token, string value)
    {
        if (!xml.Contains(token, StringComparison.Ordinal))
            throw new InvalidOperationException("执照模板缺少 " + token);
        return xml.Replace(token, Xml(value), StringComparison.Ordinal);
    }

    private static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
