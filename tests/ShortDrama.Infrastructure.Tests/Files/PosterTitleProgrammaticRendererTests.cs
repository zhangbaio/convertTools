using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterTitleProgrammaticRendererTests
{
    [Theory]
    [InlineData("灵泉空间", "灵泉空间")]
    [InlineData("携灵泉空间在古代创富安家", "携灵泉空间\n在古代创富安家")]
    [InlineData("第一行\n第二行", "第一行\n第二行")]
    public void FormatTitleLines_creates_readable_poster_lines(string title, string expected)
    {
        PosterTitleProgrammaticRenderer.FormatTitleLines(title).Should().Be(expected);
    }
}
