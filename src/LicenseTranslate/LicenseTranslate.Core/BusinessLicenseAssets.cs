namespace LicenseTranslate.Core;

internal static class BusinessLicenseAssets
{
    public static byte[] Read(string fileName)
    {
        var name = "LicenseTranslate.Core.Templates." + fileName;
        using var stream = typeof(BusinessLicenseAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少执照版式资源：" + fileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
