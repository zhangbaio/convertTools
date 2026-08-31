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
