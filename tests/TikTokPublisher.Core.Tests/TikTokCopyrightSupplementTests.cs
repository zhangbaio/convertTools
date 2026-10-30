using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

// 结构取自 2026-10 改版后 /api/content-partner/series/{id} 的真实响应（文件字段已精简）。
public sealed class TikTokCopyrightSupplementTests
{
    private const string SeriesJson =
        """
        {"code":0,"data":{"collection":{"status":6,"config":{"copyrightProof":{
          "isAdaptation":false,"isOriginalRightsHolder":true,"commitmentSigned":true,
          "materials":[
            {"materialType":2,"files":[{"fileName":"证明材料.pdf"}]},
            {"materialType":4,"files":[{"fileName":"可信时间戳认证证书.pdf"}]},
            {"materialType":6,"files":[{"fileName":"01.png"},{"fileName":"02.png"}]},
            {"materialType":7,"files":[]},
            {"materialType":99,"files":[{"fileName":"x"}]}
          ]}}}}}
        """;

    [Fact]
    public void ParsesMaterialFileCountsByAppKey()
    {
        var saved = TikTokBrowserActions.ParseSeriesCopyrightMaterials(SeriesJson);

        saved.FileCounts[TikTokPublishConstants.ProductionAgreementMaterialType].Should().Be(1);
        saved.FileCounts[TikTokPublishConstants.FilingOrDistributionLicenseMaterialType].Should().Be(1);
        saved.FileCounts[TikTokPublishConstants.AiGenerationScreenshotsMaterialType].Should().Be(2);
        saved.FileCounts[TikTokPublishConstants.EditingProjectFilesMaterialType].Should().Be(0);
        saved.UnknownApiTypes.Should().Equal(99);
    }

    [Fact]
    public void ReportsMissingConfiguredAndLeftoverUnconfiguredMaterials()
    {
        var saved = TikTokBrowserActions.ParseSeriesCopyrightMaterials(SeriesJson);
        string[] configured =
        [
            TikTokPublishConstants.ProductionAgreementMaterialType,
            TikTokPublishConstants.EditingProjectFilesMaterialType,
        ];

        var full = TikTokBrowserActions.DescribeCopyrightMaterialMismatch(saved, configured, requireFullRebuild: true);
        full.Should().Contain("缺少：剪辑工程文件")
            .And.Contain("AI 生成过程截图")
            .And.Contain("网络剧片备案");

        var incremental = TikTokBrowserActions.DescribeCopyrightMaterialMismatch(saved, configured, requireFullRebuild: false);
        incremental.Should().Be("缺少：剪辑工程文件");
    }

    [Fact]
    public void PassesWhenSavedMaterialsMatchConfiguration()
    {
        var saved = TikTokBrowserActions.ParseSeriesCopyrightMaterials(SeriesJson);
        string[] configured =
        [
            TikTokPublishConstants.ProductionAgreementMaterialType,
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
        ];

        TikTokBrowserActions.DescribeCopyrightMaterialMismatch(saved, configured, requireFullRebuild: true)
            .Should().BeNull();
    }

    [Fact]
    public void NonZeroApiCodeIsAnError()
    {
        var act = () => TikTokBrowserActions.ParseSeriesCopyrightMaterials("""{"code":401}""");
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://www.tiktokdramacenter.com/series/detail/7656996258725598229", "7656996258725598229")]
    [InlineData("https://www.tiktokdramacenter.com/series/7656996258725598229/detail", "7656996258725598229")]
    [InlineData("https://www.tiktokdramacenter.com/series/draft/7702262810893947925?x=1", "7702262810893947925")]
    [InlineData("https://www.tiktokdramacenter.com/series/list", "")]
    public void ExtractsSeriesIdFromOldAndNewUrls(string url, string expected)
    {
        TikTokBrowserActions.ExtractSeriesIdFromUrl(url).Should().Be(expected);
    }
}
