using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class UnavailableKuaishouPublishAdapter : IPlatformPublishAdapter
{
    public UnavailableKuaishouPublishAdapter(PublishPlatform platform)
    {
        if (platform is not (PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue))
            throw new ArgumentOutOfRangeException(nameof(platform));

        Platform = platform;
    }

    public PublishPlatform Platform { get; }
    public bool IsAvailable => false;
    public string AvailabilityMessage => "当前参考仓库没有快手分账自动化实现，需要接入原项目源码或完成页面流程采集。";

    public Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(AvailabilityMessage));

    public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(AvailabilityMessage));
}
