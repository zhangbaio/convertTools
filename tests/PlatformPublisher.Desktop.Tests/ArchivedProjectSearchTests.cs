using PlatformPublisher.Desktop.ViewModels;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class ArchivedProjectSearchTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("潮间", true)]
    [InlineData("放生鱼", true)]
    [InlineData("快手分账个人", true)]
    [InlineData("archive\\meta", true)]
    [InlineData("不存在的剧", false)]
    public void Matches_project_original_title_new_title_account_or_directory(string query, bool expected)
    {
        var matched = ArchivedProjectSearch.Matches(
            query,
            "潮间奇遇",
            "放生鱼群被嘲笑",
            "潮间奇遇",
            "快手分账个人",
            @"E:\kuaishou\archive\meta\放生鱼群被嘲笑");

        Assert.Equal(expected, matched);
    }
}
