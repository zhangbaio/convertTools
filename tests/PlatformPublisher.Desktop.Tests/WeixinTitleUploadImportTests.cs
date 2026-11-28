using PlatformPublisher.Desktop.Services;
using ShortDrama.Core.Interfaces;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using Xunit;
using DramaSearchItem = ShortDrama.Core.Models.DramaSearchItem;

namespace PlatformPublisher.Desktop.Tests;

public sealed class WeixinTitleUploadImportTests
{
    [Fact]
    public async Task EpisodeMatchingImportsOnlyAnExactUnambiguousInRangeResult()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateService((title, _) => title switch
        {
            "风月无凭" => [Drama("wrong-count", title, 42), Drama("wanted", title, 43)],
            "找不到" => [Drama("unrelated", "近似剧名", 43)],
            "歧义" => [Drama("one", title, 43), Drama("two", title, 43)],
            "超集数" => [Drama("over-limit", title, 201)],
            _ => [],
        });

        var outcome = await service.ImportForWeixinAsync(workspace.Root,
            "风月无凭 43\n找不到 43\n歧义 43\n超集数 201\n缺少集数",
            UploadTitleImportService.MatchModeTitleEpisode, new ClientSettings(),
            EmptyPaths(), EmptyPaths(), null, CancellationToken.None);

        Assert.Equal(5, outcome.RequestedCount);
        var path = Assert.Single(outcome.ProjectDirectories);
        Assert.Contains("\"bookId\": \"wanted\"", await File.ReadAllTextAsync(Path.Combine(path, "shortdrama-project.json")));
        Assert.Contains("\"expectedEpisodeTotal\": 43", await File.ReadAllTextAsync(Path.Combine(path, "shortdrama-project.json")));
        Assert.Equal(4, outcome.Failures.Count);
        Assert.DoesNotContain(outcome.Failures, failure => failure.Title == "风月无凭");
    }

    [Fact]
    public async Task TitleMatchingSkipsTheAccountBookIdAndProtectsTheOtherAccountDirectory()
    {
        using var workspace = new TestWorkspace();
        var own = Directory.CreateDirectory(Path.Combine(workspace.Root, "已改名项目")).FullName;
        await File.WriteAllTextAsync(Path.Combine(own, "shortdrama-project.json"), "{\"bookId\":\"old-id\"}");
        var shared = Directory.CreateDirectory(Path.Combine(workspace.Root, "其他账号剧")).FullName;
        var otherMetadata = Path.Combine(shared, "shortdrama-project.json");
        await File.WriteAllTextAsync(otherMetadata, "{\"bookId\":\"other-id\",\"marker\":true}");
        var service = workspace.CreateService((title, _) => title switch
        {
            "原剧名" => [Drama("old-id", title)],
            "其他账号剧" => [Drama("other-id", title)],
            "近似剧" => [Drama("fuzzy", "其他剧")],
            _ => [Drama("new-id", title)],
        });

        var outcome = await service.ImportForWeixinAsync(workspace.Root,
            "原剧名\n其他账号剧\n近似剧\n本批新剧\n同一ID别名\n本批新剧", UploadTitleImportService.MatchModeTitle,
            new ClientSettings(), new HashSet<string> { own }, new HashSet<string> { shared }, null, CancellationToken.None);

        Assert.Equal(6, outcome.RequestedCount);
        Assert.Equal(new[] { "本批新剧", "原剧名", "同一ID别名" }, outcome.Duplicates);
        Assert.Single(outcome.ProjectDirectories);
        Assert.Contains(outcome.Failures, failure => failure.Title == "其他账号剧" && failure.Reason.Contains("其他账号"));
        Assert.Contains(outcome.Failures, failure => failure.Title == "近似剧");
        Assert.Equal("{\"bookId\":\"other-id\",\"marker\":true}", await File.ReadAllTextAsync(otherMetadata));
    }

    [Fact]
    public async Task StoppingSearchReturnsPreviouslyPreparedDirectories()
    {
        using var workspace = new TestWorkspace();
        using var stop = new CancellationTokenSource();
        var service = workspace.CreateService((title, ct) =>
        {
            if (title == "第二部")
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return [Drama(title, title)];
        });

        var outcome = await service.ImportForWeixinAsync(workspace.Root, "第一部\n第二部",
            UploadTitleImportService.MatchModeTitle, new ClientSettings(), EmptyPaths(), EmptyPaths(), null, stop.Token);

        Assert.True(outcome.Cancelled);
        Assert.Contains("第一部", Assert.Single(outcome.ProjectDirectories));
        Assert.Empty(outcome.Failures);
    }

    private static DramaSearchItem Drama(string id, string title, int episodes = 43) =>
        new(id, title, "短剧", episodes, "测试简介", "");

    private static HashSet<string> EmptyPaths() => new(StringComparer.OrdinalIgnoreCase);

    private sealed class TestWorkspace : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("weixin-titles-test-");

        public string Root => _directory.FullName;

        public DramaTitleImportService CreateService(Func<string, CancellationToken, IReadOnlyList<DramaSearchItem>> search) =>
            new(new FakeSearch(search), new YunfanDramaSettingsProvider(Path.Combine(Root, "settings.db")));

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class FakeSearch(Func<string, CancellationToken, IReadOnlyList<DramaSearchItem>> find) : IDramaSearchService
    {
        public Task<IReadOnlyList<DramaSearchItem>> SearchAsync(string keyword, int page, CancellationToken cancellationToken) =>
            Task.FromResult(find(keyword, cancellationToken));

        public Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
