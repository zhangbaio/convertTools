namespace TikTokPublisher.Core.Models;

public sealed record TikTokDailyAnalyticsRow(DateOnly Date, long TotalViews, long ValidViews);

public sealed record TikTokDailyAnalyticsReport(
    DateOnly ActualStart,
    DateOnly ActualEnd,
    DateOnly LatestEventDate,
    IReadOnlyList<TikTokDailyAnalyticsRow> Rows);
