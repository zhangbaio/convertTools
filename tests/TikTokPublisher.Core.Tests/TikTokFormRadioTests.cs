using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

// 选项文本取自 2026-10 改版后的编辑页：原开关/下拉被替换为 semi-radioGroup。
public sealed class TikTokFormRadioTests
{
    [Theory]
    [InlineData(new[] { "已开启", "未开启" }, "未开启", 1)]
    [InlineData(new[] { "已开启", "未开启" }, "已开启", 0)]
    [InlineData(new[] { "是", "否" }, "否", 1)]
    [InlineData(new[] { "开启", "关闭" }, "开启", 0)]
    [InlineData(new[] { "原创", "成片重制", "小说改编" }, "小说改编", 2)]
    public void MatchesOptionByExactText(string[] options, string label, int expected)
    {
        TikTokBrowserActions.ResolveRadioOptionIndex(options, [label], fallbackIndex: 0)
            .Should().Be(expected);
    }

    [Fact]
    public void ExactMatchWinsOverSubstring()
    {
        // “开启”是“已开启”和“未开启”的子串，不能误选第一个包含它的选项。
        TikTokBrowserActions.ResolveRadioOptionIndex(["已开启", "未开启", "开启"], ["开启"], fallbackIndex: 0)
            .Should().Be(2);
        TikTokBrowserActions.ResolveRadioOptionIndex(["已开启", "未开启"], ["开启"], fallbackIndex: 0)
            .Should().Be(-1, "子串命中多个选项时有歧义，必须报错");
    }

    [Fact]
    public void UsesAliasesForTargetAudience()
    {
        TikTokBrowserActions.ResolveRadioOptionIndex(["Female", "Male"], ["女性", "女", "Female"], fallbackIndex: 0)
            .Should().Be(0);
    }

    [Fact]
    public void FallsBackToPositionOnlyWhenPageShowsRawI18nKeys()
    {
        string[] raw = ["contentPartnerHub_common_yes", "contentPartnerHub_common_no"];
        TikTokBrowserActions.ResolveRadioOptionIndex(raw, ["否"], fallbackIndex: 1).Should().Be(1);

        TikTokBrowserActions.ResolveRadioOptionIndex(["甲", "乙"], ["否"], fallbackIndex: 1)
            .Should().Be(-1, "已翻译但没有目标选项时必须报错，不能盲点");
    }
}
