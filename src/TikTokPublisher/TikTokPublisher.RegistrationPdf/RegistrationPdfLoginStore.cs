using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikTokPublisher.RegistrationPdf;

internal static class RegistrationPdfLoginStore
{
    private static readonly byte[] Magic = "REGPDF-LOGIN-V1\n"u8.ToArray();
    private static string FilePath => Path.Combine(RegistrationPdfPaths.DataRoot, "login.bin");

    public static RegistrationPdfLogin Load()
    {
        if (!File.Exists(FilePath))
            return new RegistrationPdfLogin("", "", "", "");

        try
        {
            var raw = File.ReadAllBytes(FilePath);
            if (raw.Length <= Magic.Length || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return new RegistrationPdfLogin("", "", "", "");
            var payload = Unprotect(raw.AsSpan(Magic.Length).ToArray());
            return JsonSerializer.Deserialize<RegistrationPdfLogin>(Encoding.UTF8.GetString(payload))
                   ?? new RegistrationPdfLogin("", "", "", "");
        }
        catch
        {
            return new RegistrationPdfLogin("", "", "", "");
        }
    }

    public static void Save(string serverUrl, string account, string password, string licenseCode)
    {
        Directory.CreateDirectory(RegistrationPdfPaths.DataRoot);
        var json = JsonSerializer.Serialize(new RegistrationPdfLogin(
            serverUrl.Trim().TrimEnd('/'),
            account.Trim(),
            password,
            licenseCode.Trim()));
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(json));
        using var stream = new MemoryStream();
        stream.Write(Magic);
        stream.Write(protectedBytes);
        File.WriteAllBytes(FilePath, stream.ToArray());
    }

    private static byte[] Protect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static byte[] Unprotect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
            : data;
}

internal sealed record RegistrationPdfLogin(
    string ServerUrl,
    string Account,
    string Password,
    string LicenseCode = "");
