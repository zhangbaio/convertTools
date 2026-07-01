using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Services;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class SimilarUpnewFilterTests
{
    [Fact]
    public void Filter_Should_Match_Similar_Title()
    {
        var items = new[]
        {
            Item("1", "替嫁新娘之复仇归来", "都市", "女主逆袭", "2026-07-01 10:00:00"),
            Item("2", "校园青春日记", "校园", "同学成长", "2026-07-01 11:00:00")
        };

        var result = SimilarUpnewFilter.Filter(items, ["替嫁新娘"], "medium");

        result.Should().ContainSingle();
        result[0].BookId.Should().Be("1");
    }

    [Fact]
    public void ParseTerms_Should_Support_Comma_And_NewLines()
    {
        var terms = SimilarUpnewFilter.ParseTerms("总裁，重生\n复仇");

        terms.Should().Equal("总裁", "重生", "复仇");
    }

    private static DramaSearchItem Item(string id, string title, string category, string intro, string publishTime)
    {
        return new DramaSearchItem(
            BookId: id,
            Title: title,
            Category: category,
            EpisodeTotal: 24,
            Intro: intro,
            PosterUrl: string.Empty,
            Author: string.Empty,
            PublishTime: publishTime,
            FavoriteCount: 0);
    }
}
