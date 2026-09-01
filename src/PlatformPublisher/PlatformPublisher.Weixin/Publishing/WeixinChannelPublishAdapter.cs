using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinChannelPublishAdapter : IPlatformPublishAdapter
{
    private readonly IWeixinChannelUploader _uploader;
    private readonly IWeixinBrowserSessionLauncher _browserSessionLauncher;
    private readonly WeixinDirectoryMaterialPublishService _directoryMaterialPublishService;
    private readonly WeixinSystemHighlightPublishService _systemHighlightPublishService;
    private readonly WeixinLocalVideoPublishService _localVideoPublishService;
    private readonly WeixinSeriesConfigOverrideService _seriesConfigOverrideService;

    public WeixinChannelPublishAdapter(
        IWeixinChannelUploader uploader,
        IWeixinBrowserSessionLauncher browserSessionLauncher,
        WeixinDirectoryMaterialPublishService directoryMaterialPublishService,
        WeixinSystemHighlightPublishService systemHighlightPublishService,
        WeixinLocalVideoPublishService localVideoPublishService,
        WeixinSeriesConfigOverrideService seriesConfigOverrideService)
    {
        _uploader = uploader;
        _browserSessionLauncher = browserSessionLauncher;
        _directoryMaterialPublishService = directoryMaterialPublishService;
        _systemHighlightPublishService = systemHighlightPublishService;
        _localVideoPublishService = localVideoPublishService;
        _seriesConfigOverrideService = seriesConfigOverrideService;
    }

    public PublishPlatform Platform => PublishPlatform.WeixinChannel;
    public bool IsAvailable => true;
    public string AvailabilityMessage => "已接入现有视频号剧集上传链路";

    public async Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ValidateProject(job);
        if (job.Kind == PublishJobKind.DirectoryMaterials)
        {
            await _directoryMaterialPublishService.PublishAsync(job, progress, cancellationToken);
            return;
        }

        if (job.Kind == PublishJobKind.SystemHighlight)
        {
            await _systemHighlightPublishService.PublishAsync(job, progress, cancellationToken);
            return;
        }

        if (job.Kind is PublishJobKind.ProjectMaterials or PublishJobKind.LocalVideos or PublishJobKind.CustomVideos)
        {
            await _localVideoPublishService.PublishAsync(job, progress, cancellationToken);
            return;
        }

        var overridePlan = _seriesConfigOverrideService.Prepare(job);
        var effectiveConfigPath = overridePlan?.OverrideConfigPath ?? NullIfWhiteSpace(job.ConfigPath);
        if (overridePlan is not null)
            progress?.Report($"剧集上传：已生成任务级配置，视频 {overridePlan.SelectedVideoCount}/{overridePlan.OriginalVideoCount}。 ");
        var result = await _uploader.UploadAsync(
            new WeixinUploadRequest(
                job.Id,
                job.ProjectDirectory,
                job.ProjectName,
                effectiveConfigPath,
                NullIfWhiteSpace(Path.GetFileName(effectiveConfigPath))),
            progress,
            cancellationToken);

        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "视频号上传失败。");
    }

    public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken)
    {
        ValidateProject(job);
        return _browserSessionLauncher.OpenHomeAsync(
            NullIfWhiteSpace(job.ConfigPath),
            job.ProjectDirectory,
            cancellationToken);
    }

    private static void ValidateProject(PublishJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ProjectDirectory) || !Directory.Exists(job.ProjectDirectory))
            throw new DirectoryNotFoundException($"项目目录不存在：{job.ProjectDirectory}");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
