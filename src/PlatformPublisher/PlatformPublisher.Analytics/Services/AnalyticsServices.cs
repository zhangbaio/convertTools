using System.Text;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Analytics.Services;

public sealed class LocalPublishActivitySyncService
{
    private readonly AnalyticsRepository _repository;
    public LocalPublishActivitySyncService(AnalyticsRepository repository) => _repository = repository;

    public void Sync(IEnumerable<PublishJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (_repository.HasActivityPrefix("item:" + job.Id + ":"))
            {
                _repository.DeleteActivity("job:" + job.Id);
                continue;
            }
            var status = job.Status switch
            {
                PublishJobStatus.Succeeded => "success",
                PublishJobStatus.Failed => "failed",
                _ => "draft",
            };
            var count = job.Kind == PublishJobKind.Series ? 1 : Math.Max(1, job.PublishCount);
            _repository.UpsertActivity(new PublishActivityRecord
            {
                ActivityId = "job:" + job.Id,
                Platform = job.Platform,
                AccountId = job.AccountId,
                AccountName = job.AccountName,
                ProjectName = job.ProjectName,
                OccurredAt = job.LastCompletedAt ?? job.UpdatedAt,
                Status = status,
                ItemCount = count,
            });
        }
    }
}

public interface IAnalyticsActivitySink
{
    void Record(PublishJob job, string itemKey, string status, DateTimeOffset occurredAt);
}

public sealed class AnalyticsActivitySink : IAnalyticsActivitySink
{
    private readonly AnalyticsRepository _repository;
    private readonly PublishItemEventStore? _eventStore;
    public AnalyticsActivitySink(AnalyticsRepository repository, PublishItemEventStore? eventStore = null)
    {
        _repository = repository;
        _eventStore = eventStore;
    }
    public void Record(PublishJob job, string itemKey, string status, DateTimeOffset occurredAt)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(itemKey.ToLowerInvariant())))[..16];
        _repository.DeleteActivity("job:" + job.Id);
        _repository.UpsertActivity(new PublishActivityRecord
        {
            ActivityId = $"item:{job.Id}:{digest}", Platform = job.Platform, AccountId = job.AccountId,
            AccountName = job.AccountName, ProjectName = job.ProjectName, OccurredAt = occurredAt,
            Status = status is "success" ? "success" : status is "failed" ? "failed" : "draft", ItemCount = 1,
        });
        _eventStore?.Save(new PublishItemEvent(
            $"item:{job.Id}:{digest}", job.Id, job.AccountId, itemKey, status, string.Empty, occurredAt,
            new { job.ProjectName, job.Platform }));
    }
}

public sealed class NullAnalyticsActivitySink : IAnalyticsActivitySink
{
    public static NullAnalyticsActivitySink Instance { get; } = new();
    public void Record(PublishJob job, string itemKey, string status, DateTimeOffset occurredAt) { }
}

public sealed class AnalyticsQueryService
{
    private readonly AnalyticsRepository _repository;
    public AnalyticsQueryService(AnalyticsRepository repository) => _repository = repository;

