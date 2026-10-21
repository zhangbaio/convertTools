using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouEnterprisePublishAdapter : IPlatformPublishAdapter
{
    private readonly KuaishouPersonalSessionService _sessionService;
    private readonly KuaishouPersonalUploadService _uploadService;

    public KuaishouEnterprisePublishAdapter(
        KuaishouPersonalSessionService sessionService,
        KuaishouPersonalUploadService uploadService)
    {
        _sessionService = sessionService;
        _uploadService = uploadService;
    }

    public PublishPlatform Platform => PublishPlatform.KuaishouEnterpriseRevenue;
    public bool IsAvailable => true;
    public string AvailabilityMessage => "已接入企业版独立配置、登录会话、资料填写、分集上传、审核提交和断点续传";

    public async Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (job.Platform != PublishPlatform.KuaishouEnterpriseRevenue)
            throw new InvalidOperationException("企业版适配器收到非企业版任务。");
        await _uploadService.RunAsync(job, progress, cancellationToken);
    }

    public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken) =>
        _sessionService.OpenLoginAsync(job, cancellationToken);
}
