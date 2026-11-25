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
        Assert.Throws<InvalidOperationException>(() => state.BindVideoBatch(null));
        Assert.Throws<InvalidOperationException>(() => state.BindVideoBatch(batch with { Identity = "changed" }));
        Assert.Throws<InvalidOperationException>(() => new KuaishouPersonalUploadState { MiniSeriesId = "123" }.BindVideoBatch(batch));
    }

    [Fact]
    public void ReadingOriginalsKeepsTheirOrderAndExcludesDedupCache()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var cache = root.CreateSubdirectory(KuaishouSettlementEpisodeVideoService.DedupDirectoryName);
            var first = Path.Combine(root.FullName, "第1集.mp4");
            var second = Path.Combine(root.FullName, "第2集.mp4");
            File.WriteAllText(second, "second");
            File.WriteAllText(first, "first");
            File.WriteAllText(Path.Combine(cache.FullName, "第1集.mp4"), "processed");

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
