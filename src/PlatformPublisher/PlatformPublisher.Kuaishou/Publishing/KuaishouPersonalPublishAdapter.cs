using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalPublishAdapter : IPlatformPublishAdapter
{
    private readonly KuaishouPersonalSessionService _sessionService;
    public KuaishouPersonalPublishAdapter(KuaishouPersonalSessionService sessionService) => _sessionService = sessionService;

    public PublishPlatform Platform => PublishPlatform.KuaishouPersonalRevenue;
    public bool IsAvailable => true;
    public string AvailabilityMessage => "已接入个人版独立登录态；上传表单流程正在迁移";

    public async Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _sessionService.ValidateLoginAsync(job, progress, cancellationToken);
        throw new NotSupportedException("快手分账个人版登录态已验证；上传表单、封面和视频步骤将在下一阶段接入。");
    }

    public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken) =>
        _sessionService.OpenLoginAsync(job, cancellationToken);
}
