using FluentAssertions;
using ShortDrama.Infrastructure.Media;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Media;

public sealed class UploadTranscodeBitrateProfilesTests
{
    [Fact]
    public void Parse_Should_Fall_Back_To_Default_Profiles_When_Input_Is_Empty()
    {
        var profiles = UploadTranscodeBitrateProfiles.Parse(string.Empty);

        profiles.Should().HaveCount(3);
        profiles[0].Name.Should().Be("720p及以下");
        profiles[1].Name.Should().Be("1080p");
        profiles[2].Name.Should().Be("2k+");
    }

    [Fact]
    public void Select_Should_Match_Profile_By_Short_Edge()
    {
        var profiles = UploadTranscodeBitrateProfiles.Parse("""
{"profiles":[
  {"name":"低分辨率","min_short_edge":1,"max_short_edge":959,"bitrate_mbps":4.8,"audio_kbps":128,"enabled":true,"video_encoder":"auto","preset":"veryfast"},
  {"name":"高清","min_short_edge":960,"max_short_edge":1439,"bitrate_mbps":6.0,"audio_kbps":128,"enabled":true,"video_encoder":"auto","preset":"fast"},
  {"name":"超清","min_short_edge":1440,"max_short_edge":0,"bitrate_mbps":7.0,"audio_kbps":160,"enabled":true,"video_encoder":"auto","preset":"medium"}
]}
""");

        UploadTranscodeBitrateProfiles.Select(profiles, 900).Name.Should().Be("低分辨率");
        UploadTranscodeBitrateProfiles.Select(profiles, 1080).Name.Should().Be("高清");
        UploadTranscodeBitrateProfiles.Select(profiles, 1800).Name.Should().Be("超清");
    }
}
