using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPublishInfoServiceTests
{
    [Fact]
    public void Catalog_contains_official_audience_category_and_plot()
    {
        Assert.Contains(KuaishouFormCatalog.Audience, option => option.Label == "男频" && option.Value == 1);
        Assert.Contains(KuaishouFormCatalog.Categories, option => option.Label == "甜宠");
        Assert.Contains(KuaishouFormCatalog.Plots, option => option.Label == "豪门赘婿" && option.Value == 1);
    }

    [Fact]
    public void Title_budget_reserves_room_for_the_alias()
    {
        var config = new KuaishouPersonalConfig
        {
            NewTitleAliasEnabled = true,
            NewTitleAliasValue = "短剧",
            NewTitleAliasPosition = "prefix",
        };

        Assert.Equal(8, KuaishouPublishInfoService.TitleBudget(config));
        Assert.Equal("短剧归途", KuaishouPublishInfoService.ApplyAlias("归途", config));
    }

    [Fact]
    public void Validate_accepts_one_response_and_rejects_a_short_synopsis_or_unknown_option()
    {
        var config = new KuaishouPersonalConfig();
        var valid = Json("""
            {"title":"归途灯火","short_title":"灯火再亮起来","description":"DESCRIPTION","tags":["家庭","误会","团圆"],"audience":"女频","categories":["家庭"],"plots":["守护家人"]}
            """.Replace("DESCRIPTION", Synopsis()));

        Assert.Empty(KuaishouPublishInfoService.Validate(valid, config, "原来的剧名", "原来的简介。", 10, true, true, ""));

        var shortSynopsis = Json("""
            {"title":"归途灯火","short_title":"灯火再亮起来","description":"太短了。","tags":["家庭","误会","团圆"],"audience":"女","categories":["不存在"],"plots":["守护家人"]}
            """);
        var issues = KuaishouPublishInfoService.Validate(shortSynopsis, config, "原来的剧名", "原来的简介。", 10, true, true, "");
        Assert.Contains(issues, issue => issue.Contains("简介", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("短剧分类", StringComparison.Ordinal));
    }

    [Fact]
    public void Upsert_info_writes_publish_fields_and_drops_the_tagline()
    {
        var updated = KuaishouPublishInfoService.UpsertInfo(
            "原剧名：旧剧\n新剧名：旧剧\n推荐语：旧推荐\n简介：旧简介\n",
            new Dictionary<string, string>
            {
                ["新剧名"] = "归途灯火",
                ["短标题"] = "灯火再亮起来",
                ["简介"] = "新的简介。",
                ["标签"] = "家庭 团圆",
            },
            ["推荐语"]);

        Assert.Contains("新剧名：归途灯火", updated);
        Assert.Contains("短标题：灯火再亮起来", updated);
        Assert.Contains("标签：家庭 团圆", updated);
        Assert.DoesNotContain("推荐语", updated);
        Assert.Contains("原剧名：旧剧", updated);
    }

    private static string Synopsis()
    {
        var parts = new List<string> { "林晚和哥哥因为一场误会分开，后来在追查旧账时重新理解彼此。" };
        var index = 1;
        while (string.Concat(parts).Length < 100)
            parts.Add($"第{index++}次对话让他们看清了当年不同的选择。");
        return string.Concat(parts);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
