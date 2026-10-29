using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

public static class XingeAutoPublishStatuses
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) =>
        status is Success or Failed;
}

// 与 XINGE 服务端 failure_kind 取值一致：账号类失败会暂停账号当日派单，source 会拉黑剧目，upload 按次数重试。
public static class XingeAutoPublishFailureKinds
{
    public const string AccountLogin = "account_login";
    public const string AccountDailyLimit = "account_daily_limit";
    public const string Source = "source";
    public const string Upload = "upload";
}

public sealed record XingeAutoPublishAssignment(
    long AssignmentId,
    string ProgressPath,
    string SeriesId,
    string AccountProfileId,
    string Title)
{
    public static XingeAutoPublishAssignment? FromCommand(TikTokRemoteCommand command)
    {
        if (!command.IsUploadCommand || !command.IsAutoPublishAssignment)
            return null;
        var spec = command.Series?.FirstOrDefault();
        return new XingeAutoPublishAssignment(
            command.AutoPublishAssignmentId,
            command.AutoPublishProgressPath.Trim(),
            spec?.SeriesId ?? "",
            command.AccountProfileId.Trim(),
            spec?.Title ?? command.Titles?.FirstOrDefault() ?? "");
    }
}

public sealed record XingeAutoPublishProgress(
    long AssignmentId,
    string ProgressPath,
    string Status,
    string Step = "",
    string Detail = "",
    string FailureKind = "")
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public static XingeAutoPublishProgress For(
        XingeAutoPublishAssignment assignment,
        string status,
        string step = "",
        string detail = "",
        string failureKind = "") =>
        new(assignment.AssignmentId, assignment.ProgressPath, status, step, detail, failureKind);
}

public sealed class XingeAutoPublishMarker
{
    [JsonPropertyName("assignment_id")]
    public long AssignmentId { get; set; }

    [JsonPropertyName("progress_path")]
    public string ProgressPath { get; set; } = "";

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = "";

    [JsonPropertyName("account_profile_id")]
    public string AccountProfileId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    // 已回传终态（success/failed）后不再上报，项目之后的手动重跑与该派单无关。
    [JsonPropertyName("final_status")]
    public string FinalStatus { get; set; } = "";

    [JsonIgnore]
    public bool IsActive => AssignmentId > 0 && string.IsNullOrWhiteSpace(FinalStatus);

    public XingeAutoPublishAssignment ToAssignment() =>
        new(AssignmentId, ProgressPath, SeriesId, AccountProfileId, Title);
}

public static class XingeAutoPublishMarkerStore
{
    public const string FileName = ".xinge-auto-publish.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string projectDir) => Path.Combine(projectDir, FileName);

    public static void Write(string projectDir, XingeAutoPublishAssignment assignment, DateTimeOffset? createdAt = null)
    {
        var marker = new XingeAutoPublishMarker
        {
            AssignmentId = assignment.AssignmentId,
            ProgressPath = assignment.ProgressPath,
            SeriesId = assignment.SeriesId,
            AccountProfileId = assignment.AccountProfileId,
            Title = assignment.Title,
            CreatedAt = createdAt ?? DateTimeOffset.Now,
        };
        Save(projectDir, marker);
    }

