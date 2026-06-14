using FluentAssertions;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class WeixinUploadMarkerMatcherTests
{
    [Fact]
    public void Evaluate_Should_Require_All_Expected_Files_For_Multi_File_Uploads()
    {
        var status = WeixinUploadMarkerMatcher.Evaluate(
            text: "工程图_1 工程图_2 重新选择 删除",
            linkTexts: [],
            expectedPaths:
            [
                "/tmp/工程图_1.png",
                "/tmp/工程图_2.png",
                "/tmp/工程图_3.png",
                "/tmp/工程图_4.png"
            ]);

        status.ExpectedCount.Should().Be(4);
        status.MatchedExpectedCount.Should().Be(2);
        status.HasAllMatches.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_Should_Treat_Single_File_Ui_As_Uploaded()
    {
        var status = WeixinUploadMarkerMatcher.Evaluate(
            text: "成本配置比例情况报告 重新选择 删除",
            linkTexts: [],
            expectedPaths:
            [
                "/tmp/成本报表.png"
            ]);

        status.ExpectedCount.Should().Be(1);
        status.HasUploadedUi.Should().BeTrue();
    }
}
