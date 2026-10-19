using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalPublishAdapter : IPlatformPublishAdapter
{
    private readonly KuaishouPersonalSessionService _sessionService;
    private readonly KuaishouPersonalUploadService _uploadService;
    private readonly KuaishouPersonalAdxPublishService _adxPublishService;
    public KuaishouPersonalPublishAdapter(
        KuaishouPersonalSessionService sessionService,
        KuaishouPersonalUploadService uploadService,
        KuaishouPersonalAdxPublishService adxPublishService)
    {
        _sessionService = sessionService;
        _uploadService = uploadService;
        _adxPublishService = adxPublishService;
    }

    public PublishPlatform Platform => PublishPlatform.KuaishouPersonalRevenue;
    public bool IsAvailable => true;
    public string AvailabilityMessage => "已接入个人版剧集上传、ADX 宣发素材发布、审核提交和断点续传";

    public async Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (job.Kind == PublishJobKind.AdxMaterials)
        {
            await _adxPublishService.PublishAsync(job, progress, cancellationToken);
            return;
        }
        await _uploadService.RunAsync(job, progress, cancellationToken);
    }

    public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken) =>
        _sessionService.OpenLoginAsync(job, cancellationToken);
}
