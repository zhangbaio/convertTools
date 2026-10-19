namespace TikTokPublisher.Core.Models;

public sealed record TikTokAccountDailyStat(DateOnly Date, long TotalViews, long ValidViews);

public sealed record TikTokSeriesSnapshot(
    string SeriesId,
    string Title,
    DateOnly SnapshotDate,
    DateOnly? PublishedOn,
    long ValidViews,
    int AverageWatchSeconds,
    long Favorites,
    long Comments,
    long Likes);

public sealed record TikTokAccountRevenue(
    string TotalAmount,
    string PendingAmount,
    string PayingAmount,
    string CompletedAmount,
    string Currency,
    string CompanyName);

public sealed record TikTokSeriesGrowthPoint(
    DateOnly FromDate,
    DateOnly ToDate,
    long Growth,
    int SpanDays);
