using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

// 与 XINGE 监控屏的上传任务状态一致：每部剧只上报这四种状态变化，不上报步骤明细。
public static class XingeUploadTaskStatuses
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    public static bool IsTerminal(string status) =>
        status is Success or Failed or Stopped;
}

public sealed record XingeUploadTaskReport(
    string ProjectPath,
    string Status,
    string AccountProfileId = "",
    string AccountProfileName = "",
    string Title = "",
    string NewTitle = "",
    int Episodes = 0,
    string SeriesId = "",
    long AssignmentId = 0,
    string FailureKind = "",
    string FailureReason = "")
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;
}

public readonly record struct XingeUploadTaskAccount(string Id, string Name);

/// <summary>
/// 把队列进度事件翻译成按剧的上传任务状态（自动上新、手动导入、远程下发的上传一视同仁）。
/// 只跟踪本次运行包含上传步骤、且尚未在运行前完成上传的项目；同一状态不重复上报。
/// 带有效自动上新标记的项目附带派单 id，终态后标记失效。
/// </summary>
public sealed class XingeUploadTaskObserver
{
    private static readonly TimeSpan CompletionClockSkew = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, CachedMarker> _markers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedTask> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateOnly Day, string Message)> _dailyLimitedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public XingeUploadTaskObserver(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.Now);

    public XingeUploadTaskReport? Observe(
        QueueWorkerProgress progress,
        bool uploadStepEnabled,
        Func<QueueProjectItem, XingeUploadTaskAccount?>? resolveAccount = null)
    {
        var item = progress.Item;
        if (item is null || string.IsNullOrWhiteSpace(item.ProjectDir))
            return null;

        lock (_sync)
        {
            var dir = Path.GetFullPath(item.ProjectDir);
            var marker = ActiveMarker(dir);
            if (!_tracked.TryGetValue(dir, out var tracked))
            {
                if (!ShouldStartTracking(item, marker, uploadStepEnabled))
                    return null;
                tracked = new TrackedTask(_now(), resolveAccount?.Invoke(item));
                _tracked[dir] = tracked;
            }

            tracked.Item = item;
            var report = Evaluate(dir, item, progress, tracked, marker);
            if (report is null)
                return null;

            if (report.FailureKind == XingeAutoPublishFailureKinds.AccountDailyLimit)
            {
                foreach (var account in AccountKeys(item, marker, tracked))
                    _dailyLimitedAccounts[account] = (Today(), report.FailureReason);
            }

            return Accept(dir, tracked, marker, report);
        }
    }

    /// <summary>
    /// 队列结束：仍在进行中的剧回报「已停止」（单日上限导致的停止回报为失败，服务端据此改派）；
    /// 单日上限停止的队列里，带派单标记却没来得及开始的剧也一并回报，服务端才能把它们改派给其他账号。
    /// </summary>
    public IReadOnlyList<XingeUploadTaskReport> ObserveRunFinished(
        string workspaceRoot,
        IEnumerable<QueueProjectItem> items,
        Func<QueueProjectItem, XingeUploadTaskAccount?>? resolveAccount = null)
    {
        var reports = new List<XingeUploadTaskReport>();
        var root = Path.GetFullPath(workspaceRoot);
        lock (_sync)
        {
            var byDir = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.ProjectDir))
                    byDir[Path.GetFullPath(item.ProjectDir)] = item;
            }

            foreach (var dir in _tracked.Keys.Where(dir => IsUnder(dir, root)).ToList())
            {
                var tracked = _tracked[dir];
                var item = byDir.GetValueOrDefault(dir) ?? tracked.Item;
                byDir.Remove(dir);
                if (item is null || tracked.LastStatus is not (null or XingeUploadTaskStatuses.Running))
                    continue;

                var marker = ActiveMarker(dir);
                var report = UploadCompletedSince(item, SinceFor(tracked, marker))
                    ? BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Success)
                    : DailyLimitFor(item, marker, tracked) is { } limit
                        ? BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Failed, XingeAutoPublishFailureKinds.AccountDailyLimit, limit)
                        : item.StatusText == QueueStepStatus.Failed
                            ? FailedReport(dir, item, tracked, marker, "")
                            : BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Stopped, "", StoppedReason(item));
                if (Accept(dir, tracked, marker, report) is { } accepted)
                    reports.Add(accepted);
            }

            foreach (var (dir, item) in byDir)
            {
                if (!IsUnder(dir, root) || item.StatusText is not (QueueStepStatus.Stopped or QueueStepStatus.Pending))
                    continue;
                var marker = ActiveMarker(dir);
                if (marker is null || UploadCompletedSince(item, marker.CreatedAt))
                    continue;
                var tracked = new TrackedTask(_now(), resolveAccount?.Invoke(item)) { Item = item };
                if (DailyLimitFor(item, marker, tracked) is not { } limit)
                    continue;
                var report = BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Failed, XingeAutoPublishFailureKinds.AccountDailyLimit, limit);
                if (Accept(dir, tracked, marker, report) is { } accepted)
                    reports.Add(accepted);
            }

            foreach (var dir in _tracked.Keys.Where(dir => IsUnder(dir, root)).ToList())
                _tracked.Remove(dir);
        }

        return reports;
    }

    private static bool ShouldStartTracking(QueueProjectItem item, XingeAutoPublishMarker? marker, bool uploadStepEnabled)
    {
        if (marker is not null)
            return true;
        if (!uploadStepEnabled)
            return false;
        if (item.StatusText is not (QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot or QueueStepStatus.ManualIntervention or QueueStepStatus.Failed))
            return false;
        // 上传已在本次运行之前完成的项目只是在补跑其他步骤，不是一次新的上传。
        return item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) != QueueStepStatus.Completed;
    }

    private XingeUploadTaskReport? Evaluate(
        string dir,
        QueueProjectItem item,
        QueueWorkerProgress progress,
        TrackedTask tracked,
        XingeAutoPublishMarker? marker)
    {
        if (UploadCompletedSince(item, SinceFor(tracked, marker)))
            return BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Success);

        return item.StatusText switch
        {
            QueueStepStatus.Failed => FailedReport(dir, item, tracked, marker, progress.Message),
            QueueStepStatus.Stopped when
                XingeAutoPublishFailureClassifier.Classify("", progress.Message) == XingeAutoPublishFailureKinds.AccountDailyLimit =>
                BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Failed, XingeAutoPublishFailureKinds.AccountDailyLimit, progress.Message),
            QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot or QueueStepStatus.ManualIntervention =>
                BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Running),
            _ => null,
        };
    }

    private XingeUploadTaskReport FailedReport(
        string dir,
        QueueProjectItem item,
        TrackedTask tracked,
        XingeAutoPublishMarker? marker,
        string? progressMessage)
    {
        var step = item.StepStates.FirstOrDefault(pair => pair.Value == QueueStepStatus.Failed).Key ?? "";
        var reason = FirstNonEmpty(item.LastError, progressMessage);
        if (step.Length > 0 && !reason.Contains(QueueStepRegistry.LabelOf(step), StringComparison.Ordinal))
            reason = $"{QueueStepRegistry.LabelOf(step)}：{reason}";
        return BuildReport(dir, item, tracked, marker, XingeUploadTaskStatuses.Failed,
            XingeAutoPublishFailureClassifier.Classify(step, reason), reason);
    }

    private XingeUploadTaskReport BuildReport(
        string dir,
        QueueProjectItem item,
        TrackedTask tracked,
        XingeAutoPublishMarker? marker,
        string status,
        string failureKind = "",
        string failureReason = "")
    {
        var accountId = FirstNonEmpty(item.AccountProfileId, marker?.AccountProfileId, tracked.Account?.Id);
        var accountName = FirstNonEmpty(item.AccountProfileName, tracked.Account?.Name);
        return new XingeUploadTaskReport(
            dir,
            status,
            accountId,
            accountName,
            FirstNonEmpty(item.OriginalTitle, marker?.Title, Path.GetFileName(dir)),
            item.NewTitle?.Trim() ?? "",
            Math.Max(0, item.EpisodeCount),
            marker?.SeriesId ?? "",
            marker?.AssignmentId ?? 0,
            failureKind,
            failureReason?.Trim() ?? "")
        {
            OccurredAt = _now(),
        };
    }

    private XingeUploadTaskReport? Accept(string dir, TrackedTask tracked, XingeAutoPublishMarker? marker, XingeUploadTaskReport report)
    {
        if (tracked.LastStatus == report.Status &&
            (report.Status != XingeUploadTaskStatuses.Failed || tracked.LastReason == report.FailureReason))
        {
            return null;
        }

        tracked.LastStatus = report.Status;
        tracked.LastReason = report.FailureReason;
        if (marker is not null && report.Status is XingeUploadTaskStatuses.Success or XingeUploadTaskStatuses.Failed)
        {
            XingeAutoPublishMarkerStore.MarkFinal(dir, marker.AssignmentId, report.Status);
            _markers.Remove(dir);
        }

        return report;
    }

    // 带派单标记的项目以派单写入时间为界（目录可能复用自更早的上传），否则以本次开始跟踪的时间为界。
    private static DateTimeOffset SinceFor(TrackedTask tracked, XingeAutoPublishMarker? marker) =>
        marker?.CreatedAt ?? tracked.StartedAt - CompletionClockSkew;

    private static bool UploadCompletedSince(QueueProjectItem item, DateTimeOffset since) =>
        item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) == QueueStepStatus.Completed &&
        DateTimeOffset.TryParse(item.UploadCompletedAt, out var completedAt) &&
        completedAt >= since;

    private string? DailyLimitFor(QueueProjectItem item, XingeAutoPublishMarker? marker, TrackedTask tracked)
    {
        foreach (var account in AccountKeys(item, marker, tracked))
        {
            if (_dailyLimitedAccounts.TryGetValue(account, out var hit) && hit.Day == Today())
                return hit.Message;
        }

        return null;
    }

    private static string StoppedReason(QueueProjectItem item) =>
        FirstNonEmpty(item.LastError, item.StatusText == QueueStepStatus.Stopped ? "队列已停止，未完成上传" : "队列结束时未完成上传");

    private static IEnumerable<string> AccountKeys(QueueProjectItem item, XingeAutoPublishMarker? marker, TrackedTask tracked) =>
        new[] { item.AccountProfileId, marker?.AccountProfileId, tracked.Account?.Id }
            .Select(value => value?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private DateOnly Today() => DateOnly.FromDateTime(_now().LocalDateTime);

    private static bool IsUnder(string dir, string root) =>
        dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private XingeAutoPublishMarker? ActiveMarker(string dir)
    {
        var writeTime = File.GetLastWriteTimeUtc(XingeAutoPublishMarkerStore.PathFor(dir));
        if (!_markers.TryGetValue(dir, out var cached) || cached.WriteTime != writeTime)
        {
            cached = new CachedMarker(writeTime, XingeAutoPublishMarkerStore.TryRead(dir));
            _markers[dir] = cached;
        }

        return cached.Marker is { IsActive: true } marker ? marker : null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";

    private sealed record CachedMarker(DateTime WriteTime, XingeAutoPublishMarker? Marker);

    private sealed class TrackedTask(DateTimeOffset startedAt, XingeUploadTaskAccount? account)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public XingeUploadTaskAccount? Account { get; } = account;
        public QueueProjectItem? Item { get; set; }
        public string? LastStatus { get; set; }
        public string LastReason { get; set; } = "";
    }
}
