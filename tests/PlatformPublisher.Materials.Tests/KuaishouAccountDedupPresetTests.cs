using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;
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

            var failure = await Assert.ThrowsAsync<KuaishouVideoDedup.DraftBatchUnavailableException>(() => KuaishouVideoDedup.RunAsync(
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

    [Fact]
    public async Task Unbound_batch_is_reused_only_when_template_parameters_match()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            const string account = "account-a";
            var output = root.CreateSubdirectory("videos");
            var episode = Path.Combine(output.FullName, "第1集.mp4");
            var identity = new string('A', 64);
            var original = new VideoDedupPreset { TrimStartSeconds = 1 };
            var manifest = Path.Combine(output.FullName, KuaishouSettlementEpisodeVideoService.DedupManifestName);
            File.WriteAllText(episode, "validated episode");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new KuaishouSettlementEpisodeVideoService.DedupManifest(
                identity, KuaishouSettlementEpisodeVideoService.BatchAccountKey(account), ["第1集.mp4"], original.Fingerprint())));

            Task<PublishVideoBatch> Run(VideoDedupPreset preset) =>
                KuaishouVideoDedup.RunAsync(Project(root.FullName), account, "测试账号", preset,
                    PublishPlatform.KuaishouPersonalRevenue, force: false, _ => { }, (_, _) => Task.FromResult(true), CancellationToken.None);

            Assert.Equal(identity, (await Run(original)).Identity);
            var changed = new VideoDedupPreset { TrimStartSeconds = 2 };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(changed));
            Assert.Contains("没有可去重的原视频", error.Message);
            Assert.Equal("validated episode", File.ReadAllText(episode));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static KuaishouPersonalProjectData Project(string directory) =>
        new(directory, directory, "测试剧集", "", "", [], "", "", "", [], [], []);

    [Fact]
    public void Kuaishou_uses_the_shared_chinese_default_and_snapshots_explicit_templates()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var catalog = new VideoDedupTemplateCatalog(root.FullName);
            var kuaishou = VideoDedupTemplateAccount.ForKuaishou("account-a");
            var tikTok = new VideoDedupTemplateAccount("account-b", VideoDedupTemplateTarget.TikTokOverseas);
            Assert.Equal("通用中文模板", KuaishouVideoDedup.LoadPreset(kuaishou.Id, catalog).Name);

            var template = catalog.SaveAccountConfiguration(tikTok, null, "共享自定义", new VideoDedupPreset { TrimStartSeconds = 4 });
            catalog.SaveAccountConfiguration(kuaishou, template.Id);
            var captured = KuaishouVideoDedup.LoadPreset(kuaishou.Id, catalog);
            catalog.Save(template.Id, template.Name, new VideoDedupPreset { TrimStartSeconds = 5 });

            Assert.Equal(4, captured.TrimStartSeconds);
            Assert.Equal(5, KuaishouVideoDedup.LoadPreset(kuaishou.Id, catalog).TrimStartSeconds);
            Assert.Equal("通用中文模板", KuaishouVideoDedup.LoadPreset("account-c", catalog).Name);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
