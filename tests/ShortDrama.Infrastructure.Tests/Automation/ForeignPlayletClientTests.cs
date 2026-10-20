using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class ForeignPlayletClientTests
{
    [Fact]
    public void ReadSearchItemsMapsReelShortBooks()
    {
        const string html = """
            <html><script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"books":[{
              "book_id":"6a8eb668895fd687c603c908",
              "book_title":"仮面の夫 隠れた帝王",
              "book_pic":"https://img.example/cover.jpg",
              "special_desc":"简介",
              "chapter_count":62,
              "read_count":342564,
              "tag":["CEO","逆袭"]
            }]}}}
            </script></html>
            """;

        var items = ForeignPlayletClient.ReadSearchItems(html, "reelshort", "ja");

        Assert.Single(items);
        Assert.Equal("foreign:reelshort|ja|6a8eb668895fd687c603c908", items[0].BookId);
        Assert.Equal("仮面の夫 隠れた帝王", items[0].Title);
        Assert.Equal(62, items[0].EpisodeTotal);
        Assert.Equal(342564, items[0].PlayCount);
        Assert.Equal("CEO / 逆袭", items[0].Category);
        Assert.Equal("https://img.example/cover.jpg", items[0].PosterUrl);
    }

    [Fact]
    public void ReadEpisodesUsesPageLinksAndFallsBackToMoviePath()
    {
        const string html = """
            <html>
            <a href="/ja/episodes/episode-1-slug-book-chapter1">1</a>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"data":{"online_base":[
              {"serial_number":1,"chapter_id":"chapter1"},
              {"serial_number":2,"chapter_id":"chapter2"}
            ]}}}}
            </script></html>
            """;

        var episodes = ForeignPlayletClient.ReadEpisodes(
            html,
            "/ja/movie/slug-book",
            "reelshort",
            "ja");

        Assert.Equal(2, episodes.Count);
        Assert.Equal("foreign_ep:reelshort|ja|/ja/episodes/episode-1-slug-book-chapter1", episodes[0].VideoId);
        Assert.Equal("foreign_ep:reelshort|ja|/ja/episodes/episode-2-slug-book-chapter2", episodes[1].VideoId);
    }

    [Fact]
    public void BuildEpisodePathFollowsReelShortCanonicalMovieUrl()
    {
        var path = ForeignPlayletClient.BuildEpisodePath(
            "/ja/movie/仮面の夫-隠れた帝王-6a8eb668895fd687c603c908",
            1,
            "0p4ncr7970");

        Assert.Equal(
            "/ja/episodes/episode-1-仮面の夫-隠れた帝王-6a8eb668895fd687c603c908-0p4ncr7970",
            path);
    }

    [Fact]
    public void ReadVideoUrlReadsEpisodePlaylist()
    {
        const string html = """
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"data":{"video_url":"https://cdn.example/video-sd.m3u8"}}}}
            </script>
            """;

        Assert.Equal("https://cdn.example/video-sd.m3u8", ForeignPlayletClient.ReadVideoUrl(html));
        Assert.True(ForeignPlayletClient.IsHlsUrl("https://cdn.example/video-sd.m3u8"));
    }

    [Fact]
    public void BuildQualityCandidatesPrefersRequestedTokenWithoutDroppingOriginal()
    {
        var candidates = ForeignPlayletClient.BuildQualityCandidates(
            "https://cdn.example/video-sd.m3u8",
            "1080P");

        Assert.Equal(
            ["https://cdn.example/video-sd.m3u8", "https://cdn.example/video-hd.m3u8"],
            candidates);
    }

    [Fact]
    public void PickByQualityPrefersRequestedRendition()
    {
        var urls = new Dictionary<string, string>
        {
            ["1080"] = "https://cdn.example/1080.mp4",
            ["720"] = "https://cdn.example/720.mp4",
        };

        Assert.Equal("https://cdn.example/720.mp4", ForeignPlayletClient.PickByQuality(urls, "720P"));
        Assert.Equal("https://cdn.example/1080.mp4", ForeignPlayletClient.PickByQuality(urls, "1080P"));
        Assert.Equal("CEO Wants My Little Rascal", ForeignPlayletClient.StripMarkup("<em>CEO</em> Wants My Little Rascal"));
    }

    [Fact]
    public void ReadReelShortV2SearchMapsResults()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"results":[{"id":"abc","title":"Back Off","chapters":71,"pic":"https://img.example/a.jpg"}]}
            """);

        var items = ForeignPlayletClient.ReadReelShortV2Search(document.RootElement, "reelshortv2", "en");

        Assert.Single(items);
        Assert.Equal("foreign:reelshortv2|en|abc", items[0].BookId);
        Assert.Equal(71, items[0].EpisodeTotal);
    }

    [Fact]
    public void UnknownSourceAndLanguageFallBackToReelShortEnglish()
    {
        Assert.Equal("reelshort", ForeignPlayletClient.NormalizeSource("missing"));
        Assert.Equal("en", ForeignPlayletClient.NormalizeLanguage(""));
        Assert.Equal("ReelShortV2", ForeignPlayletClient.SourceLabel("reelshortv2"));
    }
}
