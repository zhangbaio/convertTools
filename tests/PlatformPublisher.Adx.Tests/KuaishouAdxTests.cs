using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Adx.Tests;

public sealed class KuaishouAdxTests
{
    [Fact]
    public void FormatTitle_PreservesFullMaterialIdWithinTwentyCharacters()
    {
        var title = KuaishouAdxIdentity.FormatTitle("{新剧名}{排名}", "这是一个非常非常长的新剧名称", "原剧名", 12, "1297839032");

        Assert.True(title.Length <= 20);
        Assert.EndsWith("-1297839032", title);
    }

    [Fact]
    public void List_SeparatesKuaishouStatusAndKeepsMissingFiles()
    {
        var root = CreateRoot();
        try
        {
            var workflow = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
            var batchDirectory = Directory.CreateDirectory(Path.Combine(workflow, "materials", "adx", "batch-1")).FullName;
            var video = Path.Combine(batchDirectory, "one.mp4");
            File.WriteAllBytes(video, [1, 2, 3]);
            var missing = Path.Combine(batchDirectory, "missing.mp4");
            var store = new AdxBatchStore();
            var manifestPath = Path.Combine(batchDirectory, AdxBatchStore.ManifestFileName);
            store.Write(new AdxBatchManifest
            {
                BatchId = "batch-1", WorkflowDir = workflow, ManifestPath = manifestPath,
                CreatedAt = DateTimeOffset.UtcNow,
                Items =
                [
                    new AdxBatchItem { MaterialId = "one", Rank = 1, VideoPath = video },
                    new AdxBatchItem { MaterialId = "missing", Rank = 2, VideoPath = missing },
                ],
            });
            store.RecordItem(manifestPath, "weixin-account", "one", "success", "视频号完成");

            var resolver = new KuaishouAdxBatchResolver(store);
            var initial = resolver.List(workflow, "account-1");
            Assert.Equal(KuaishouLocalAdxMaterialStatus.Available, initial.Single(item => item.MaterialId == "one").Status);
            Assert.Equal(KuaishouLocalAdxMaterialStatus.Missing, initial.Single(item => item.MaterialId == "missing").Status);

            store.RecordItem(manifestPath, KuaishouAdxIdentity.AccountKey("account-1"), "one", "success", "快手完成");
            Assert.Equal(KuaishouLocalAdxMaterialStatus.Published,
                resolver.List(workflow, "account-1").Single(item => item.MaterialId == "one").Status);
            Assert.Equal(KuaishouLocalAdxMaterialStatus.Available,
                resolver.List(workflow, "account-2").Single(item => item.MaterialId == "one").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validate_RejectsVideoOutsideSelectedAdxBatch()
    {
        var root = CreateRoot();
        try
        {
            var workflow = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
            var batchDirectory = Directory.CreateDirectory(Path.Combine(workflow, "materials", "adx", "batch-1")).FullName;
            var outsideVideo = Path.Combine(root, "outside.mp4");
            var cover = Path.Combine(batchDirectory, "cover.jpg");
            File.WriteAllBytes(outsideVideo, [1]); File.WriteAllBytes(cover, [1]);
            var store = new AdxBatchStore();
            var manifestPath = Path.Combine(batchDirectory, AdxBatchStore.ManifestFileName);
            store.Write(new AdxBatchManifest
            {
                BatchId = "batch-1", WorkflowDir = workflow, ManifestPath = manifestPath,
                CreatedAt = DateTimeOffset.UtcNow,
                Items = [new AdxBatchItem { MaterialId = "one", Rank = 1, VideoPath = outsideVideo, CoverPath = cover }],
            });

            var resolver = new KuaishouAdxBatchResolver(store);
            var error = Assert.Throws<InvalidOperationException>(() => resolver.Validate(workflow,
                [new KuaishouAdxPublishItem { MaterialId = "one", ManifestPath = manifestPath }], cover));
            Assert.Contains("不属于所选 ADX 批次", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-adx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
