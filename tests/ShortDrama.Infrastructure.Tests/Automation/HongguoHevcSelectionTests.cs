using System.Text.Json.Nodes;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoHevcSelectionTests
{
    [Fact]
    public void RequestedQualityPrefersHevcOverByteVc2()
    {
        var playback = HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/1080-bvc2", "bytevc2", "1080p"),
            Track("https://cdn.example/1080-hevc", "hvc1", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p")), "1080P");

        Assert.Equal("https://cdn.example/1080-hevc", playback.Url);
        Assert.Equal(1080, playback.Definition);
        Assert.Null(HongguoGuestPlayback.LowerQualityNote(1080, playback.Definition));
    }

    [Fact]
    public void LowersToTheHighestHevcWhenRequestedQualityIsByteVc2()
    {
        var playback = HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/1080-bvc2", "bytevc2", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p"),
            Track("https://cdn.example/540-hevc", "hevc", "540p")), "1080");

        Assert.Equal("https://cdn.example/720-hevc", playback.Url);
        Assert.Equal(720, playback.Definition);
        Assert.Equal("1080P 没有 FFmpeg 可播放的编码，改用 720P", HongguoGuestPlayback.LowerQualityNote(1080, playback.Definition));
    }

    [Fact]
    public void DoesNotRaiseQualityToFindHevc()
    {
        var error = Assert.Throws<HongguoHighException>(() => HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/1080-hevc", "bytevc1", "1080p"),
            Track("https://cdn.example/720-bvc2", "bvc2", "720p")), "720P"));

        Assert.Equal(HongguoGuestPlayback.UnsupportedCodecMessage, error.Message);
        Assert.False(DramaSourceRouter.ShouldRetryDownload(error));
    }

    [Fact]
    public void SkipsWhenEveryQualityIsByteVc2()
    {
        var error = Assert.Throws<HongguoHighException>(() => HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/1080", "bytevc2", "1080p"),
            Track("https://cdn.example/360", "bytevc2", "360p")), "1080P"));

        Assert.Contains("更换剧集", error.Message, StringComparison.Ordinal);
        Assert.Contains("更换下载链路", error.Message, StringComparison.Ordinal);
        Assert.True(HongguoGuestPlayback.IsUnsupportedCodec(error));
    }

    [Fact]
    public void KeepsCommonFfmpegCodecAtTheRequestedQuality()
    {
        var playback = HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/1080-h264", "h264", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p")), "1080P");

        Assert.Equal("https://cdn.example/1080-h264", playback.Url);
        Assert.Equal(1080, playback.Definition);
    }

    [Theory]
    [InlineData("av1")]
    [InlineData("vp9")]
    [InlineData("mpeg4")]
    [InlineData("prores")]
    public void AcceptsOtherCommonFfmpegCodecs(string codec)
    {
        var playback = HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/playable", codec, "1080p")), "1080P");

        Assert.Equal("https://cdn.example/playable", playback.Url);
    }

    [Fact]
    public void LowersEveryEpisodeWhenAnyEpisodeLacksTheRequestedCodec()
    {
        var playable = Model(
            Track("https://cdn.example/1080-hevc", "bytevc1", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p"));
        var blocked = Model(
            Track("https://cdn.example/1080-bvc2", "bytevc2", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p"),
            Track("https://cdn.example/540-hevc", "hevc", "540p"));

        Assert.Equal("720", HongguoGuestPlayback.ChooseUniformQuality("1080P", [playable, blocked]));

        var playback = HongguoGuestPlayback.Pick(playable, "720", lockDefinition: true);
        Assert.Equal("https://cdn.example/720-hevc", playback.Url);
        Assert.Equal(720, playback.Definition);
    }

    [Fact]
    public void KeepsRequestedQualityWhenEveryEpisodeCanPlayIt()
    {
        var first = Model(Track("https://cdn.example/a", "h264", "1080p"));
        var second = Model(Track("https://cdn.example/b", "bytevc1", "1080p"));

        Assert.Equal("1080", HongguoGuestPlayback.ChooseUniformQuality("1080P", [first, second]));
    }

    [Fact]
    public void RefusesTheWholeDownloadWhenAnyEpisodeHasNoPlayableAddress()
    {
        var playable = Model(
            Track("https://cdn.example/1080-hevc", "bytevc1", "1080p"),
            Track("https://cdn.example/720-hevc", "bytevc1", "720p"));
        var blocked = Model(
            Track("https://cdn.example/1080-bvc2", "bytevc2", "1080p"),
            Track("https://cdn.example/720-bvc2", "bytevc2", "720p"));
        var chosen = HongguoGuestPlayback.ChooseUniformQuality("1080P", [playable, blocked]);

        var message = HongguoGuestPlayback.MissingPlayableDownloadMessage(
            [(2, playable), (99, blocked)],
            chosen);

        Assert.Equal("720", chosen);
        Assert.Equal("第99集没有 FFmpeg 可播放的下载地址，未开始下载。请更换剧集或更换下载链路。", message);
    }

    [Fact]
    public void AllowsDownloadWhenEveryEpisodeHasAPlayableAddress()
    {
        var first = Model(Track("https://cdn.example/a", "h264", "1080p"));
        var second = Model(Track("https://cdn.example/b", "bytevc1", "1080p"));

        Assert.Null(HongguoGuestPlayback.MissingPlayableDownloadMessage([(1, first), (2, second)], "1080"));
    }

    [Fact]
    public void LockedQualityDoesNotLowerASingleEpisode()
    {
        var error = Assert.Throws<HongguoHighException>(() => HongguoGuestPlayback.Pick(Model(
            Track("https://cdn.example/720-bvc2", "bytevc2", "720p"),
            Track("https://cdn.example/540-hevc", "hevc", "540p")), "720", lockDefinition: true));

        Assert.Equal(HongguoGuestPlayback.UnsupportedCodecMessage, error.Message);
    }

    [Fact]
    public void ReadsCodecFromVideoMeta()
    {
        var track = new JsonObject
        {
            ["main_url"] = "https://cdn.example/meta",
            ["video_meta"] = new JsonObject
            {
                ["definition"] = "480p",
                ["codec_type"] = "bytevc1",
            },
        };
        var playback = HongguoGuestPlayback.Pick(Model(track), "1080P");

        Assert.Equal("https://cdn.example/meta", playback.Url);
        Assert.Equal(480, playback.Definition);
    }

    private static JsonObject Model(params JsonObject[] tracks)
    {
        var list = new JsonArray();
        foreach (var track in tracks)
            list.Add(track);
        return new JsonObject { ["video_list"] = list };
    }

    private static JsonObject Track(string url, string codec, string definition) => new()
    {
        ["main_url"] = url,
        ["codec_type"] = codec,
        ["video_meta"] = new JsonObject { ["definition"] = definition },
    };
}
