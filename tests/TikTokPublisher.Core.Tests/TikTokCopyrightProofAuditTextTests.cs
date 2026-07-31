using DocumentFormat.OpenXml.Packaging;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokCopyrightProofAuditTextTests
{
    [Fact]
    public void BuildDisplayText_OnlyListsMissingAndFailedTitlesWithoutPlatformIds()
    {
        var items = new[]
        {
            Item(1, "已有证明", TikTokCopyrightProofAuditState.HasMaterial, "7650000000000000001"),
            Item(2, "缺少证明", TikTokCopyrightProofAuditState.MissingMaterial, "7650000000000000002"),
            Item(3, "检查失败", TikTokCopyrightProofAuditState.Failed, "7650000000000000003", "标签页未加载"),
        };

        var text = TikTokCopyrightProofAuditText.BuildDisplayText(items);

        Assert.Contains("【未上传版权证明（1）】", text);
        Assert.Contains("缺少证明", text);
        Assert.Contains("【检查失败（1）】", text);
        Assert.Contains("检查失败　[标签页未加载]", text);
        Assert.DoesNotContain("已有证明", text);
        Assert.DoesNotContain("7650000000000000002", text);
    }

    [Fact]
    public void BuildMissingTitlesCopyText_OrdersAndDeduplicatesMissingTitles()
    {
        var items = new[]
        {
            Item(3, "剧集乙", TikTokCopyrightProofAuditState.MissingMaterial),
            Item(1, "剧集甲", TikTokCopyrightProofAuditState.MissingMaterial),
            Item(2, "剧集甲", TikTokCopyrightProofAuditState.MissingMaterial),
            Item(4, "已有证明", TikTokCopyrightProofAuditState.HasMaterial),
        };

        var text = TikTokCopyrightProofAuditText.BuildMissingTitlesCopyText(items);

        Assert.Equal($"剧集甲{Environment.NewLine}剧集乙", text);
    }

    [Fact]
    public void Export_CreatesReadableWorkbookWithAllAuditRows()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"copyright-proof-audit-{Guid.NewGuid():N}.xlsx");
        try
        {
            var result = TikTokCopyrightProofAuditExcelService.Export(
                "测试账号",
                [
                    Item(1, "缺少证明", TikTokCopyrightProofAuditState.MissingMaterial),
                    Item(2, "检查失败", TikTokCopyrightProofAuditState.Failed, detail: "页面超时"),
                ],
                outputPath);

            Assert.Equal(outputPath, result);
            Assert.True(File.Exists(outputPath));

            using var document = SpreadsheetDocument.Open(outputPath, false);
            var worksheetPart = document.WorkbookPart!.WorksheetParts.Single();
            var text = worksheetPart.Worksheet.InnerText;
            Assert.Contains("缺少证明", text);
            Assert.Contains("未上传证明", text);
            Assert.Contains("检查失败", text);
            Assert.Contains("页面超时", text);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static TikTokCopyrightProofAuditItem Item(
        int order,
        string title,
        TikTokCopyrightProofAuditState state,
        string seriesId = "",
        string detail = "") =>
        new(
            order,
            title,
            seriesId,
            $"https://www.tiktokdramacenter.com/series/detail/{seriesId}",
            state,
            detail,
            DateTimeOffset.Parse("2026-07-31T10:00:00+08:00"));
}
