using System.Text;
using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class VolcengineVodClientTests
{
    [Fact]
    public void Crc32_matches_standard_test_vector()
    {
        VolcengineVodClient.Crc32HexForTest(Encoding.ASCII.GetBytes("123456789"))
            .Should().Be("cbf43926");
    }

    [Fact]
    public void Playback_domain_replaces_only_the_origin_and_preserves_the_signed_path()
    {
        var result = VolcengineVodClient.ApplyPlaybackDomain(
            "http://origin.example.com/path/video.mp4?token=abc%20123",
            "https://media.example.cn");

        result.Should().Be("https://media.example.cn/path/video.mp4?token=abc%20123");
    }

    [Fact]
    public void Empty_playback_domain_keeps_the_api_url()
    {
        VolcengineVodClient.ApplyPlaybackDomain("http://origin/video.mp4", "")
            .Should().Be("http://origin/video.mp4");
    }

    [Fact]
    public void Aideo_direct_url_uses_volc_type_a_signature()
    {
        var result = VolcengineVodClient.BuildSignedDirectUrl(
            "http://play.example.com", "video.mp4", "auth_key", "abc123def456",
            1644406401, "2e1ca42a1bb248408fc9cf435e5af744");

        result.Should().Be(
            "http://play.example.com/video.mp4?auth_key=1644406401-2e1ca42a1bb248408fc9cf435e5af744-0-19ac048da47272a73cb635f9a2ccd4eb");
    }
}
