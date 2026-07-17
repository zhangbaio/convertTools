using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TikTokPublisher.Core.Licensing;

public static class MachineFingerprintHelper
{
    public static string GetMachineFingerprint()
    {
        var sources = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = key?.GetValue("MachineGuid")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(guid))
                    sources.Add(guid);
            }
            catch
            {
                // ignored
            }
        }

        sources.Add(Environment.MachineName);
        sources.Add(Environment.OSVersion.VersionString);
        var joined = string.Join("|", sources.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (joined.Length == 0)
            joined = "UNKNOWN-MACHINE";
        return Sha256Hex(joined);
    }

    public static string GetMachineFingerprintLegacy()
    {
        var sources = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = key?.GetValue("MachineGuid")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(guid))
                    sources.Add(guid);
            }
            catch
            {
                // ignored
            }
        }

        sources.Add(Environment.MachineName);
        var joined = string.Join("|", sources.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (joined.Length == 0)
            joined = "UNKNOWN-MACHINE";
        return Sha256Hex(joined);
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}
