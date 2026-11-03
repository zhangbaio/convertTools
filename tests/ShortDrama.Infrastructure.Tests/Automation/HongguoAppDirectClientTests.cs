using System.Text;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoAppDirectClientTests
{
    [Fact]
    public async Task LiveSearchEpisodesAndPlayback()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HONGUO_LIVE"), "1", StringComparison.Ordinal))
            return;

        Assert.True(HongguoAppEmulatorIdentity.TryRead(out var deviceId, out var installId));
        Assert.InRange(deviceId.Length, 10, 24);
        Assert.InRange(installId.Length, 10, 24);
        using var http = new HttpClient();
        var client = new HongguoAppDirectClient(http);
        var used = HongguoAppDirectClient.EmulatorChainUses;
        var items = await client.SearchAsync("长公主", 1, CancellationToken.None);
        Assert.True(items.Count > 0);
        var item = items.FirstOrDefault(candidate => candidate.Title.Contains("长公主", StringComparison.Ordinal)) ?? items[0];
        var episodes = await client.GetEpisodesAsync(item.BookId, CancellationToken.None);
        Assert.True(episodes.Count > 0);
        Assert.True(HongguoHighCrypto.TryDecodeEpisodeId(episodes[0].VideoId, out _, out _, out var videoId));
        var probe = await client.ProbePlaybackAsync(videoId, CancellationToken.None);
        Assert.True(probe.Count >= 5);
        Assert.True(probe.AllHaveUrl);
        Assert.True(probe.AllHaveSpade);
        var playback = await client.GetVideoPlaybackAsync(episodes[0].VideoId, "1080", CancellationToken.None);
        Assert.True(playback.Encrypted);
        Assert.True(playback.SpadeA.Length > 0);
#pragma warning disable xUnit2009
        Assert.True(playback.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
#pragma warning restore xUnit2009
        Assert.True(playback.EncryptedUrls.Count > 0);
        Assert.True(HongguoAppDirectClient.EmulatorChainUses >= used + 4);
    }

    [Fact]
    public void SignedMediaUrlRejectsBadCiphertext()
    {
        var shortCipher = Assert.Throws<HongguoHighException>(() =>
            HongguoSignedPlayback.DecryptMediaUrl("YQ==", [1, 2, 3]));
        Assert.Equal("链路 2 地址密文格式无效", shortCipher.Message);
        var badHeader = Convert.ToBase64String(new byte[20]);
        var header = Assert.Throws<HongguoHighException>(() =>
            HongguoSignedPlayback.DecryptMediaUrl(badHeader, [1, 2, 3]));
        Assert.Equal("链路 2 地址密文格式无效", header.Message);
        Assert.Equal(
            "链路 2 地址不受支持",
            Assert.Throws<HongguoHighException>(() =>
                HongguoSignedPlayback.RequireHttpsHost("http://v.fqnovelvod.com/a", HongguoSignedPlayback.VodDomains)).Message);
        var allowed = HongguoSignedPlayback.RequireHttpsHost("https://v.fqnovelvod.com/a", HongguoSignedPlayback.VodDomains);
        Assert.Contains("fqnovelvod.com", allowed, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedPlaybackIdentityRequiresBothNumbers()
    {
        Assert.True(HongguoGuestPlayback.IsSavedIdentity("1234567890123456", "1234567890123457"));
        Assert.False(HongguoGuestPlayback.IsSavedIdentity("", "1234567890123457"));
        Assert.False(HongguoGuestPlayback.IsSavedIdentity("12345", "1234567890123457"));
    }

    [Fact]
    public async Task BookIdQuerySkipsNetwork()
    {
        using var http = new HttpClient();
        var client = new HongguoAppDirectClient(http);
        var items = await client.SearchAsync("1234567890123456", 1, CancellationToken.None, DramaSearchCategory.BookId);
        Assert.Single(items);
        Assert.Equal("1234567890123456", items[0].BookId);
    }

    [Fact]
    public void NewReleaseBodyUsesLatestShelfWithinSevenDays()
    {
        var comic = Encoding.UTF8.GetString(HongguoAppDirectClient.LandpageBody("comic_series", 20));
        Assert.Contains("\"genre\":[\"comic_series\"]", comic, StringComparison.Ordinal);
        Assert.Contains("\"sort\":[\"online_time\"]", comic, StringComparison.Ordinal);
        Assert.Contains("\"online_time\":[\"days_7\"]", comic, StringComparison.Ordinal);
        Assert.Contains("\"offset\":20", comic, StringComparison.Ordinal);
        Assert.Contains("\"session_id\":\"\"", comic, StringComparison.Ordinal);
        var continued = Encoding.UTF8.GetString(
            HongguoAppDirectClient.LandpageBody("ai_series", 20, "20261101161659E2CC17C340F09FAB0FB3"));
        Assert.Contains("\"session_id\":\"20261101161659E2CC17C340F09FAB0FB3\"", continued, StringComparison.Ordinal);
        Assert.Contains("\"offset\":20", continued, StringComparison.Ordinal);

        var ai = Encoding.UTF8.GetString(HongguoAppDirectClient.LandpageBody("ai_series", 0));
        Assert.Contains("\"genre\":[\"ai_series\"]", ai, StringComparison.Ordinal);
        Assert.Contains("\"sort\":[\"online_time\"]", ai, StringComparison.Ordinal);
        Assert.Contains("\"online_time\":[\"days_7\"]", ai, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => HongguoAppDirectClient.LandpageBody("short_play", 0));
    }

    [Fact]
    public void NewReleasePageReadsSeriesAndStopsWhenServerHasNoMore()
    {
        var root = JsonNode.Parse("""
            {
              "code": 0,
              "data": {
                "has_more": false,
                "cell_list": [
                  {
                    "video_data": {
                      "series_id": "1234567890123456789",
                      "series_title": "测试漫剧",
                      "episode_cnt": 12
                    }
                  }
                ]
              }
            }
            """)!;
        var items = HongguoAppDirectClient.ReadLandpageItems(root);
        Assert.Single(items);
        Assert.Equal("1234567890123456789", items[0].BookId.Split(':')[^1]);
        Assert.Equal("测试漫剧", items[0].Title);
        Assert.Equal(12, items[0].EpisodeTotal);
        Assert.False(HongguoAppDirectClient.LandpageHasMore(root, items.Count));
        var cursor = HongguoAppDirectClient.ReadLandpagePaging(root, 0, items.Count);
        Assert.False(cursor.HasMore);
        Assert.Equal(items.Count, cursor.NextOffset);
    }

    [Fact]
    public void EmulatorChainUsesInstalledVersionAndEncodedResolution()
    {
        var url = HongguoAppDirectClient.BuildEmulatorUrl(
            HongguoAppDirectClient.PlaybackHost,
            "/novel/player/multi_video_model/preload/v1/",
            null,
            "1234567890123456",
            "1234567890123457",
            1700000000000);
        Assert.StartsWith(
            "https://api5-normal-sinfonlineb.fqnovel.com/novel/player/multi_video_model/preload/v1/?",
            url,
            StringComparison.Ordinal);
        Assert.Contains("version_code=74332", url, StringComparison.Ordinal);
        Assert.Contains("version_name=7.4.3.32", url, StringComparison.Ordinal);
        Assert.Contains("device_type=sdk_gphone64_arm64", url, StringComparison.Ordinal);
        Assert.Contains("os_version=13", url, StringComparison.Ordinal);
        Assert.Contains("resolution=1080%2A2337", url, StringComparison.Ordinal);
        Assert.Contains("device_id=1234567890123456", url, StringComparison.Ordinal);
        Assert.Contains("iid=1234567890123457", url, StringComparison.Ordinal);
        Assert.EndsWith("_rticket=1700000000000", url, StringComparison.Ordinal);
        Assert.DoesNotContain('*', url);

        var episodes = HongguoAppDirectClient.BuildEmulatorUrl(
            HongguoAppDirectClient.ReadingHost,
            "/novel/player/multi_video_detail/preload/v1",
            null,
            "1234567890123456",
            "1234567890123457",
            1700000000000);
        Assert.StartsWith("https://reading.snssdk.com/novel/player/multi_video_detail/preload/v1?", episodes, StringComparison.Ordinal);

        var body = Encoding.UTF8.GetString(HongguoAppDirectClient.EmulatorPlaybackBody("7600111222333444555"));
        Assert.Contains("\"dr_scene\":\"preload\"", body, StringComparison.Ordinal);
        Assert.Contains("\"1004\":[\"7600111222333444555\"]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorPrefsKeepACompleteIdentity()
    {
        const string xml = """
            <map>
              <string name="device_id">1234567890123456</string>
              <string name="install_id">1234567890123457</string>
              <string name="klink_egdi">hidden</string>
            </map>
            """;
        Assert.True(HongguoAppEmulatorIdentity.TryParse(xml, out var deviceId, out var installId));
        Assert.Equal("1234567890123456", deviceId);
        Assert.Equal("1234567890123457", installId);
        Assert.False(HongguoAppEmulatorIdentity.TryParse(
            "<map><string name=\"device_id\">0</string><string name=\"install_id\">1234567890123457</string></map>",
            out _,
            out _));
    }

    [Fact]
    public void NewReleasePagingKeepsSessionAndNextOffset()
    {
        var root = JsonNode.Parse("""
            {
              "code": 0,
              "data": {
                "has_more": true,
                "next_offset": 20,
                "session_id": "20261101161659E2CC17C340F09FAB0FB3",
                "video_data": []
              }
            }
            """)!;
        var cursor = HongguoAppDirectClient.ReadLandpagePaging(root, 0, 20);
        Assert.True(cursor.HasMore);
        Assert.Equal(20, cursor.NextOffset);
        Assert.Equal("20261101161659E2CC17C340F09FAB0FB3", cursor.SessionId);
    }

}
