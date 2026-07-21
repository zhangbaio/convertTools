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
    private static readonly Regex Hex32Pattern = Hex32Regex();

    public static string Normalize(string? value) => (value ?? string.Empty).Trim();

    public static bool LooksLikeGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GuidPattern.IsMatch(value.Trim());

    public static bool LooksLikeHex32(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Hex32Pattern.IsMatch(value.Trim());

    /// <param name="preferAes">
    /// true：1.4.x AES，读 HongGuoClient GUID；
    /// false：>=1.5.0 REST，只读 HongGuopy 32hex（不再回退 GUID）。
    /// </param>
    public static string? TryReadFromRegistry(bool preferAes = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // REST：仅 HongGuopy（GUID 只服务 1.4.x，不再给 1.5.0 用）
            string[] restOnly =
            [
                @"Software\HongGuopy",
                // @"Software\HongGuoClient",
                // @"Software\WOW6432Node\HongGuoClient"
            ];
            // AES / 1.4.x：GUID 优先
            string[] aesFirst =
            [
                @"Software\HongGuoClient",
                @"Software\WOW6432Node\HongGuoClient",
                // @"Software\HongGuopy" // 1.5.0 设备号，1.4.x 读取时不再回退
            ];

            foreach (var subKey in preferAes ? aesFirst : restOnly)
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, false)
                    ?? Registry.LocalMachine.OpenSubKey(subKey, false);
                var value = key?.GetValue("DeviceUDID")?.ToString();
                var normalized = Normalize(value);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                // 1.4.x 读取 HongGuoClient GUID 时统一成大写展示（保存时仍允许用户手改大小写）
                if (preferAes && LooksLikeGuid(normalized))
                {
                    return normalized.ToUpperInvariant();
                }

                return normalized;
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
