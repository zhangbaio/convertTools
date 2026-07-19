using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class AliyunImsSubtitleClientTests
{
    [Fact]
    public void BuildJobParams_uses_normalized_limit_region_array()
    {
        var json = AliyunImsSubtitleClient.BuildJobParams("[[0,0.65,1,0.35]]");

        using var document = JsonDocument.Parse(json!);
        var region = document.RootElement.GetProperty("LimitRegion")[0];
        region[0].GetDouble().Should().Be(0);
        region[1].GetDouble().Should().Be(0.65);
        region[2].GetDouble().Should().Be(1);
        region[3].GetDouble().Should().Be(0.35);
    }

    [Fact]
    public void BuildJobParams_returns_null_for_default_bottom_region()
    {
        AliyunImsSubtitleClient.BuildJobParams("  ").Should().BeNull();
    }

    [Fact]
    public void BuildJobParams_rejects_invalid_region_json()
    {
        var action = () => AliyunImsSubtitleClient.BuildJobParams("not-json");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*[[0,0.65,1,0.35]]*");
    }
}
