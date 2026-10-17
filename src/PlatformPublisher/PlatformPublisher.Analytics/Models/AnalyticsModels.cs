using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Analytics.Models;

public enum AnalyticsRecordStatus
{
    Success = 0,
    Missing = 1,
    NotReady = 2,
    Failed = 3,
}

public sealed record AnalyticsAccount(
    string Id,
    PublishPlatform Platform,
    string Name,
    string SessionDirectory,
    string ConfigPath = "");

public sealed class AccountAnalyticsSnapshot
{
    public PublishPlatform Platform { get; init; }
    public string AccountId { get; init; } = "";
    public DateTimeOffset CollectedAt { get; init; }
    public long? VideoTotal { get; init; }
    public long? FollowerTotal { get; init; }
    public long? YesterdayNetFollowers { get; init; }
    public long? YesterdayViews { get; init; }
    public long? YesterdayLikes { get; init; }
    public long? YesterdayComments { get; init; }
    public int? ListedSeriesTotal { get; init; }
    public long? MountedVideoTotal { get; init; }
    public long? SeriesViewsTotal { get; init; }
    public long? AdMonetizationIncomeFen { get; init; }
    public long? YesterdayAdMonetizationIncomeFen { get; init; }
    public long? HeatingIncomeFen { get; init; }
    public long? MountedIncomeFen { get; init; }
    public long? EstimatedViolationDeductionFen { get; init; }
    public string RangeStart { get; init; } = "";
    public string RangeEnd { get; init; } = "";
}

public sealed class DailyAnalyticsRecord
{
    public PublishPlatform Platform { get; init; }
    public string AccountId { get; init; } = "";
    public DateOnly MetricDate { get; init; }
    public DateTimeOffset CollectedAt { get; init; }
    public AnalyticsRecordStatus Status { get; init; }
    public int? ListedSeriesTotal { get; init; }
    public long? MountedVideoTotal { get; init; }
    public long? SeriesViewsTotal { get; init; }
    public long? AdMonetizationIncomeFen { get; init; }
    public long? HeatingIncomeFen { get; init; }
    public long? MountedIncomeFen { get; init; }
    public long? EstimatedViolationDeductionFen { get; init; }
    public string Message { get; init; } = "";
}

public sealed class SubjectDailyAnalyticsRecord
{
    public PublishPlatform Platform { get; init; }
    public string AccountId { get; init; } = "";
    public string SubjectType { get; init; } = "";
    public string SubjectId { get; init; } = "";
    public string SubjectName { get; init; } = "";
    public DateOnly MetricDate { get; init; }
    public DateTimeOffset CollectedAt { get; init; }
    public AnalyticsRecordStatus Status { get; init; }
    public long? Views { get; init; }
    public long? Likes { get; init; }
    public long? Comments { get; init; }
    public long? Favorites { get; init; }
    public long? AdIncomeFen { get; init; }
    public string Message { get; init; } = "";
}

public sealed class PublishActivityRecord
{
    public string ActivityId { get; init; } = "";
    public PublishPlatform Platform { get; init; }
    public string AccountId { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string Status { get; init; } = "";
    public int ItemCount { get; init; }
}

public sealed record AnalyticsAccountRow(
    AnalyticsAccount Account,
    AccountAnalyticsSnapshot? Snapshot,
    int TaskCount,
    int Succeeded,
    int Failed,
    int Drafts,
    SubjectDailyAnalyticsRecord? KuaishouSummary);

public sealed record AnalyticsDailyPoint(DateOnly Date, int Succeeded, int Failed, int Drafts);

public sealed record AnalyticsSummary(
    int AccountCount,
    int PlatformCount,
    int TaskCount,
    int Succeeded,
    int Failed,
    int Drafts,
    long VideoTotal,
    long FollowerTotal,
    long YesterdayViews,
    long YesterdayLikes,
    long YesterdayComments,
    long WeixinIncomeFen,
    long KuaishouIncomeFen);

public sealed record AnalyticsDataset(
    AnalyticsSummary Summary,
    IReadOnlyList<AnalyticsAccountRow> Accounts,
    IReadOnlyList<AnalyticsDailyPoint> Daily,
    IReadOnlyList<DailyAnalyticsRecord> DailyMetrics,
    IReadOnlyList<SubjectDailyAnalyticsRecord> Subjects);
