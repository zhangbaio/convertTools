using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSeriesListLookupRetryTests
{
    [Fact]
    public void Stable_duplicate_rows_are_safe_after_two_identical_scans()
    {
        var first = Attempt(
            uniqueCount: 721,
            expectedTotal: 722,
            rawRows: 722,
            skippedRows: 0,
            duplicates: new Dictionary<string, int> { ["id:1234567890123456"] = 1 });
        var second = Attempt(
            uniqueCount: 721,
            expectedTotal: 722,
            rawRows: 722,
            skippedRows: 0,
            duplicates: new Dictionary<string, int> { ["id:1234567890123456"] = 1 });

        TikTokSeriesListLookupService
            .HasStableDuplicateOnlyShortfall(first, second)
            .Should().BeTrue();
    }

    [Fact]
    public void A_skipped_row_must_never_be_treated_as_a_stable_duplicate()
    {
        var first = Attempt(721, 722, 722, 1, new Dictionary<string, int>());
        var second = Attempt(721, 722, 722, 1, new Dictionary<string, int>());

        TikTokSeriesListLookupService
            .HasStableDuplicateOnlyShortfall(first, second)
            .Should().BeFalse();
    }

    [Fact]
    public void Different_duplicate_keys_indicate_a_moving_list_and_must_fail_closed()
    {
        var first = Attempt(
            721, 722, 722, 0,
            new Dictionary<string, int> { ["id:1111111111111111"] = 1 });
        var second = Attempt(
            721, 722, 722, 0,
            new Dictionary<string, int> { ["id:2222222222222222"] = 1 });

        TikTokSeriesListLookupService
            .HasStableDuplicateOnlyShortfall(first, second)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(214, 50, 1, 50)]
    [InlineData(214, 50, 4, 50)]
    [InlineData(214, 50, 5, 14)]
    [InlineData(10, 50, 1, 10)]
    public void Expected_row_count_matches_real_pagination(
        int total,
        int pageSize,
        int pageNumber,
        int expected)
    {
        TikTokSeriesListLookupService.ExpectedRowCount(total, pageSize, pageNumber)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(214, 50, 5)]
    [InlineData(200, 50, 4)]
    [InlineData(1, 50, 1)]
    [InlineData(808, 10, 81)]
    public void Expected_page_count_matches_footer_pagination(
        int total,
        int pageSize,
        int expectedPages)
    {
        TikTokSeriesListLookupService.ExpectedPageCount(total, pageSize)
            .Should().Be(expectedPages);
    }

    [Fact]
    public void Transitional_last_page_rows_are_rejected_on_a_full_page()
    {
        TikTokSeriesListLookupService.IsPageReadinessSampleAcceptable(
                expectedPageNumber: 4,
                activePageNumber: 4,
                expectedVisibleRowCount: 50,
                actualVisibleRowCount: 14,
                previousFingerprint: "page-3",
                currentFingerprint: "page-5")
            .Should().BeFalse();
    }

    [Fact]
    public void Stale_previous_page_fingerprint_is_rejected_even_after_page_number_changes()
    {
        TikTokSeriesListLookupService.IsPageReadinessSampleAcceptable(
                expectedPageNumber: 5,
                activePageNumber: 5,
                expectedVisibleRowCount: 14,
                actualVisibleRowCount: 14,
                previousFingerprint: "same-14-rows",
                currentFingerprint: "same-14-rows")
            .Should().BeFalse();
    }

    [Fact]
    public void Complete_expected_page_sample_is_accepted_for_stability_tracking()
    {
        TikTokSeriesListLookupService.IsPageReadinessSampleAcceptable(
                expectedPageNumber: 5,
                activePageNumber: 5,
                expectedVisibleRowCount: 14,
                actualVisibleRowCount: 14,
                previousFingerprint: "page-4",
                currentFingerprint: "page-5")
            .Should().BeTrue();
    }

    [Fact]
    public void Status_filter_requires_selected_set_to_match_requested_categories()
    {
        TikTokSeriesListLookupService.IsExactStatusSelection(
                ["视频检测中", "已发布"],
                ["已发布", "视频检测中"])
            .Should().BeTrue();
        TikTokSeriesListLookupService.IsExactStatusSelection(
                ["已发布", "视频检测中", "审核中"],
                ["已发布", "视频检测中"])
            .Should().BeFalse("存在未请求的额外分类");
        TikTokSeriesListLookupService.IsExactStatusSelection(
                ["已发布"],
                ["已发布", "视频检测中"])
            .Should().BeFalse("缺少用户请求的分类");
        TikTokSeriesListLookupService.IsExactStatusSelection(
                [],
                ["视频检测中"])
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(
        "疑似版权问题",
        TikTokCopyrightProofAuditSelection.CopyrightSuspectedStatus)]
    [InlineData(
        TikTokCopyrightProofAuditSelection.CopyrightSuspectedStatus,
        "疑似版权问题")]
    public void Copyright_suspected_filter_accepts_localized_label_and_platform_token(
        string selected,
        string expected)
    {
        TikTokSeriesListLookupService.IsExactStatusSelection([selected], [expected])
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("+1", true)]
    [InlineData("+12", true)]
    [InlineData("已发布", false)]
    [InlineData("contentPartnerHub_seriesPage_copyrightSuspected", false)]
    public void Collapsed_multi_select_summary_is_not_treated_as_a_real_filter_value(
        string value,
        bool expected)
    {
        TikTokSeriesListLookupService.HasCollapsedSelectionSummary([value])
            .Should().Be(expected);
    }

    [Fact]
    public void Page_readiness_snapshot_deserializes_browser_json_object()
    {
        var snapshot = TikTokSeriesListLookupService.ParsePageReadinessSnapshot(
            """
            {
              "activePageNumber": 2,
              "visibleRowCount": 43,
              "fingerprint": "id-1|id-2",
              "rangeText": "显示第 51 条-第 93 条，共 93 条"
            }
            """);

        snapshot.ActivePageNumber.Should().Be(2);
        snapshot.VisibleRowCount.Should().Be(43);
        snapshot.Fingerprint.Should().Be("id-1|id-2");
        snapshot.RangeText.Should().Contain("51 条-第 93 条");
    }

    private static TikTokSeriesListEnumerationAttempt Attempt(
        int uniqueCount,
        int expectedTotal,
        int rawRows,
        int skippedRows,
        IReadOnlyDictionary<string, int> duplicates) =>
        new(
            Enumerable.Range(1, uniqueCount)
                .Select(index => new TikTokSeriesListRow(
                    $"剧集{index}",
                    "已发布",
                    index.ToString("D16"),
                    $"https://example.test/series/detail/{index:D16}",
                    string.Empty))
                .ToArray(),
            expectedTotal,
            rawRows,
            skippedRows,
            duplicates,
            "“下一页”按钮已禁用");
}
