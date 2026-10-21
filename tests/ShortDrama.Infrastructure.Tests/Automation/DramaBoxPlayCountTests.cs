using System.Text.Json;
using FluentAssertions;
using Xunit;
using ShortDrama.Infrastructure.Automation;

namespace ShortDrama.Infrastructure.Tests.Automation;

public class DramaBoxPlayCountTests
{
    [Theory]
    [InlineData("79.6K", 79600)]
    [InlineData("8.6K", 8600)]
    [InlineData("1.2M", 1200000)]
    [InlineData("1.5万", 15000)]
    [InlineData("1200", 1200)]
    [InlineData("", 0)]
    public void Parses_compact_play_count(string text, int expected)
    {
        DramaBoxApiService.ParsePlayCount(text).Should().Be(expected);
    }

    [Fact]
    public void Reads_episode_count_when_chapter_count_is_zero()
    {
        using var document = JsonDocument.Parse("""{"chapterCount":0,"totalChapterNum":31}""");
        DramaBoxApiService.ReadEpisodeCount(document.RootElement).Should().Be(31);
    }

    [Fact]
    public void Reads_episode_count_from_chapter_count()
    {
        using var document = JsonDocument.Parse("""{"chapterCount":31}""");
        DramaBoxApiService.ReadEpisodeCount(document.RootElement).Should().Be(31);
    }

    [Fact]
    public void Reads_cover_from_search_and_chapter_payloads()
    {
        using var search = JsonDocument.Parse("""{"cover":"https://img.example/a.jpg"}""");
        DramaBoxApiService.ReadCoverUrl(search.RootElement).Should().Be("https://img.example/a.jpg");

        using var book = JsonDocument.Parse("""{"bookCover":"https://img.example/b.webp","cover":"https://img.example/ignored.jpg"}""");
        DramaBoxApiService.ReadCoverUrl(book.RootElement).Should().Be("https://img.example/b.webp");

        using var empty = JsonDocument.Parse("""{"cover":"not-a-url"}""");
        DramaBoxApiService.ReadCoverUrl(empty.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void Reads_numeric_play_count()
    {
        using var document = JsonDocument.Parse("8600");
        DramaBoxApiService.ParsePlayCount(document.RootElement).Should().Be(8600);
    }
}