    public static XingeAutoPublishMarker? TryRead(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return null;
        var path = PathFor(projectDir);
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<XingeAutoPublishMarker>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool IsActive(string? projectDir) => TryRead(projectDir)?.IsActive == true;

    public static void MarkFinal(string projectDir, long assignmentId, string finalStatus)
    {
        var marker = TryRead(projectDir);
        if (marker is null || marker.AssignmentId != assignmentId || !marker.IsActive)
            return;
        marker.FinalStatus = finalStatus;
        Save(projectDir, marker);
    }

    private static void Save(string projectDir, XingeAutoPublishMarker marker)
    {
        var path = PathFor(projectDir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public static class XingeAutoPublishFailureClassifier
{
    private static readonly string[] AccountLoginKeywords =
    [
        "自动登录失败", "未登录", "登录已失效", "登录失效", "重新登录", "未找到账号", "CDP 端点", "login required", "not logged in",
        "指定的 TikTok 账号", "没有配置有效工作目录", "上传工作目录不存在",
    ];

    private static readonly string[] DailyLimitKeywords =
    [
        "单日创建剧集上限", "daily limit",
    ];

    public static string Classify(string? stepKey, string? message)
    {
        var text = message ?? "";
        if (ContainsAny(text, DailyLimitKeywords))
            return XingeAutoPublishFailureKinds.AccountDailyLimit;
        if (ContainsAny(text, AccountLoginKeywords))
            return XingeAutoPublishFailureKinds.AccountLogin;
        return stepKey is QueueStepRegistry.Download or QueueStepRegistry.MaterialValidate
            ? XingeAutoPublishFailureKinds.Source
            : XingeAutoPublishFailureKinds.Upload;
    }

    // 导入阶段：剧不存在、作者排除、集数不符都说明这部剧不适合本客户端，拉黑；搜索服务不可用属于临时故障，按上传失败重试。
    public static string ClassifyImportFailure(string? reason)
    {
        var text = reason ?? "";
        if (text.Contains(UploadTitleImportService.EmptySearchResultReason, StringComparison.Ordinal))
            return XingeAutoPublishFailureKinds.Upload;
        if (text.Contains(UploadTitleImportService.NoExactMatchReason, StringComparison.Ordinal) ||
            text.StartsWith(UploadTitleImportService.AuthorExcludedFailurePrefix, StringComparison.Ordinal) ||
            text.StartsWith(UploadTitleImportService.UnknownAuthorFailurePrefix, StringComparison.Ordinal) ||
            text.Contains("集数", StringComparison.Ordinal))
        {
            return XingeAutoPublishFailureKinds.Source;
        }

        return Classify("", text);
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}

// 把队列进度事件翻译成派单进度：只关心带有效标记的项目，同一状态/步骤不重复上报。
public sealed class XingeAutoPublishQueueObserver
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedMarker> _markers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _lastReported = new();
    private readonly Dictionary<string, (DateOnly Day, string Message)> _dailyLimitedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public XingeAutoPublishQueueObserver(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.Now);

    public XingeAutoPublishProgress? Observe(QueueWorkerProgress progress)
    {
        var item = progress.Item;
        if (item is null || string.IsNullOrWhiteSpace(item.ProjectDir))
            return null;

        lock (_sync)
        {
            var marker = ReadMarker(item.ProjectDir);
            if (marker is null || !marker.IsActive)
                return null;

            var report = BuildReport(marker, item, progress);
            if (report is null)
                return null;

            if (report.FailureKind == XingeAutoPublishFailureKinds.AccountDailyLimit)
            {
                foreach (var account in AccountKeys(item, marker))
                    _dailyLimitedAccounts[account] = (Today(), report.Detail);
            }

            return Accept(item.ProjectDir, marker, report);
        }
    }

    // 单日上限会停止整个队列，其余项目被直接置为「已停止」而不产生进度事件；
    // 队列结束时把同账号下仍未完成的派单一并回报，服务端才能把这些剧改派给其他账号。
    public IReadOnlyList<XingeAutoPublishProgress> ObserveRunFinished(IEnumerable<QueueProjectItem> items)
    {
        var reports = new List<XingeAutoPublishProgress>();
        lock (_sync)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.ProjectDir) ||
                    item.StatusText is not (QueueStepStatus.Stopped or QueueStepStatus.Pending))
                {
                    continue;
                }

                var marker = ReadMarker(item.ProjectDir);
                if (marker is null || !marker.IsActive || UploadCompletedForMarker(item, marker))
                    continue;

                var limit = AccountKeys(item, marker)
                    .Select(account => _dailyLimitedAccounts.TryGetValue(account, out var hit) ? hit : default)
                    .FirstOrDefault(hit => hit.Day == Today());
                if (limit.Day == default)
                    continue;

                var report = XingeAutoPublishProgress.For(
                    marker.ToAssignment(),
                    XingeAutoPublishStatuses.Failed,
                    QueueStepRegistry.UploadSeries,
                    limit.Message,
                    XingeAutoPublishFailureKinds.AccountDailyLimit);
                var accepted = Accept(item.ProjectDir, marker, report);
                if (accepted is not null)
                    reports.Add(accepted);
            }
        }

        return reports;
    }

