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

    [Fact]
    public void CreateFixedTemplateLayout_uses_reference_position_and_size()
    {
        var layout = PosterTitleProgrammaticRenderer.CreateFixedTemplateLayout(
            PosterTitleProgrammaticRenderer.FormatTitleLines("签约错认旧爱再续前缘"),
            600,
            858);

        layout.X.Should().BeApproximately(0.07f, 0.001f);
        layout.Y.Should().BeApproximately(0.663f, 0.01f);
        layout.Width.Should().BeApproximately(0.8f, 0.001f);
        layout.FontScale.Should().BeApproximately(64f / 858f, 0.001f);
        layout.TextColor.Should().Be(new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255));
        layout.Align.Should().Be(SixLabors.Fonts.HorizontalAlignment.Left);
    }
}
