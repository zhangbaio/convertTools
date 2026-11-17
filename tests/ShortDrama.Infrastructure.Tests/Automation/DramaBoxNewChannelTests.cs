using System.Text.Json;
using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public class DramaBoxNewChannelTests
{
    [Fact]
    public void Finds_new_drama_channel_ahead_of_other_tabs()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "navigation": [
                  {"tabKey":"explore_feed","tabName":"Popular","channelId":10},
                  {"tabKey":"new_drama","tabName":"New","channelId":57,"contentEndpoint":"/drama-box/he001/theater"}
                ]
              }
            }
            """);

        DramaBoxApiService.FindNewChannelId(document.RootElement).Should().Be(57);
    }

    [Fact]
    public void Reads_english_new_page_fields()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "newTheaterList": {
                  "current": 1,
                  "pages": 3,
                  "records": [
                    {
                      "bookId": "42000030939",
                      "bookName": "Reborn to Build My Own Empire II",
                      "totalChapterNum": 120,
                      "playCount": "41.6K",
                      "bookShelfTime": "2026-11-13 13:47:08",
                      "tagV3s": [{"tagName":"Rebirth"},{"tagName":"Second Chance"}]
                    }
                  ]
                }
              }
            }
            """);

        var page = DramaBoxApiService.DescribeNewChannel(document.RootElement, 1);
        page.Count.Should().Be(1);
        page.HasMore.Should().BeTrue();
        page.Title.Should().Be("Reborn to Build My Own Empire II");
        page.EpisodeCount.Should().Be(120);
        page.PlayCount.Should().Be(41600);
        page.Category.Should().Be("Rebirth / Second Chance");
    }

    [Fact]
    public void Stops_when_new_page_is_the_last_page()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "newTheaterList": {
                  "current": 2,
                  "pages": 2,
                  "records": [{"bookId":"1","bookName":"Last"}]
                }
              }
            }
            """);

        DramaBoxApiService.DescribeNewChannel(document.RootElement, 2).HasMore.Should().BeFalse();
    }

    [Fact]
    public void Reads_search_books_from_book_list()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "bookList": [
                  {"bookId":"9","bookName":"Empire","chapterCount":50}
                ]
              }
            }
            """);

        DramaBoxApiService.CountSearchBooks(document.RootElement).Should().Be(1);
    }

    [Fact]
    public void Splits_curl_status_from_body()
    {
        var text = DramaBoxApiService.SplitCurlDump("HTTP/1.1 206 Partial Content\r\nContent-Length: 16\r\n\r\n{\"ok\":true}");
        text.Status.Should().Be(206);
        text.Body.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public void Swaps_the_two_drama_box_api_hosts()
    {
        const string signed = "https://sapi.dramaboxdb.com/drama-box/chapterv2/detail?timestamp=1";
        var alternate = DramaBoxApiService.AlternateDramaBoxHost(signed);
        alternate.Should().Be("https://sapi.dramaboxvideo.com/drama-box/chapterv2/detail?timestamp=1");
        DramaBoxApiService.AlternateDramaBoxHost(alternate!).Should().Be(signed);
        DramaBoxApiService.AlternateDramaBoxHost("https://api.iusc.cc/api/dr/sign").Should().BeNull();
    }
}
