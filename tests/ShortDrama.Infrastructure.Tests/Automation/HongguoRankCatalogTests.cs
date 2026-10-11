using System.Text.Json.Nodes;
using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoRankCatalogTests
{
    [Theory]
    [InlineData("漫剧", "热播榜", "comic_series_rank", "comic_series_hot_play")]
    [InlineData("漫剧", "推荐榜", "comic_series_rank", "comic_series_hot_rank")]
    [InlineData("漫剧", "新剧榜", "comic_series_rank", "comic_series_new_rank")]
    [InlineData("漫剧", "热搜榜", "comic_series_rank", "comic_series_hot_search")]
    [InlineData("全部", "热播榜", "all", "ranklist_hot_play_sc")]
    [InlineData("全部", "推荐榜", "all", "ranklist_hot_sc")]
    [InlineData("全部", "新剧榜", "all", "ranklist_new_rank_sc")]
    [InlineData("全部", "必看榜", "all", "ranklist_must_watch")]
    [InlineData("全部", "热搜榜", "all", "ranklist_hot_search_sc")]
    [InlineData("全部", "收藏榜", "all", "ranklist_followed")]
    [InlineData("真人剧", "推荐榜", "human", "human_hot_sc")]
    [InlineData("真人剧", "热播榜", "human", "human_hot_play")]
    [InlineData("真人剧", "收藏榜", "human", "human_followed")]
    [InlineData("AI剧", "热播榜", "ai_playlet", "ai_playlet_hot_play")]
    [InlineData("AI剧", "推荐榜", "ai_playlet", "ai_playlet_hot_sc")]
    [InlineData("AI剧", "必看榜", "ai_playlet", "ai_playlet_must_watch")]
    [InlineData("AI剧", "收藏榜", "ai_playlet", "ai_playlet_followed")]
    public void Resolve_UsesAssistantRankKeys(string category, string board, string selectedItems, string subSelectedItems)
    {
        HongguoRankCatalog.TryResolve(category, board, out var query).Should().BeTrue();
        query.SelectedItems.Should().Be(selectedItems);
        query.SubSelectedItems.Should().Be(subSelectedItems);
        HongguoRankCatalog.UnavailableReason(category, board).Should().BeNull();
    }

    [Theory]
    [InlineData("漫剧", "收藏榜", "漫剧没有收藏榜")]
    [InlineData("漫剧", "必看榜", "漫剧没有必看榜")]
    public void Resolve_RejectsComicBoardsWithoutKeys(string category, string board, string reason)
    {
        HongguoRankCatalog.TryResolve(category, board, out _).Should().BeFalse();
        HongguoRankCatalog.UnavailableReason(category, board).Should().Be(reason);
    }

    [Fact]
    public void ReadRankItems_MapsCellVideoPayload()
    {
        var payload = JsonNode.Parse("""
            {
              "cell_view": {
                "has_more": true,
                "next_offset": 10,
                "cell_data": [
                  {
                    "video_data": [
                      {
                        "series_id": "7123456789",
                        "title": "山野测试",
                        "episode_cnt": 80,
                        "play_cnt": 120000,
                        "followed_cnt": 3400,
                        "copyright": "作者甲",
                        "video_desc": "一段简介",
                        "cover": "https://example.com/poster.jpg"
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var items = HongguoHighCalendarMapper.ReadRankItems(payload);

        items.Should().ContainSingle();
        var item = items[0];
        item.BookId.Should().Be(HongguoHighCrypto.EnsureBookPrefix("7123456789"));
        item.Title.Should().Be("山野测试");
        item.EpisodeTotal.Should().Be(80);
        item.PlayCount.Should().Be(120000);
        item.FavoriteCount.Should().Be(3400);
        item.Author.Should().Be("作者甲");
        item.Intro.Should().Be("一段简介");
        item.PosterUrl.Should().Be("https://example.com/poster.jpg");
        var page = HongguoHighCalendarMapper.ReadRankPage(payload);
        page.HasMore.Should().BeTrue();
        page.NextOffset.Should().Be(10);
        page.Items.Should().ContainSingle();
    }

    [Fact]
    public void ReadRankPage_StopsWhenThePayloadHasNoNextPage()
    {
        var payload = JsonNode.Parse("""
            {
              "cell_view": {
                "has_more": false,
                "next_offset": 20,
                "cell_data": [
                  {
                    "video_data": [
                      { "series_id": "7000000002", "title": "最后一条" }
                    ]
                  }
                ]
              }
            }
            """);

        var page = HongguoHighCalendarMapper.ReadRankPage(payload);

        page.HasMore.Should().BeFalse();
        page.Items.Should().ContainSingle();
    }

    [Fact]
    public void TryMapItem_ReadsCardFavoriteOnlineTimeAndOrientation()
    {
        var payload = JsonNode.Parse("""
            {
              "series_id": "7123456789",
              "title": "烬九州：第二十九季",
              "category": "第47集",
              "favorite_cnt": 166000,
              "play_cnt": 1873000,
              "tag_text": "竖屏 | 快穿",
              "first_online_time": "2026-10-05 07:05"
            }
            """)!.AsObject();

        var item = HongguoHighCalendarMapper.TryMapItem(payload);

        item.Should().NotBeNull();
        item!.FavoriteCount.Should().Be(166000);
        item.PlayCount.Should().Be(1873000);
        item.Category.Should().Be("竖屏 | 快穿");
        item.PublishTime.Should().StartWith("2026-10-05 07:05");
    }

    [Fact]
    public void TryMapItem_ReadsFavoriteAndOnlineTimeFromCardText()
    {
        var payload = JsonNode.Parse("""
            {
              "series_id": "7000000001",
              "title": "烬九州",
              "type": "第47集",
              "sub_title_list": [
                "上线 2026-10-05 07:05",
                "140集 | 187.3万播放 | 16.6万收藏",
                "竖屏 | 快穿"
              ]
            }
            """)!.AsObject();

        var item = HongguoHighCalendarMapper.TryMapItem(payload);

        item.Should().NotBeNull();
        item!.FavoriteCount.Should().Be(166000);
        item.Category.Should().Be("竖屏 | 快穿");
        item.PublishTime.Should().StartWith("2026-10-05 07:05");
        item.PlayCount.Should().Be(0);
    }
}
