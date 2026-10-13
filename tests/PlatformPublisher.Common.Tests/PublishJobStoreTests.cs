using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using Xunit;

namespace PlatformPublisher.Common.Tests;

public sealed class PublishJobStoreTests
{
    [Fact]
    public async Task SaveAndLoadPreservesIndependentPlatformJobs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "platform-publisher-tests", Guid.NewGuid().ToString("N"));
        var store = new PublishJobStore(Path.Combine(tempRoot, "jobs.json"));
        try
        {
            var jobs = new[]
            {
                new PublishJob
                {
                    Id = "weixin-job",
                    Platform = PublishPlatform.WeixinChannel,
                    ProjectName = "测试剧",
                    ProjectDirectory = Path.Combine(tempRoot, "project"),
                    IsChecked = true,
                    StepStates = new Dictionary<string, PublishJobStepState>
                    {
                        ["transcode"] = new()
                        {
                            Key = "transcode",
                            Label = "素材转码",
                            Status = PublishJobStepStatus.Failed,
                            Message = "测试失败",
                        },
                    },
                    AttemptCount = 3,
                    LastStartedAt = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.FromHours(8)),
                    LastCompletedAt = new DateTimeOffset(2026, 9, 1, 20, 5, 0, TimeSpan.FromHours(8)),
                },
                new PublishJob
                {
                    Id = "kuaishou-personal-job",
                    Platform = PublishPlatform.KuaishouPersonalRevenue,
                    ProjectName = "测试剧",
                    ProjectDirectory = Path.Combine(tempRoot, "project"),
                    Status = PublishJobStatus.Blocked,
                },
            };

            await store.SaveAsync(jobs);
            var loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, job => job.Id == "weixin-job" && job.Platform == PublishPlatform.WeixinChannel);
            var weixin = Assert.Single(loaded, job => job.Id == "weixin-job");
            Assert.Equal(3, weixin.AttemptCount);
            Assert.True(weixin.IsChecked);
            var transcode = Assert.Contains("transcode", weixin.StepStates);
            Assert.Equal(PublishJobStepStatus.Failed, transcode.Status);
            Assert.Equal("测试失败", transcode.Message);
            Assert.NotNull(weixin.LastStartedAt);
            Assert.NotNull(weixin.LastCompletedAt);
            Assert.Contains(loaded, job => job.Id == "kuaishou-personal-job" && job.Platform == PublishPlatform.KuaishouPersonalRevenue);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DefaultStoreDoesNotUseTikTokQueueLocation()
    {
        var store = new PublishJobStore();

        Assert.Contains("YunfanPlatformPublisher", store.StorePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".tiktok-task-queue", store.StorePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("app.db", store.StorePath, StringComparison.OrdinalIgnoreCase);
    }
}
