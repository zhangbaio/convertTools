using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果设备号规范化。
/// 1.4.x 使用 HongGuoClient 的大写 GUID；旧版 1.5.x 的 HongGuopy 32hex 不能直接沿用。
/// </summary>
public static partial class HongguoDeviceId
{
    private static readonly Regex GuidPattern = GuidRegex();
    private static readonly Regex Hex32Pattern = Hex32Regex();

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    public static bool LooksLikeGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GuidPattern.IsMatch(value.Trim());

    public static bool LooksLikeLegacyHex32(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Hex32Pattern.IsMatch(value.Trim());

    public static string GetV14ValidationMessage(string? value) =>
        LooksLikeLegacyHex32(value)
            ? "检测到旧版 1.5.x 使用的 32 位设备标识，当前 1.4.x 不能直接沿用。请启动并登录红果 1.4.x 后点击“读取设备标识”；若生成新 GUID，需要先重新绑定账号。"
            : "设备唯一标识必须是 1.4.x 使用的带连字符大写 GUID。";

    public static string ResolveV14(string? configuredValue)
    {
        var normalized = Normalize(configuredValue);
        return LooksLikeGuid(normalized)
            ? normalized
            : TryReadFromRegistry() ?? normalized;
    }

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
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    var normalized = Normalize(TryReadRegistryValue(root, subKey));
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
        }
        catch
        {
            // ignore registry errors
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadRegistryValue(RegistryKey root, string subKey)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, false);
            return key?.GetValue("DeviceUDID")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex32Regex();
}
