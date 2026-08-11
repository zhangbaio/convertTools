using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using TikTokPublisher.Core.Services.ProjectImages.FableCut;

namespace TikTokPublisher.Core.Tests;

public sealed class FableCutLoopbackServerTests
{
    [Theory]
    [InlineData("bytes=2-5", 10, 2, 5)]
    [InlineData("bytes=7-", 10, 7, 9)]
    [InlineData("bytes=-3", 10, 7, 9)]
    [InlineData("bytes=0-99", 10, 0, 9)]
    public void Range_parser_accepts_single_http_byte_ranges(
        string value,
        long size,
        long expectedStart,
        long expectedEnd)
    {
        FableCutLoopbackServer.TryParseRange(value, size, out var start, out var end)
            .Should().BeTrue();
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Theory]
    [InlineData("bytes=10-11", 10)]
    [InlineData("bytes=5-2", 10)]
    [InlineData("bytes=0-1,4-5", 10)]
    [InlineData("items=0-1", 10)]
    public void Range_parser_rejects_invalid_or_multi_ranges(string value, long size)
    {
        FableCutLoopbackServer.TryParseRange(value, size, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Server_exposes_project_static_assets_and_video_ranges_on_loopback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fablecut-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>ok</html>");
            var video = Path.Combine(root, "episode.mp4");
            await File.WriteAllBytesAsync(video, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
            await using var server = FableCutLoopbackServer.Start(
                root,
                video,
                "{\"name\":\"测试\",\"media\":[]}",
                "[]");
            using var http = new HttpClient();

            (await http.GetStringAsync(server.BaseUrl + "api/project")).Should().Contain("测试");
            (await http.GetStringAsync(server.BaseUrl)).Should().Contain("ok");

            // A plain fetch would make FableCut decode the whole source once per
            // synthetic mediaId. Only actual media-element/range reads are allowed.
            using var fullFetch = await http.GetAsync(server.BaseUrl + "media/episode");
            fullFetch.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

            using var mediaElementRequest = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "media/episode");
            mediaElementRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "video");
            using var mediaElementResponse = await http.SendAsync(mediaElementRequest);
            mediaElementResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await mediaElementResponse.Content.ReadAsByteArrayAsync()).Should().HaveCount(32);

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "media/episode");
            rangeRequest.Headers.Range = new RangeHeaderValue(4, 9);
            using var rangeResponse = await http.SendAsync(rangeRequest);
            rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            rangeResponse.Content.Headers.ContentRange!.From.Should().Be(4);
            rangeResponse.Content.Headers.ContentRange.To.Should().Be(9);
            (await rangeResponse.Content.ReadAsByteArrayAsync()).Should().Equal(4, 5, 6, 7, 8, 9);

            using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "media/episode");
            invalidRequest.Headers.TryAddWithoutValidation("Range", "bytes=99-100");
            using var invalidResponse = await http.SendAsync(invalidRequest);
            invalidResponse.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }
}
