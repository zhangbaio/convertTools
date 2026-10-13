namespace PlatformPublisher.Common.Models;

public enum PublishPlatform
{
    WeixinChannel,
    KuaishouPersonalRevenue,
    KuaishouEnterpriseRevenue,
}

public static class PublishPlatformExtensions
{
    public static string DisplayName(this PublishPlatform platform) => platform switch
    {
        PublishPlatform.WeixinChannel => "视频号",
        PublishPlatform.KuaishouPersonalRevenue => "快手分账 · 个人",
        PublishPlatform.KuaishouEnterpriseRevenue => "快手分账 · 企业",
        _ => platform.ToString(),
    };
}
