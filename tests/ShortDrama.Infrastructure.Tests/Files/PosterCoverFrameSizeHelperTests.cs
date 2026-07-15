using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterCoverFrameSizeHelperTests
{
    [Fact]
    public void ResolveFrameApiSize_DoubaoPresetWithDifferentAspect_UsesSourceAspect()
    {
        var config = new Dictionary<string, string>
        {
            ["ImageProvider"] = "doubao",
            ["ImageSize"] = "1728x2304",
        };

        PosterCoverFrameSizeHelper.ResolveFrameApiSize(600, 858, config)
            .Should().Be("1616x2304");
    }

    [Fact]
    public void ResolveFrameApiSize_DoubaoPresetWithMatchingAspect_KeepsPreset()
    {
        var config = new Dictionary<string, string>
        {
            ["ImageProvider"] = "doubao",
            ["ImageSize"] = "1728x2304",
        };

        PosterCoverFrameSizeHelper.ResolveFrameApiSize(600, 800, config)
            .Should().Be("1728x2304");
    }

    [Fact]
    public void ResolveFrameApiSize_Ofox_UsesSourceAspect()
    {
        var config = new Dictionary<string, string>
        {
            ["ImageProvider"] = "ofox_image2",
            ["ImageSize"] = "1728x2304",
        };

        PosterCoverFrameSizeHelper.ResolveFrameApiSize(600, 858, config)
            .Should().Be("1616x2304");
    }
}
