using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinAdxMaterialPublishServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weixin-adx-publish-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedItemIsImmediatelyWrittenBackToItsManifest()
    {
        var workflow = Path.Combine(_root, "workflow");
        var batchDirectory = Path.Combine(workflow, "materials", "adx", "202609031200");
        Directory.CreateDirectory(batchDirectory);
        var video = Path.Combine(batchDirectory, "新剧-TOP001-123.mp4");
        await File.WriteAllTextAsync(video, "video");
        var batchStore = new AdxBatchStore();
        var manifestPath = Path.Combine(batchDirectory, AdxBatchStore.ManifestFileName);
        batchStore.Write(new AdxBatchManifest
        {
            BatchId = "202609031200", WorkflowDir = workflow, SeriesName = "新剧", NewTitle = "新剧",
            OriginalTitle = "原剧", CreatedAt = DateTimeOffset.UtcNow, ManifestPath = manifestPath,
            Items = [new AdxBatchItem { MaterialId = "123", Rank = 1, VideoPath = video }],
        });
        var uploader = new CompletingUploader();
        var local = new WeixinLocalVideoPublishService(uploader, Path.Combine(_root, "data"));
        var service = new WeixinAdxMaterialPublishService(uploader, local, batchStore);
        var payload = new AdxPublishPayload
        {
            OriginalTitle = "原剧", NewTitle = "新剧",
            PublishOptionsJson = new WeixinPublishOptions { EpisodeSelectionMode = "all", FinalAction = "draft" }.ToJson(),
            Items = [new AdxPublishItem("123", video, null, "描述", "短标题", manifestPath)],
        };
        var job = new PublishJob
        {
            Kind = PublishJobKind.AdxMaterials, ProjectDirectory = workflow, ProjectName = "新剧",
            AccountId = "account-1", AccountName = "账号1", PlatformOptionsJson = JsonSerializer.Serialize(payload),
        };

        await service.PublishAsync(job, null, CancellationToken.None);

        var status = batchStore.Read(manifestPath)!.PublishByAccount["account-1"].Items["123"];
        Assert.Equal("draft_saved", status.Status);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class CompletingUploader : IWeixinChannelUploader
    {
        public Task<WeixinUploadResult> UploadAsync(WeixinUploadRequest request, IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var video = JsonDocument.Parse(File.ReadAllText(request.ConfigPath!)).RootElement
                .GetProperty("video_publish").GetProperty("publish_video_custom_files")[0].GetString()!;
            request.MaterialItemCompleted?.Invoke(new(video, "draft_saved", "保存草稿完成", DateTimeOffset.UtcNow));
            return Task.FromResult(new WeixinUploadResult(true, request.ProjectDir, request.ConfigPath, "完成"));
        }
    }
}
