using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSeriesEditorPageUrlTests
{
    [Theory]
    [InlineData("https://www.tiktokdramacenter.com/series/draft")]
    [InlineData("https://www.tiktokdramacenter.com/series/draft/7686659828362613780")]
    [InlineData("https://www.tiktokdramacenter.com/series/7686659828362613780")]
    [InlineData("https://www.tiktokdramacenter.com/series/7686659828362613780/")]
    [InlineData("https://www.tiktokdramacenter.com/series/7686659828362613780?from=draft")]
    public void Recognizes_supported_series_editor_routes(string url)
    {
        TikTokBrowserActions.IsTikTokSeriesEditorPageUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://example.com/series/7686659828362613780")]
    [InlineData("https://fake-tiktokdramacenter.com/series/7686659828362613780")]
    [InlineData("https://www.tiktokdramacenter.com/series")]
    [InlineData("https://www.tiktokdramacenter.com/series/detail/7686659828362613780")]
    [InlineData("https://www.tiktokdramacenter.com/series/not-a-series-id")]
    public void Rejects_non_editor_routes(string? url)
    {
        TikTokBrowserActions.IsTikTokSeriesEditorPageUrl(url).Should().BeFalse();
    }
}
