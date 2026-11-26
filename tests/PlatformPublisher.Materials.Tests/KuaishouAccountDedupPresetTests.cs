using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using TikTokPublisher.Core.Models;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class KuaishouAccountDedupPresetTests
{
    [Theory]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue)]
    [InlineData(PublishPlatform.KuaishouEnterpriseRevenue)]
    public async Task RetryReusesTheDraftBatchBeforeReadingOriginals(PublishPlatform platform)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            const string account = "account-a";
            var video = root.CreateSubdirectory("videos");
            var episode = Path.Combine(video.FullName, "第1集.mp4");
            var identity = new string('A', 64);
            var manifest = Path.Combine(video.FullName, KuaishouSettlementEpisodeVideoService.DedupManifestName);
            var contents = JsonSerializer.Serialize(new KuaishouSettlementEpisodeVideoService.DedupManifest(
                identity, KuaishouSettlementEpisodeVideoService.BatchAccountKey(account), ["第1集.mp4"]));
            File.WriteAllText(episode, "validated episode");
            File.WriteAllText(manifest, contents);
            await new KuaishouPersonalUploadStateStore().SaveAsync(root.FullName,
                new KuaishouPersonalUploadState { MiniSeriesId = "100", VideoBatchIdentity = identity, VideoBatchEpisodeCount = 1 },
                CancellationToken.None, platform);

            var batch = await KuaishouVideoDedup.RunAsync(Project(root.FullName), account, "测试账号",
                VideoDedupPreset.CreateDefault(), platform, force: false, _ => { },
                (_, _) => throw new InvalidOperationException("Reusing a batch must not start media processing."),
                CancellationToken.None);

            Assert.Equal(identity, batch.Identity);
            Assert.Equal(episode, Assert.Single(batch.Paths));
            Assert.Equal(contents, File.ReadAllText(manifest));
            Assert.Equal("validated episode", File.ReadAllText(episode));
            var forced = await Assert.ThrowsAsync<InvalidOperationException>(() => KuaishouVideoDedup.RunAsync(
                Project(root.FullName), account, "测试账号", VideoDedupPreset.CreateDefault(), platform, force: true,
                _ => { }, (_, _) => Task.FromResult(true), CancellationToken.None));
            Assert.Contains("没有可去重的原视频", forced.Message);
            Assert.Equal(contents, File.ReadAllText(manifest));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RetryStopsBeforeOverwritingFilesWhenTheDraftBatchWasInterrupted()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var output = root.CreateSubdirectory("videos");
            var marker = Path.Combine(output.FullName, KuaishouSettlementEpisodeVideoService.DedupWritingName);
            File.WriteAllText(marker, "[]");
            await new KuaishouPersonalUploadStateStore().SaveAsync(root.FullName,
                new KuaishouPersonalUploadState { MiniSeriesId = "100", VideoBatchIdentity = new string('A', 64) },
                CancellationToken.None);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => KuaishouVideoDedup.RunAsync(
                Project(root.FullName), "account-a", "测试账号", VideoDedupPreset.CreateDefault(),
                PublishPlatform.KuaishouPersonalRevenue, force: false, _ => { }, (_, _) => Task.FromResult(true),
                CancellationToken.None));

            Assert.Contains("请恢复原批次后续传", failure.Message);
            Assert.Equal("[]", File.ReadAllText(marker));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static KuaishouPersonalProjectData Project(string directory) =>
        new(directory, directory, "测试剧集", "", "", [], "", "", "", [], [], []);

    [Fact]
    public void AccountPresetsAreIndependentAndSavedPresetsTakePriorityOverLegacyTemplates()
    {
        var root = Directory.CreateTempSubdirectory();
        var database = new PlatformDatabase(Path.Combine(root.FullName, "app.db"));
        try
        {
            var legacy = VideoDedupPreset.CreateDefault();
            legacy.Name = "旧方案";
            legacy.TrimStartSeconds = 2;
            new JsonSettingStore(database).Save("kuaishou.video-dedup.templates",
                new Dictionary<string, VideoDedupPreset> { ["legacy"] = legacy });

            Assert.Equal(legacy.Fingerprint(), KuaishouVideoDedup.LoadPreset("account-a", "legacy", database).Fingerprint());
            var personal = KuaishouVideoDedup.LoadPreset("account-a", "legacy", database);
            personal.TrimStartSeconds = 4;
            KuaishouVideoDedup.SavePreset("account-a", personal, database);

            var enterprise = KuaishouVideoDedup.LoadPreset("account-b", null, database);
            Assert.Equal(0, enterprise.TrimStartSeconds);
            enterprise.TrimStartSeconds = 1;
            KuaishouVideoDedup.SavePreset("account-b", enterprise, database);

            Assert.Equal(4, KuaishouVideoDedup.LoadPreset("account-a", "legacy", database).TrimStartSeconds);
            Assert.Equal(1, KuaishouVideoDedup.LoadPreset("account-b", "legacy", database).TrimStartSeconds);
            Assert.Equal(2, KuaishouVideoDedup.LoadPreset("account-c", "legacy", database).TrimStartSeconds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }
}
