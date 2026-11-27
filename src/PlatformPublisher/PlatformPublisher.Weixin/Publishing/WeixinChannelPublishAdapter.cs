using System.Text.Json;
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
    private readonly WeixinAdxMaterialPublishService _adxMaterialPublishService;
    private readonly WeixinSeriesApiUploadService _seriesApi;

    public WeixinChannelPublishAdapter(
        IWeixinChannelUploader uploader,
        IWeixinBrowserSessionLauncher browserSessionLauncher,
        WeixinDirectoryMaterialPublishService directoryMaterialPublishService,
        WeixinSystemHighlightPublishService systemHighlightPublishService,
        WeixinLocalVideoPublishService localVideoPublishService,
        WeixinSeriesConfigOverrideService seriesConfigOverrideService,
        WeixinAdxMaterialPublishService adxMaterialPublishService,
        WeixinSeriesApiUploadService? seriesApi = null)
    {
        _uploader = uploader;
        _browserSessionLauncher = browserSessionLauncher;
        _directoryMaterialPublishService = directoryMaterialPublishService;
        _systemHighlightPublishService = systemHighlightPublishService;
        _localVideoPublishService = localVideoPublishService;
        _seriesConfigOverrideService = seriesConfigOverrideService;
        _adxMaterialPublishService = adxMaterialPublishService;
        _seriesApi = seriesApi ?? new WeixinSeriesApiUploadService();
    }

    public PublishPlatform Platform => PublishPlatform.WeixinChannel;
    public bool IsAvailable => true;
    public string AvailabilityMessage => "已接入现有视频号剧集上传链路";

    public Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        RunAsync(job, progress, cancellationToken, confirmSeriesReviewCompleteAsync: null);

    public async Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? confirmSeriesReviewCompleteAsync)
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

        if (job.Kind == PublishJobKind.AdxMaterials)
        {
            await _adxMaterialPublishService.PublishAsync(job, progress, cancellationToken);
            return;
        }

        if (WeixinPublishOptions.FromJob(job).SeriesUploadMode != WeixinSeriesUploadMode.Browser)
        {
            var apiResult = await RunSeriesApiAsync(job, progress, confirm: null, cancellationToken);
            progress?.Report(DescribeApiResult(apiResult));
            return;
        }

        using var browser = _seriesApi.OpenBrowserRun(job);
        var overridePlan = _seriesConfigOverrideService.Prepare(job);
        if (confirmSeriesReviewCompleteAsync is not null && overridePlan is null)
            throw new InvalidOperationException("未找到可用的视频号剧集配置。");
        var effectiveConfigPath = overridePlan?.OverrideConfigPath ?? NullIfWhiteSpace(job.ConfigPath);
        if (overridePlan is not null)
            progress?.Report($"剧集上传：已生成任务级配置，视频 {overridePlan.SelectedVideoCount}/{overridePlan.OriginalVideoCount}。 ");
        var result = await _uploader.UploadAsync(
            new WeixinUploadRequest(
                job.Id,
                job.ProjectDirectory,
                job.ProjectName,
                effectiveConfigPath,
                NullIfWhiteSpace(Path.GetFileName(effectiveConfigPath)))
            {
                ConfirmSeriesReviewCompleteAsync = confirmSeriesReviewCompleteAsync,
            },
            progress,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "视频号上传失败。");
    }

    public Task<WeixinSeriesApiResult> RunSeriesApiAsync(PublishJob job, IProgress<string>? progress,
        Func<WeixinSeriesApiConfirmation, CancellationToken, Task<bool>>? confirm, CancellationToken cancellationToken)
    {
        ValidateProject(job);
        if (job.Kind != PublishJobKind.Series)
            throw new InvalidOperationException("接口上传仅用于视频号剧集上架。");
        if (!string.IsNullOrWhiteSpace(job.ConfigPath) && !File.Exists(job.ConfigPath))
            throw new InvalidOperationException("找不到选择的剧集配置。");
        var config = WeixinSeriesConfigOverrideService.ResolveSourceConfigPath(job)
            ?? throw new InvalidOperationException("未找到可用的视频号剧集配置。");
        return _seriesApi.RunAsync(job, config, WeixinPublishOptions.FromJob(job), progress, confirm, cancellationToken);
    }

    public static async Task<string> RequireQueuedSeriesConfigAsync(PublishJob job, CancellationToken cancellationToken)
    {
        var path = WeixinSeriesConfigOverrideService.ResolveSourceConfigPath(job)
            ?? throw new InvalidOperationException("未找到剧集配置，请先执行「素材校验」准备配置。");
        await using var stream = File.OpenRead(path);
        using var config = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (config.RootElement.TryGetProperty("task_type", out var taskType) &&
            string.Equals(taskType.GetString(), "publish_videos", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前项目配置用于素材发表，请选择视频号剧集项目。");
        return path;
    }

    public bool ResolveSeriesReview(string accountId, string projectDirectory, bool submitted) =>
        _seriesApi.ResolveReview(accountId, projectDirectory, submitted);

    public static string DescribeApiResult(WeixinSeriesApiResult result) => result switch
    {
        WeixinSeriesApiResult.Uploaded => "接口上传完成，素材回执已保存，尚未提审。",
        WeixinSeriesApiResult.Submitted => "平台已接收提审，请到视频号后台查看审核状态。",
        WeixinSeriesApiResult.AlreadySubmitted => "此项目已有提审记录，请到视频号后台查看审核状态。",
        WeixinSeriesApiResult.Cancelled => "已取消接口提审。",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

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
