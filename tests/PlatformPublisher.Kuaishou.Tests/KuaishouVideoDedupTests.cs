using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouVideoDedupTests
{
    [Fact]
    public void ExistingDraftRequiresTheSameVideoBatch()
    {
        var batch = new PublishVideoBatch("template-and-source-version", ["first.mp4"]);
        var state = new KuaishouPersonalUploadState();
        state.BindVideoBatch(batch);
        state.MiniSeriesId = "123";
        state = JsonSerializer.Deserialize<KuaishouPersonalUploadState>(JsonSerializer.Serialize(state))!;

        state.BindVideoBatch(batch);
        Assert.Equal(1, state.VideoBatchEpisodeCount);
        Assert.Contains("旧草稿 1 集，本次成片 2 集", Assert.Throws<InvalidOperationException>(() =>
            state.BindVideoBatch(batch with { Paths = ["first.mp4", "second.mp4"] })).Message);
        Assert.Throws<InvalidOperationException>(() => state.BindVideoBatch(null));
        Assert.Throws<InvalidOperationException>(() => state.BindVideoBatch(batch with { Identity = "changed" }));
        Assert.Throws<InvalidOperationException>(() => new KuaishouPersonalUploadState { MiniSeriesId = "123" }.BindVideoBatch(batch));
    }

    [Fact]
    public async Task UploadUsesTheResegmentedBatchInsteadOfTheOriginalCount()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "短剧信息.txt"), "剧名：测试剧集");
            File.WriteAllText(Path.Combine(root.FullName, "第1集.mp4"), "first original");
            File.WriteAllText(Path.Combine(root.FullName, "第2集.mp4"), "second original");
            var videos = root.CreateSubdirectory(KuaishouSettlementEpisodeVideoService.VideosDirectoryName);
            var merged = Path.Combine(videos.FullName, "第1集.mp4");
            File.WriteAllText(merged, "merged output");
            const string account = "test-account";
            var identity = new string('A', 64);
            File.WriteAllText(Path.Combine(videos.FullName, KuaishouSettlementEpisodeVideoService.DedupManifestName),
                JsonSerializer.Serialize(new KuaishouSettlementEpisodeVideoService.DedupManifest(
                    identity, KuaishouSettlementEpisodeVideoService.BatchAccountKey(account), ["第1集.mp4"])));
            var dataService = new KuaishouPersonalProjectDataService(null!);
            var config = new KuaishouPersonalConfig();

            foreach (var platform in new[] { PublishPlatform.KuaishouPersonalRevenue, PublishPlatform.KuaishouEnterpriseRevenue })
            {
                var job = new PublishJob { AccountId = account, Platform = platform, ProjectDirectory = root.FullName };
                var data = await dataService.ResolveForUploadAsync(job, config, CancellationToken.None);
                Assert.Equal(merged, Assert.Single(data.VideoPaths));
                Assert.Equal(identity, data.VideoBatchIdentity);
                Assert.Equal(merged, KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(data.VideoPaths, 1));
                Assert.Null(KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(data.VideoPaths, 2));
                job.PreparedUploadVideos = new PublishVideoBatch(new string('B', 64), [merged]);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => dataService.ResolveForUploadAsync(job, config, CancellationToken.None));
            }

            Assert.Equal(2, KuaishouSettlementEpisodeVideoService.ReadSource(root.FullName, root.FullName).Count);
            File.WriteAllText(Path.Combine(videos.FullName, KuaishouSettlementEpisodeVideoService.DedupWritingName), "[]");
            var blocked = new PublishJob { AccountId = account, ProjectDirectory = root.FullName };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dataService.ResolveForUploadAsync(blocked, config, CancellationToken.None));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadingOriginalsKeepsTheirOrderAndExcludesDedupCache()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var cache = root.CreateSubdirectory(KuaishouSettlementEpisodeVideoService.DedupDirectoryName);
            var published = root.CreateSubdirectory(KuaishouSettlementEpisodeVideoService.DedupOutputDirectoryName)
                .CreateSubdirectory("批次-测试");
            var first = Path.Combine(root.FullName, "第1集.mp4");
            var second = Path.Combine(root.FullName, "第2集.mp4");
            File.WriteAllText(second, "second");
            File.WriteAllText(first, "first");
            File.WriteAllText(Path.Combine(cache.FullName, "第1集.mp4"), "processed");
            File.WriteAllText(Path.Combine(published.FullName, "第0001集.mp4"), "current output");

            var sources = KuaishouSettlementEpisodeVideoService.ReadSource(root.FullName, root.FullName);

            Assert.Equal(new[] { first, second }, sources);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "videos")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void TemplateSelectionIsSavedPerAccountAndOldConfigurationHasNoSelection()
    {
        var first = JsonSerializer.Deserialize<KuaishouPersonalConfig>("""{"VideoDedupTemplateId":"first-template"}""")!;
        var second = JsonSerializer.Deserialize<KuaishouPersonalConfig>("""{"VideoDedupTemplateId":"second-template"}""")!;
        Assert.Equal("first-template", JsonSerializer.Deserialize<KuaishouPersonalConfig>(JsonSerializer.Serialize(first))!.VideoDedupTemplateId);
        Assert.Equal("second-template", second.VideoDedupTemplateId);
        Assert.Equal("", JsonSerializer.Deserialize<KuaishouPersonalConfig>("{}")!.VideoDedupTemplateId);
    }
}
