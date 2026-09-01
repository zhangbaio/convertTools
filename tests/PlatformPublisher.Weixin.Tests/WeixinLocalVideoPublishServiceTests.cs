using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinLocalVideoPublishServiceTests
{
    [Fact]
    public void ProjectMaterialsPrefersDirectoryWithMoreVideos()
    {
        var root = CreateTempRoot();
        try
        {
            var materials = Directory.CreateDirectory(Path.Combine(root, "material-videos")).FullName;
            var videos = Directory.CreateDirectory(Path.Combine(root, "videos")).FullName;
            File.WriteAllBytes(Path.Combine(materials, "1.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(materials, "2.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(videos, "1.mp4"), [1]);

            var files = WeixinLocalVideoPublishService.ResolveVideoFiles(new PublishJob
            {
                Kind = PublishJobKind.ProjectMaterials,
                ProjectDirectory = root,
            });

            Assert.Equal(2, files.Count);
            Assert.All(files, path => Assert.Contains("material-videos", path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomVideosGenerateIsolatedPublishConfiguration()
    {
        var root = CreateTempRoot();
        var dataRoot = Path.Combine(root, "isolated-data");
        try
        {
            var first = Path.Combine(root, "episode10.mp4");
            var second = Path.Combine(root, "episode2.mp4");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [1]);
            var service = new WeixinLocalVideoPublishService(new FakeUploader(), dataRoot);
            var advanced = new WeixinPublishOptions
            {
                DescriptionTemplate = "自定义发表描述 #短剧",
                EpisodeSelectionMode = "explicit",
                EpisodeIndexes = "2",
                FillShortTitle = true,
                ShortTitleMaxLength = 10,
                LinkOptionText = "视频号剧集",
                FinalAction = "draft",
            };

            var plan = service.Prepare(new PublishJob
            {
                Id = "custom-job",
                Kind = PublishJobKind.CustomVideos,
                ProjectDirectory = root,
                ProjectName = "自选素材",
                AccountId = "account-1",
                PublishCount = 2,
                CustomVideoFiles = [first, second],
                PlatformOptionsJson = advanced.ToJson(),
            });

            Assert.Equal("custom_files", plan.SourceMode);
            Assert.Equal(2, plan.ResolvedFiles.Count);
            Assert.EndsWith("episode2.mp4", plan.ResolvedFiles[0], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(dataRoot), Path.GetFullPath(plan.ConfigPath), StringComparison.OrdinalIgnoreCase);

            using var document = JsonDocument.Parse(File.ReadAllText(plan.ConfigPath));
            var publish = document.RootElement.GetProperty("video_publish");
            Assert.Equal("custom_files", publish.GetProperty("video_source_mode").GetString());
            Assert.Equal("自定义发表描述 #短剧", publish.GetProperty("description_template").GetString());
            Assert.Equal(2, publish.GetProperty("publish_video_custom_files").GetArrayLength());
            Assert.Equal(1, publish.GetProperty("publish_count").GetInt32());
            Assert.True(publish.GetProperty("fill_short_title").GetBoolean());
            Assert.Equal("视频号剧集", publish.GetProperty("link_option_text").GetString());
            Assert.Equal("draft", publish.GetProperty("final_action").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "weixin-local-video-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
