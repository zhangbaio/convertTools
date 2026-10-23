using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class PosterImageConfigHelperTests
{
    [Theory]
    [InlineData("2K", "3:4", "1776x2368")]
    [InlineData("2K", "16:9", "2816x1584")]
    [InlineData("4K", "3:4", "1776x2368")]
    [InlineData("4K", "auto", "2K")]
    public void DoubaoImageSizeForRatio_matches_python_table(string resolution, string ratio, string expected)
    {
        PosterImageConfigHelper.DoubaoImageSizeForRatio(resolution, ratio).Should().Be(expected);
    }

    [Fact]
    public void ApplyPosterRuntimeConfig_sets_doubao_image_size()
    {
        var settings = new ClientSettings
        {
            ImageProvider = "doubao",
            ImageModelId = "seedream",
            ImageModelApiKey = "key",
            ImageModelEndpoint = "https://ark.cn-beijing.volces.com/api/v3",
            DoubaoImageResolution = "2K",
            DoubaoImageRatio = "3:4",
        };
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        PosterImageConfigHelper.ApplyPosterRuntimeConfig(payload, settings);

        payload["ImageSize"].Should().Be("1776x2368");
        payload["ImageQuality"].Should().Be("2K");
        payload["ImageEditPath"].Should().Be("/images/generations");
    }
}
