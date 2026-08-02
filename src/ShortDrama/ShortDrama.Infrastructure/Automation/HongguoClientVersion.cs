using System.Globalization;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果新接口客户端版本分流（与 weixin-channel-tool hongguo_auth_service 对齐）。
/// 仅支持 1.4.x AES（版本号原样进入 URL）。
/// </summary>
public static class HongguoClientVersion
{
    public const string Default = "1.4.2";
    private static readonly Version AesLineMinVersion = new(1, 4, 0);
    private static readonly Version AesLineMaxVersion = new(1, 5, 0);

    /// <summary>
    /// 解析设置中的版本：协议按阈值分流，版本号本身尽量原样透传。
    /// 1.4.x 原样保留；其他版本统一回落到默认版本。
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

        if (parsed < AesLineMinVersion || parsed >= AesLineMaxVersion)
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
