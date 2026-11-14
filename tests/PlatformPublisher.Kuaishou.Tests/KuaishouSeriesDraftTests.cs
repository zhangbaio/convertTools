using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouSeriesDraftTests
{
    [Fact]
    public void Broadcast_date_uses_china_midnight()
    {
        var timestamp = KuaishouSeriesDraft.ChinaMidnightUnixMilliseconds(new DateTime(2026, 1, 1));
        Assert.Equal(1767196800000, timestamp);
    }

    [Fact]
    public void Catalog_maps_tags_and_plots_from_the_operator_payload()
    {
        var catalog = KuaishouSeriesCatalog.Parse("""
            {"result":1,"data":{"miniSeriesTagIdList":[{"tagId":6,"tagName":"家庭"}],"plotList":[{"plotId":1,"plotDesc":"豪门赘婿"}]}}
            """);
        Assert.Equal(6, catalog.RequireTag("家庭"));
        Assert.Equal(1, catalog.RequirePlot("豪门赘婿"));
        Assert.Equal(2, KuaishouSeriesDraft.AudienceGenderCode("女"));
        Assert.Equal(1, KuaishouSeriesDraft.AudienceGenderCode("男频"));
    }

    [Fact]
    public void Create_payload_writes_actors_date_and_shared_episode_covers_without_ids()
    {
        using var document = JsonDocument.Parse(KuaishouSeriesDraft.Build(Sample(creating: true)));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("miniSeriesId", out _));
        Assert.Equal("女", root.GetProperty("mainActorList")[1].GetProperty("gender").GetString());
        Assert.Equal("沈清禾", root.GetProperty("mainActorList")[1].GetProperty("actorName").GetString());
        Assert.Equal(1767196800000, root.GetProperty("productionDate").GetInt64());
        Assert.Equal("快手", root.GetProperty("displayInfoList")[0].GetProperty("displayPlatform").GetString());
        Assert.Equal(2, root.GetProperty("seriesContentType").GetInt32());
        Assert.Equal(3, root.GetProperty("productMethod").GetInt32());
        Assert.Equal(4, root.GetProperty("copyrightProofMaterialInfoList")[0].GetProperty("materialType").GetInt32());
        Assert.Equal(4, root.GetProperty("copyrightProofMaterialInfoList")[0].GetProperty("fileKeyList").GetArrayLength());
        Assert.Equal("pdf", root.GetProperty("copyrightProofMaterialInfoList")[0].GetProperty("commitmentLetterKeyList")[0].GetString());
        var episode = root.GetProperty("episodes")[0];
        Assert.False(episode.TryGetProperty("episodeId", out _));
        Assert.Equal("episode-cover", episode.GetProperty("coverImgKey").GetString());
        Assert.True(episode.GetProperty("free").GetBoolean());
        Assert.False(root.GetProperty("episodes")[1].GetProperty("free").GetBoolean());
        Assert.Equal(2, root.GetProperty("audienceGender").GetInt32());
    }

    [Fact]
    public void Field_lines_print_each_submitted_form_value()
    {
        var lines = KuaishouSeriesDraft.FieldLines(Sample(creating: true), new KuaishouSeriesFieldLog(
            "漫剧",
            "AIGC剧",
            "含AI生成内容",
            "自有版权",
            "观看广告解锁",
            "1",
            "0.11",
            "2026-11-14",
            2026,
            ["年代", "都市"],
            ["豪门赘婿"],
            @"E:\covers\横屏封面.jpg",
            @"E:\covers\竖屏海报.jpg",
            [@"E:\img\工程图_1.png", @"E:\img\工程图_2.png", @"E:\img\工程图_3.png", @"E:\img\工程图_4.png"],
            @"E:\pdf\承诺函.pdf",
            2));

        Assert.Contains("短剧名称：双生江湖行", lines);
        Assert.Contains("内容类型：漫剧", lines);
        Assert.Contains("漫剧制作方式：AIGC剧", lines);
        Assert.Contains("短剧分类：年代、都市", lines);
        Assert.Contains("情节：豪门赘婿", lines);
        Assert.Contains("男女频道：女频", lines);
        Assert.Contains("制作形式：横屏", lines);
        Assert.Contains("播出时间：2026-11-14", lines);
        Assert.Contains("售卖类型：观看广告解锁", lines);
        Assert.Contains("广告解锁集数：1", lines);
        Assert.Contains("单集价格：0.11 元", lines);
        Assert.Contains("整剧价格：1 元", lines);
        Assert.Contains("演员2：沈清禾 / 女 / 女主", lines);
        Assert.Contains("短剧封面：横屏封面.jpg", lines);
        Assert.Contains("承诺函：承诺函.pdf", lines);

        var personal = KuaishouSeriesDraft.FieldLines(Sample(creating: true) with
        {
            Enterprise = false,
            CreditName = "星燃",
            CreditGender = "男",
            AudienceGender = null,
            PlotIds = [],
        }, new KuaishouSeriesFieldLog(
            "漫剧", "AIGC剧", "含AI生成内容", "自有版权", "观看广告解锁",
            "1", "0.11", "2026-11-14", 2026, ["都市"], [],
            "cover.jpg", "episode.jpg", ["1.png", "2.png", "3.png", "4.png"], "承诺函.pdf", 2));
        Assert.Contains("导演：星燃 / 男", personal);
        Assert.Contains("男女频道：未选择", personal);
        Assert.Contains("情节：未选择", personal);
    }

    [Fact]
    public void Personal_plot_list_includes_the_saved_audience_channel()
    {
        using var document = JsonDocument.Parse(KuaishouSeriesDraft.Build(Sample(creating: true) with
        {
            Enterprise = false,
            PlotIds = [8, 22, 28],
            AudienceGender = 1,
        }));
        Assert.Equal(1, document.RootElement.GetProperty("audienceGender").GetInt32());

        var missing = Sample(creating: true) with { Enterprise = false, PlotIds = [8], AudienceGender = null };
        var error = Assert.Throws<InvalidOperationException>(() => KuaishouSeriesDraft.Build(missing));
        Assert.Contains("男女频道", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_payload_keeps_the_existing_episode_ids()
    {
        using var document = JsonDocument.Parse(KuaishouSeriesDraft.Build(Sample(creating: false)));
        var episode = document.RootElement.GetProperty("episodes")[1];
        Assert.Equal(55987933, document.RootElement.GetProperty("miniSeriesId").GetInt64());
        Assert.Equal(102, episode.GetProperty("episodeId").GetInt64());
    }

    [Fact]
    public void Existing_series_is_matched_by_exact_title_and_episode_order()
    {
        using var listed = JsonDocument.Parse("""
            {"result":1,"data":{"data":[{"courseName":"别的剧","miniSeriesId":1,"auditStatus":1},{"courseName":"双生江湖行","miniSeriesId":55987933,"auditStatus":1}]}}
            """);
        var matches = KuaishouSeriesDraft.ReadMatchingSeries(listed.RootElement, "双生江湖行");
        Assert.Equal("55987933", Assert.Single(matches).Id);
        Assert.Equal(1, matches[0].AuditStatus);

        using var detail = JsonDocument.Parse("""
            {"result":1,"data":{"episodeList":[{"orderNo":1,"episodeId":102},{"orderNo":0,"episodeId":101}]}}
            """);
        Assert.Equal(["101", "102"], KuaishouSeriesDraft.ReadEpisodeIds(detail.RootElement));
    }

    [Fact]
    public void Daily_cap_and_short_intro_stop_before_another_create()
    {
        using var denied = JsonDocument.Parse("""{"result":0,"error_msg":"您今日的短剧上传已达上限"}""");
        Assert.Throws<KuaishouDailyUploadLimitException>(() => KuaishouSeriesDraft.EnsureSuccess(denied.RootElement));

        var input = Sample(creating: true);
        input = input with { Description = "太短" };
        var error = Assert.Throws<InvalidOperationException>(() => KuaishouSeriesDraft.Build(input));
        Assert.Contains("100", error.Message, StringComparison.Ordinal);

        input = Sample(creating: true);
        input = input with { Actors = [new("顾承宇", "男", "陆沉"), new("沈清禾", "", "女主")] };
        error = Assert.Throws<InvalidOperationException>(() => KuaishouSeriesDraft.Build(input));
        Assert.Contains("第 2 位演员", error.Message, StringComparison.Ordinal);
    }

    private static KuaishouSeriesDraftInput Sample(bool creating) => new()
    {
        Title = "双生江湖行",
        Description = new string('剧', 120),
        Enterprise = true,
        Creating = creating,
        MiniSeriesId = creating ? "" : "55987933",
        SeriesContentType = 2,
        ProductMethod = 3,
        AuthorDeclaration = 1,
        CopyrightProofType = 1,
        SaleType = 3,
        PriceCents = 11,
        EpisodePriceCents = 100,
        UpdateEnd = true,
        SmallAmountUnlock = false,
        UnlockCount = 1,
        FreeEpisodeCount = 1,
        TagIds = [11, 12],
        PlotIds = [1],
        AudienceGender = 2,
        CoverImgKey = "cover",
        EpisodeCoverImgKey = "episode-cover",
        EpisodeTitles = ["双生江湖行", "双生江湖行"],
        EpisodeIds = creating ? [] : ["101", "102"],
        ProductionForm = "横屏",
        ProductionCost = 5,
        EpisodeAvgTime = 1,
        ProductionDate = KuaishouSeriesDraft.ChinaMidnightUnixMilliseconds(new DateTime(2026, 1, 1)),
        DisplayDate = KuaishouSeriesDraft.ChinaMidnightUnixMilliseconds(new DateTime(2026, 11, 14)),
        BroadcastPlatform = "快手",
        BroadcastChannel = "小屏APP",
        ProductionOrg = "武汉映正科技有限公司",
        CreditName = "张彪",
        CreditGender = "男",
        DirectorName = "张彪",
        DirectorGender = "男",
        ScreenwriterName = "张彪",
        ScreenwriterGender = "男",
        ProducerName = "张彪",
        ProducerGender = "男",
        Actors = [new("顾承宇", "男", "陆沉"), new("沈清禾", "女", "女主")],
        ProjectImageKeys = ["a", "b", "c", "d"],
        CommitmentKey = "pdf",
    };
}
