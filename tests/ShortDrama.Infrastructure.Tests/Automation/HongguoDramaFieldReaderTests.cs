using System.Text.Json.Nodes;
using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public class HongguoDramaFieldReaderTests
{
    [Fact]
    public void Read_Splits_Portrait_And_Genre_From_Content_Type()
    {
        var raw = JsonNode.Parse("""
            {
              "book_name": "大壮之重生卤菜大王",
              "book_id": "123",
              "author": "夜珠文化传媒",
              "actors": "大壮/贺静佳",
              "category": "漫剧",
              "tag_text": "竖屏 | 种田",
              "episode_cnt": 113,
              "read_count": 312000,
              "favorite_count": 372000,
              "duration": 8587,
              "first_online_time": "2026-07-05 04:28:00",
              "record_number": "（番茄）网微剧备字（2026）第507311号"
            }
            """)!.AsObject();

        var read = HongguoDramaFieldReader.Read(raw);

        read.Orientation.Should().Be("竖屏");
        read.Genre.Should().Be("种田");
        read.Fields.Should().Contain(field => field.Name == "内容类型" && field.Value == "漫剧");
        read.Fields.Should().Contain(field => field.Name == "作者" && field.Value == "夜珠文化传媒");
        read.Fields.Should().Contain(field => field.Name == "演员" && field.Value == "大壮/贺静佳");
        read.Fields.Should().Contain(field => field.Name == "集数" && field.Value == "113");
        read.Fields.Should().Contain(field => field.Name == "播放量" && field.Value == "31.2万");
        read.Fields.Should().Contain(field => field.Name == "收藏量" && field.Value == "37.2万");
        read.Fields.Should().Contain(field => field.Name == "时长" && field.Value == "2小时23分7秒");
        read.Fields.Should().Contain(field => field.Name == "上线时间" && field.Value == "2026-07-05 04:28:00");
        read.Fields.Should().Contain(field => field.Name == "备案号" && field.Value.Contains("第507311号"));
        read.Fields.Select(field => field.Name).Should().NotIntersectWith(["book_id", "cell_id", "作品ID"]);
    }

    [Fact]
    public void Read_Hides_Search_Cell_Metrics()
    {
        var raw = JsonNode.Parse("""
            {
              "text": "大壮之重生卤菜大王",
              "abstract": "肥肠在国营门口打开销路",
              "vertical": true,
              "episode": true,
              "play_cnt": 325753,
              "show_play_cnt": true,
              "cell_id": 1000,
              "show_type": 369,
              "content_type": 1004,
              "auto_play": false,
              "recommend_info": "{\"channel_id\":\"94349615599\"}",
              "search_result_id": "7658231391053679640"
            }
            """)!.AsObject();

        var read = HongguoDramaFieldReader.Read(raw);

        read.Orientation.Should().Be("竖屏");
        read.Fields.Should().Contain(field => field.Name == "剧名" && field.Value == "大壮之重生卤菜大王");
        read.Fields.Should().Contain(field => field.Name == "播放量" && field.Value == "32.6万");
        read.Fields.Should().Contain(field => field.Name == "简介" && field.Value.Contains("肥肠"));
        read.Fields.Select(field => field.Name).Should().NotIntersectWith(
            ["cell_id", "show_type", "content_type", "recommend_info", "search_result_id", "episode", "auto_play"]);
        read.Fields.Should().NotContain(field => field.Name == "集数");
        read.Fields.Should().NotContain(field => field.Name == "内容类型");
    }

    [Fact]
    public void Read_Uses_Dimensions_When_No_Orientation_Label_Exists()
    {
        var landscape = JsonNode.Parse("""{"category":"漫剧","video_width":1920,"video_height":1080}""")!.AsObject();
        var portrait = JsonNode.Parse("""
            {
              "video_vertical": 1,
              "video_width": 1920,
              "video_height": 1080,
              "category_dim_theme": [{"name":"种田"}]
            }
            """)!.AsObject();

        HongguoDramaFieldReader.Read(landscape).Orientation.Should().Be("横屏");
        HongguoDramaFieldReader.Read(landscape).Genre.Should().BeEmpty();
        var labeled = HongguoDramaFieldReader.Read(portrait);
        labeled.Orientation.Should().Be("竖屏");
        labeled.Genre.Should().Be("种田");
    }

    [Fact]
    public async Task LookupExactTitle_Keeps_Only_The_Same_Title()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient)
        {
            AuthedRequestForTests = (_, _, _, _, _) => Task.FromResult<JsonNode?>(new JsonObject
            {
                ["host"] = "api5-normal-sinfonlinea.fqnovel.com",
                ["path"] = "/reading/bookapi/search/tab/v",
                ["method"] = "GET",
            }),
            ExecuteSignedRequestForTests = (_, _, _, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""
                {
                  "data": {
                    "search_data": [
                      {
                        "book_id": "200",
                        "video_data": [{
                          "series_id": "200",
                          "title": "大壮之重生卤菜大王第二季",
                          "video_detail": {"episode_cnt": 10, "tag_text": "横屏 | 逆袭"}
                        }]
                      },
                      {
                        "book_id": "100",
                        "video_data": [{
                          "series_id": "100",
                          "title": "大壮之重生卤菜大王",
                          "authorName": "夜珠文化传媒",
                          "video_detail": {
                            "episode_cnt": 113,
                            "tag_text": "竖屏 | 种田",
                            "category": "漫剧"
                          }
                        }]
                      },
                      {
                        "book_id": "300",
                        "video_data": [{
                          "series_id": "300",
                          "title": "安猪下水",
                          "video_detail": {"episode_cnt": 8}
                        }]
                      }
                    ]
                  }
                }
                """)),
        };

        var hit = await service.LookupExactTitleAsync(
            new DramaSourceSettings { DramaSourceChain = "hghigh", HghighEdition = "standard" },
            "大壮之重生卤菜大王",
            CancellationToken.None);

        hit.Title.Should().Be("大壮之重生卤菜大王");
        hit.BookId.Should().Be("hghigh:100");
        hit.Orientation.Should().Be("竖屏");
        hit.Genre.Should().Be("种田");
        hit.Fields.Should().Contain(field => field.Name == "内容类型" && field.Value == "漫剧");
        hit.Fields.Should().Contain(field => field.Name == "作者" && field.Value == "夜珠文化传媒");
    }

    [Fact]
    public async Task LookupExactTitle_Does_Not_Substitute_A_Similar_Title()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient)
        {
            AuthedRequestForTests = (_, _, _, _, _) => Task.FromResult<JsonNode?>(new JsonObject()),
            ExecuteSignedRequestForTests = (_, _, _, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""
                {
                  "data": {
                    "search_data": [{
                      "book_id": "200",
                      "video_data": [{
                        "series_id": "200",
                        "title": "大壮之重生卤菜大王第二季",
                        "video_detail": {"episode_cnt": 10}
                      }]
                    }]
                  }
                }
                """)),
        };

        var hit = await service.LookupExactTitleAsync(
            new DramaSourceSettings { DramaSourceChain = "hghigh" },
            "大壮之重生卤菜大王",
            CancellationToken.None);

        hit.Title.Should().BeEmpty();
        hit.Candidates.Should().ContainSingle().Which.Should().Be("大壮之重生卤菜大王第二季");
    }

    private sealed class FailAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("目录请求失败");
    }
}
