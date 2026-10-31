using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LicenseTranslate.Auth;

public static class MachineFingerprint
{
    public static string Current()
    {
        var sources = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = key?.GetValue("MachineGuid")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(guid))
                    sources.Add(guid);
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        sources.Add(Environment.MachineName);
        sources.Add(Environment.OSVersion.VersionString);
        var joined = string.Join("|", sources.Where(item => !string.IsNullOrWhiteSpace(item)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined.Length == 0 ? "UNKNOWN-MACHINE" : joined));
        return Convert.ToHexString(hash);
    }
}
