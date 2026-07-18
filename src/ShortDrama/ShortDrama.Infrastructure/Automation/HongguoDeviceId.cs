using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果设备号规范化。
/// REST（>=1.5.0）HongGuopy DeviceUDID：无连字符 32hex（必须小写）；
/// AES（&lt;1.5.0）HongGuoClient DeviceUDID：GUID（大写）。
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

    /// <param name="preferAes">
    /// true：优先 HongGuoClient GUID（AES）；false/默认：优先 HongGuopy 32hex（REST）。
    /// </param>
    public static string? TryReadFromRegistry(bool preferAes = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string[] restFirst =
            [
                @"Software\HongGuopy",
                @"Software\HongGuoClient",
                @"Software\WOW6432Node\HongGuoClient"
            ];
            string[] aesFirst =
            [
                @"Software\HongGuoClient",
                @"Software\WOW6432Node\HongGuoClient",
                @"Software\HongGuopy"
            ];

            foreach (var subKey in preferAes ? aesFirst : restFirst)
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