    public AnalyticsDataset Query(IReadOnlyList<AnalyticsAccount> accounts, DateOnly from, DateOnly to,
        PublishPlatform? platform = null, string? keyword = null)
    {
        var token = keyword?.Trim() ?? string.Empty;
        var filtered = accounts.Where(account =>
                (!platform.HasValue || account.Platform == platform) &&
                (string.IsNullOrEmpty(token) || account.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var ids = filtered.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshots = _repository.ListSnapshots().Where(item => ids.Contains(item.AccountId)).ToArray();
        var dailyMetrics = _repository.ListDaily(from, to).Where(item => ids.Contains(item.AccountId)).ToArray();
        var subjects = _repository.ListSubjects(from, to).Where(item => ids.Contains(item.AccountId)).ToArray();
        var activities = _repository.ListActivities(from, to)
            .Where(item => ids.Contains(item.AccountId) && (!platform.HasValue || item.Platform == platform)).ToArray();

        var rows = filtered.Select(account =>
        {
            var accountActivities = activities.Where(item =>
                item.AccountId.Equals(account.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Platform == account.Platform).ToArray();
            var accountSubjectsAll = subjects.Where(item =>
                item.AccountId.Equals(account.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Platform == account.Platform &&
                item.Status == AnalyticsRecordStatus.Success).ToArray();
            var latestSubjectDate = accountSubjectsAll.Length == 0 ? (DateOnly?)null : accountSubjectsAll.Max(item => item.MetricDate);
            var accountSubjects = accountSubjectsAll.Where(item => item.MetricDate == latestSubjectDate).ToArray();
            SubjectDailyAnalyticsRecord? summary = accountSubjects.Length == 0 ? null : new SubjectDailyAnalyticsRecord
            {
                Platform = account.Platform, AccountId = account.Id, SubjectId = "account-total", SubjectName = account.Name,
                MetricDate = accountSubjects.Max(item => item.MetricDate), CollectedAt = accountSubjects.Max(item => item.CollectedAt),
                Status = AnalyticsRecordStatus.Success, Views = Sum(accountSubjects, item => item.Views),
                Likes = Sum(accountSubjects, item => item.Likes), Comments = Sum(accountSubjects, item => item.Comments),
                Favorites = Sum(accountSubjects, item => item.Favorites), AdIncomeFen = Sum(accountSubjects, item => item.AdIncomeFen),
            };
            return new AnalyticsAccountRow(account,
                snapshots.FirstOrDefault(item =>
                    item.AccountId.Equals(account.Id, StringComparison.OrdinalIgnoreCase) &&
                    item.Platform == account.Platform),
                accountActivities.Sum(item => item.ItemCount),
                accountActivities.Where(item => item.Status == "success").Sum(item => item.ItemCount),
                accountActivities.Where(item => item.Status == "failed").Sum(item => item.ItemCount),
                accountActivities.Where(item => item.Status == "draft").Sum(item => item.ItemCount), summary);
        }).ToArray();

        var daily = activities.GroupBy(item => DateOnly.FromDateTime(item.OccurredAt.LocalDateTime.Date))
            .OrderBy(group => group.Key)
            .Select(group => new AnalyticsDailyPoint(group.Key,
                group.Where(item => item.Status == "success").Sum(item => item.ItemCount),
                group.Where(item => item.Status == "failed").Sum(item => item.ItemCount),
                group.Where(item => item.Status == "draft").Sum(item => item.ItemCount))).ToArray();
        var summary = new AnalyticsSummary(
            filtered.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            filtered.Select(item => item.Platform).Distinct().Count(),
            rows.Sum(item => item.TaskCount), rows.Sum(item => item.Succeeded), rows.Sum(item => item.Failed), rows.Sum(item => item.Drafts),
            rows.Sum(item => item.Snapshot?.VideoTotal ?? 0), rows.Sum(item => item.Snapshot?.FollowerTotal ?? 0),
            rows.Sum(item => item.Snapshot?.YesterdayViews ?? item.KuaishouSummary?.Views ?? 0),
            rows.Sum(item => item.Snapshot?.YesterdayLikes ?? item.KuaishouSummary?.Likes ?? 0),
            rows.Sum(item => item.Snapshot?.YesterdayComments ?? item.KuaishouSummary?.Comments ?? 0),
            rows.Where(item => item.Account.Platform == PublishPlatform.WeixinChannel).Sum(item => item.Snapshot?.AdMonetizationIncomeFen ?? 0),
            subjects.Where(item => item.Platform == PublishPlatform.KuaishouPersonalRevenue && item.Status == AnalyticsRecordStatus.Success).Sum(item => item.AdIncomeFen ?? 0));
        return new AnalyticsDataset(summary, rows, daily, dailyMetrics, subjects);
    }

    private static long? Sum(IEnumerable<SubjectDailyAnalyticsRecord> values, Func<SubjectDailyAnalyticsRecord, long?> selector)
    {
        var resolved = values.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return resolved.Length == 0 ? null : resolved.Sum();
    }
}

public static class AnalyticsCsvExporter
{
    public static void Export(string path, AnalyticsDataset dataset)
    {
        var rows = new List<string[]> { new[] { "平台", "账号", "任务数", "成功", "失败", "草稿", "视频数", "关注者", "昨日播放", "昨日点赞", "昨日评论", "收入(元)", "最后采集" } };
        rows.AddRange(dataset.Accounts.Select(row => new[]
        {
            row.Account.Platform.DisplayName(), row.Account.Name, row.TaskCount.ToString(), row.Succeeded.ToString(), row.Failed.ToString(), row.Drafts.ToString(),
            Text(row.Snapshot?.VideoTotal), Text(row.Snapshot?.FollowerTotal), Text(row.Snapshot?.YesterdayViews ?? row.KuaishouSummary?.Views),
            Text(row.Snapshot?.YesterdayLikes ?? row.KuaishouSummary?.Likes), Text(row.Snapshot?.YesterdayComments ?? row.KuaishouSummary?.Comments),
            Fen(row.Account.Platform == PublishPlatform.WeixinChannel ? row.Snapshot?.AdMonetizationIncomeFen : row.KuaishouSummary?.AdIncomeFen),
            (row.Snapshot?.CollectedAt ?? row.KuaishouSummary?.CollectedAt)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
        }));
        var content = string.Join("\r\n", rows.Select(row => string.Join(',', row.Select(Escape))));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, "\uFEFF" + content, new UTF8Encoding(false));
    }
    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Text(long? value) => value?.ToString() ?? "";
    private static string Fen(long? value) => value.HasValue ? (value.Value / 100m).ToString("0.00") : "";
}

public static class AnalyticsDatePolicy
{
    public static DateOnly Yesterday(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime.Date.AddDays(-1));
    public static IReadOnlyList<DateOnly> Range(DateOnly from, DateOnly to, int maximumDays = 31)
    {
        if (to < from) return [];
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > maximumDays) throw new ArgumentOutOfRangeException(nameof(to), $"单次最多补采 {maximumDays} 天数据。");
        return Enumerable.Range(0, days).Select(from.AddDays).ToArray();
    }
}
