using System.Globalization;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果新接口客户端版本分流（与 weixin-channel-tool hongguo_auth_service 对齐）。
/// AES：version &lt; 1.5.0（版本号原样进 URL，1.4.1/1.4.2 均可）；
/// REST：version &gt;= 1.5.0。
/// </summary>
public static class HongguoClientVersion
{
    public const string Default = "1.4.1";
    private static readonly Version RestMinVersion = new(1, 5, 0);
    private static readonly Version AesLineMinVersion = new(1, 4, 0);

    public static bool IsRest(string? clientVersion)
    {
        if (!TryParse(clientVersion, out var parsed))
        {
            return false;
        }

        return parsed >= RestMinVersion;
    }

    /// <summary>
    /// 解析设置中的版本：协议按阈值分流，版本号本身尽量原样透传。
    /// &gt;=1.5.0 REST 原样；&gt;=1.4.0 且 &lt;1.5.0 AES 原样；&lt;1.4.0（如 1.3.9）抬到 Default。
    /// </summary>
    public static string Normalize(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return Default;
        }

        var trimmed = clientVersion.Trim();
        if (!TryParse(trimmed, out var parsed))
        {
            return Default;
        }

        if (parsed >= RestMinVersion)
        {
            return trimmed;
        }

        if (parsed < AesLineMinVersion)
        {
            return Default;
        }

        return trimmed;
    }

    public static string BuildAesBaseUrl(string clientVersion) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "https://au.s1o.cc/api/user/1000/win/{0}",
            Normalize(clientVersion));

    private static bool TryParse(string? clientVersion, out Version parsed)
    {
        var parts = (clientVersion ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.All(char.IsDigit))
            .Take(4)
            .ToArray();
        if (parts.Length == 0)
        {
            parsed = new Version(0, 0);
            return false;
        }

        return Version.TryParse(string.Join('.', parts), out parsed!);
    }
}
