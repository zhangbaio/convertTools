using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果设备号规范化。
/// 仅 Trim，不强制大小写——用户可按绑定设备原样保存大写 GUID 或小写 32hex。
/// </summary>
public static partial class HongguoDeviceId
{
    private static readonly Regex GuidPattern = GuidRegex();
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    public static bool LooksLikeGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GuidPattern.IsMatch(value.Trim());

    public static string? TryReadFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string[] registryPaths =
            [
                @"Software\HongGuoClient",
                @"Software\WOW6432Node\HongGuoClient"
            ];

            foreach (var subKey in registryPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, false)
                    ?? Registry.LocalMachine.OpenSubKey(subKey, false);
                var value = key?.GetValue("DeviceUDID")?.ToString();
                var normalized = Normalize(value);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (LooksLikeGuid(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
            // ignore registry errors
        }

        return null;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}