    private XingeAutoPublishProgress? Accept(string projectDir, XingeAutoPublishMarker marker, XingeAutoPublishProgress report)
    {
        var key = $"{report.Status}\n{report.Step}";
        if (_lastReported.TryGetValue(marker.AssignmentId, out var last) && last == key)
            return null;
        _lastReported[marker.AssignmentId] = key;

        if (XingeAutoPublishStatuses.IsTerminal(report.Status))
        {
            XingeAutoPublishMarkerStore.MarkFinal(projectDir, marker.AssignmentId, report.Status);
            _markers.Remove(Path.GetFullPath(projectDir));
        }

        return report;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_now().LocalDateTime);

    private static IEnumerable<string> AccountKeys(QueueProjectItem item, XingeAutoPublishMarker marker) =>
        new[] { item.AccountProfileId, marker.AccountProfileId }
            .Select(value => value?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static XingeAutoPublishProgress? BuildReport(
        XingeAutoPublishMarker marker,
        QueueProjectItem item,
        QueueWorkerProgress progress)
    {
        var assignment = marker.ToAssignment();
        if (UploadCompletedForMarker(item, marker))
            return XingeAutoPublishProgress.For(assignment, XingeAutoPublishStatuses.Success, QueueStepRegistry.UploadSeries, progress.Message);

        switch (item.StatusText)
        {
            case QueueStepStatus.Failed:
            {
                var step = FailedStep(item) ?? progress.StepKey ?? "";
                var detail = FirstNonEmpty(item.LastError, progress.Message);
                return XingeAutoPublishProgress.For(
                    assignment,
                    XingeAutoPublishStatuses.Failed,
                    step,
                    detail,
                    XingeAutoPublishFailureClassifier.Classify(step, detail));
            }
            case QueueStepStatus.Stopped when
                XingeAutoPublishFailureClassifier.Classify("", progress.Message) == XingeAutoPublishFailureKinds.AccountDailyLimit:
                return XingeAutoPublishProgress.For(
                    assignment,
                    XingeAutoPublishStatuses.Failed,
                    QueueStepRegistry.UploadSeries,
                    progress.Message,
                    XingeAutoPublishFailureKinds.AccountDailyLimit);
            case QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot:
            {
                var step = FirstNonEmpty(item.CurrentStep, progress.StepKey);
                return string.IsNullOrWhiteSpace(step)
                    ? null
                    : XingeAutoPublishProgress.For(assignment, XingeAutoPublishStatuses.Running, step, QueueStepRegistry.LabelOf(step));
            }
            default:
                return null;
        }
    }

    // 项目目录可能复用自更早的上传；只有派单下发之后完成的上传才算本派单成功。
    private static bool UploadCompletedForMarker(QueueProjectItem item, XingeAutoPublishMarker marker) =>
        item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) == QueueStepStatus.Completed &&
        DateTimeOffset.TryParse(item.UploadCompletedAt, out var completedAt) &&
        completedAt >= marker.CreatedAt;

    private static string? FailedStep(QueueProjectItem item) =>
        item.StepStates.FirstOrDefault(pair => pair.Value == QueueStepStatus.Failed).Key;

    private XingeAutoPublishMarker? ReadMarker(string projectDir)
    {
        var dir = Path.GetFullPath(projectDir);
        var writeTime = File.GetLastWriteTimeUtc(XingeAutoPublishMarkerStore.PathFor(dir));
        if (_markers.TryGetValue(dir, out var cached) && cached.WriteTime == writeTime)
            return cached.Marker;
        var marker = XingeAutoPublishMarkerStore.TryRead(dir);
        _markers[dir] = new CachedMarker(writeTime, marker);
        return marker;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";

    private sealed record CachedMarker(DateTime WriteTime, XingeAutoPublishMarker? Marker);
}
