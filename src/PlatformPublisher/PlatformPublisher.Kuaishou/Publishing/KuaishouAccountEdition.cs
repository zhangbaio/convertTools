using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouAccountEdition
{
    public const string Personal = "personal";
    public const string Enterprise = "enterprise";

    public static bool TryGetPlatform(string? edition, out PublishPlatform platform)
    {
        if (string.Equals(edition, Enterprise, StringComparison.OrdinalIgnoreCase))
        {
            platform = PublishPlatform.KuaishouEnterpriseRevenue;
            return true;
        }

        if (string.Equals(edition, Personal, StringComparison.OrdinalIgnoreCase))
        {
            platform = PublishPlatform.KuaishouPersonalRevenue;
            return true;
        }

        platform = default;
        return false;
    }

    public static string Label(string? edition) =>
        string.Equals(edition, Enterprise, StringComparison.OrdinalIgnoreCase) ? "企业版"
        : string.Equals(edition, Personal, StringComparison.OrdinalIgnoreCase) ? "个人版"
        : "未设置";

    public static PublishPlatform Other(PublishPlatform platform) =>
        platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? PublishPlatform.KuaishouPersonalRevenue
            : PublishPlatform.KuaishouEnterpriseRevenue;
}

public interface IKuaishouAccountEditionCatalog
{
    IReadOnlyList<string> AccountIds { get; }

    /// <summary>账号不在目录里时返回 false。已知但未设置类型时返回 true，platform 为 null。</summary>
    bool TryGetEdition(string accountId, out PublishPlatform? platform);
}
