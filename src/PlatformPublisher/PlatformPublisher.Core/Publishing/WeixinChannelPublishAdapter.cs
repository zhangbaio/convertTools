using PlatformPublisher.Core.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Core.Publishing;

public sealed class WeixinChannelPublishAdapter : IPlatformPublishAdapter
{
    private readonly IWeixinChannelUploader _uploader;
    private readonly IWeixinBrowserSessionLauncher _browserSessionLauncher;

    public WeixinChannelPublishAdapter(
        IWeixinChannelUploader uploader,
        IWeixinBrowserSessionLauncher browserSessionLauncher)
    {
        _uploader = uploader;
        _browserSessionLauncher = browserSessionLauncher;
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
        var result = await _uploader.UploadAsync(
            new WeixinUploadRequest(
                job.Id,
                job.ProjectDirectory,
                job.ProjectName,
                NullIfWhiteSpace(job.ConfigPath),
                NullIfWhiteSpace(Path.GetFileName(job.ConfigPath))),
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
