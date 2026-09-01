using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using Xunit;

namespace PlatformPublisher.Common.Tests;

public sealed class PublishAccountStoreTests
{
    [Fact]
    public async Task AccountsRoundTripWithoutUsingTikTokStorage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "platform-account-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempRoot, "accounts.json");
        try
        {
            var store = new PublishAccountStore(path);
            await store.SaveAsync(
            [
                new PublishAccount
                {
                    Id = "weixin-account",
                    Platform = PublishPlatform.WeixinChannel,
                    Name = "视频号主账号",
                    BaseConfigPath = @"D:\config\weixin.json",
                },
                new PublishAccount
                {
                    Id = "kuaishou-account",
                    Platform = PublishPlatform.KuaishouPersonalRevenue,
                    Name = "快手个人账号",
                },
            ]);

            var loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, account => account.Id == "weixin-account" && account.Platform == PublishPlatform.WeixinChannel);
            Assert.Contains(loaded, account => account.Id == "kuaishou-account" && account.Platform == PublishPlatform.KuaishouPersonalRevenue);
            Assert.DoesNotContain("tiktok", store.StorePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void StableAccountIdSurvivesDisplayNameChanges()
    {
        var job = new PublishJob { AccountId = "stable-id", AccountName = "旧昵称" };
        var before = PublishAccountStorageKey.ForJob(job);
        job.AccountName = "新昵称";

        Assert.Equal(before, PublishAccountStorageKey.ForJob(job));
    }
}
