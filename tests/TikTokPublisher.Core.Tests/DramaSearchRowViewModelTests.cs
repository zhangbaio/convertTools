using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaSearchRowViewModelTests
{
    [Fact]
    public void UpdateItem_ReusesRowAndPreservesPosterStateWhenUrlIsUnchanged()
    {
        var row = new DramaSearchRowViewModel(Item("剧一", "https://cdn.example/poster.jpg"))
        {
            PosterStatus = "已缓存",
        };

        row.UpdateItem(Item("剧一（详情已补全）", "https://cdn.example/poster.jpg", author: "作者甲"));

        row.Title.Should().Be("剧一（详情已补全）");
        row.Author.Should().Be("作者甲");
        row.PosterStatus.Should().Be("已缓存");
    }

    [Fact]
    public void UpdateItem_ResetsOnlyThisRowsPosterWhenUrlChanges()
    {
        var row = new DramaSearchRowViewModel(Item("剧一", ""))
        {
            PosterStatus = "暂无封面",
        };

        row.UpdateItem(Item("剧一", "https://cdn.example/poster.jpg"));

        row.PosterUrl.Should().Be("https://cdn.example/poster.jpg");
        row.PosterStatus.Should().Be("封面加载中");
        row.HasPosterImage.Should().BeFalse();
    }

    private static DramaSearchItem Item(string title, string posterUrl, string author = "") => new()
    {
        BookId = "book-1",
        Title = title,
        PosterUrl = posterUrl,
        Author = author,
        EpisodeTotal = 20,
    };
}
