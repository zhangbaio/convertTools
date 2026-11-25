using FluentAssertions;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoSampleAspectRatioTests
{
    [Fact]
    public void Detects_an_in_band_header_overriding_a_near_square_container_ratio()
    {
        const string trace = """
            [trace_headers] 88 aspect_ratio_info_present_flag 0 = 0
            [trace_headers] 88 aspect_ratio_info_present_flag 1 = 1
            [trace_headers] 89 aspect_ratio_idc 11111111 = 255
            [trace_headers] 97 sar_width 0110010110100101 = 26021
            [trace_headers] 113 sar_height 0101100011100111 = 22759
            """;

        VideoSampleAspectRatio.FindConflict("34170277:34167015", trace)
            .Should().Contain("容器 34170277:34167015，码流 26021:22759");
    }

    [Theory]
    [InlineData("1:1", "aspect_ratio_idc 00000001 = 1")]
    [InlineData("16:12", "aspect_ratio_idc 00001110 = 14")]
    [InlineData("4:3", "aspect_ratio_info_present_flag 0 = 0")]
    public void Accepts_equivalent_ratios_and_unspecified_codec_metadata(string containerRatio, string trace)
    {
        VideoSampleAspectRatio.FindConflict(containerRatio, trace).Should().BeNull();
    }
}
