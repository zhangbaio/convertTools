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
                writer.Write(Fill(reader.ReadToEnd(), sheet));
            }
            else
            {
                source.CopyTo(target);
            }
        }
    }

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
