using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果设备号规范化。
/// 1.5.0 HongGuopy DeviceUDID：无连字符 32hex（必须小写）；
/// 1.3.x HongGuoClient DeviceUDID：GUID（大写）。
/// </summary>
public static partial class HongguoDeviceId
{
    private static readonly Regex GuidPattern = GuidRegex();
    private static readonly Regex Hex32Pattern = Hex32Regex();

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "";
        }

        if (GuidPattern.IsMatch(text))
        {
            return text.ToUpperInvariant();
        }

        if (Hex32Pattern.IsMatch(text))
        {
            return text.ToLowerInvariant();
        }

        return text;
    }

    public static bool LooksLikeGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GuidPattern.IsMatch(value.Trim());

    public static bool LooksLikeHex32(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Hex32Pattern.IsMatch(value.Trim());

    public static string? TryReadFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string[] subKeys =
            [
                @"Software\HongGuopy",
                @"Software\HongGuoClient",
                @"Software\WOW6432Node\HongGuoClient"
            ];

            foreach (var subKey in subKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, false)
                    ?? Registry.LocalMachine.OpenSubKey(subKey, false);
                var value = key?.GetValue("DeviceUDID")?.ToString();
                var normalized = Normalize(value);
                if (!string.IsNullOrWhiteSpace(normalized))
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

    [GeneratedRegex(@"^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex32Regex();
}
