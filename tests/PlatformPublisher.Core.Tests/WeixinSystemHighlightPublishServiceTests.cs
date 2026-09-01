using System.Text.Json;
using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Core.Tests;

public sealed class WeixinSystemHighlightPublishServiceTests
{
    [Fact]
    public void PrepareWritesIsolatedSystemHighlightConfiguration()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "platform-publisher-highlight-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var dataRoot = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(workspace);

        try
        {
            var service = new WeixinSystemHighlightPublishService(new FakeUploader(), dataRoot);
            var plan = service.Prepare(new PublishJob
            {
                Id = "highlight-job",
                Kind = PublishJobKind.SystemHighlight,
                ProjectDirectory = workspace,
                ProjectName = "测试新剧名",
                DramaTitle = "测试新剧名",
                AccountName = "高光账号",
                PublishCount = 3,
                PublishVideoTypes = "混剪,切片",
                RegenerateHighlightsAfterPublish = true,
            });

            Assert.Equal(3, plan.PublishCount);
            Assert.StartsWith(Path.GetFullPath(dataRoot), Path.GetFullPath(plan.ConfigPath), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories));

            using var document = JsonDocument.Parse(File.ReadAllText(plan.ConfigPath));
            var publish = document.RootElement.GetProperty("video_publish");
            Assert.Equal("system_highlight", publish.GetProperty("video_source_mode").GetString());
            Assert.Equal("测试新剧名", publish.GetProperty("system_highlight_drama_title").GetString());
            Assert.Equal(3, publish.GetProperty("publish_count").GetInt32());
            Assert.Equal(2, publish.GetProperty("system_highlight_publish_video_types").GetArrayLength());
            Assert.True(publish.GetProperty("system_highlight_regenerate_after_publish").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeUploader : IWeixinChannelUploader
    {
        public Task<WeixinUploadResult> UploadAsync(
            WeixinUploadRequest request,
            IProgress<string>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WeixinUploadResult(true, request.ProjectDir, request.ConfigPath));
    }
}
