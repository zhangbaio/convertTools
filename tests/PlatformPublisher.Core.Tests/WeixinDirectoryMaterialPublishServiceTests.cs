using System.Text.Json;
using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Core.Tests;

public sealed class WeixinDirectoryMaterialPublishServiceTests
{
    [Fact]
    public void PrepareScansLargestVideosAndWritesConfigOutsideSourceDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "platform-publisher-material-tests", Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(tempRoot, "source");
        var dataRoot = Path.Combine(tempRoot, "isolated-data");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "第一条 #甜宠"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "第二条"));
        File.WriteAllBytes(Path.Combine(sourceRoot, "第一条 #甜宠", "small.mp4"), new byte[3]);
        File.WriteAllBytes(Path.Combine(sourceRoot, "第一条 #甜宠", "large.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(sourceRoot, "第二条", "video.mov"), new byte[5]);
        File.WriteAllText(Path.Combine(sourceRoot, "第二条", "description.txt"), "自定义描述#话题");

        try
        {
            var service = new WeixinDirectoryMaterialPublishService(new FakeUploader(), dataRoot);
            var plan = service.Prepare(new PublishJob
            {
                Id = "job-1",
                Kind = PublishJobKind.DirectoryMaterials,
                ProjectDirectory = sourceRoot,
                ProjectName = "素材目录",
                DeclareOriginal = false,
                HideLocation = false,
                AllowDuplicatePublish = true,
            });

            Assert.Equal(2, plan.Items.Count);
            Assert.EndsWith("large.mp4", plan.Items[0].VideoPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("自定义描述 #话题", plan.Items[1].Description);
            Assert.StartsWith(Path.GetFullPath(dataRoot), Path.GetFullPath(plan.ConfigPath), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(sourceRoot, "*.json", SearchOption.TopDirectoryOnly));

            using var document = JsonDocument.Parse(File.ReadAllText(plan.ConfigPath));
            var root = document.RootElement;
            Assert.Equal("publish_videos", root.GetProperty("task_type").GetString());
            Assert.Equal(2, root.GetProperty("video_publish").GetProperty("publish_count").GetInt32());
            var publish = root.GetProperty("video_publish");
            Assert.False(publish.GetProperty("declare_original").GetBoolean());
            Assert.Equal(string.Empty, publish.GetProperty("location_option_text").GetString());
            Assert.True(publish.GetProperty("allow_duplicate_publish").GetBoolean());
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
