using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

// 服务端通知某账号今日上传已达上限后，到 until 之前不再为它扫描、恢复、重试失败项目或启动新的自动派单。
public static class XingeQuotaFull
{
    public static bool IsHeld(DateTimeOffset until, DateTimeOffset now) => now < until;

    // 今日上传数已经达标时，之前失败或还在下载的派单都不再恢复。
    public static bool ShouldSkipRecovery(bool accountFull, string? status) => accountFull;
}

public static class XingeAutoPublishStatuses
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string UserStopped = "user_stopped";

    // success/failed/user_stopped 都是终态：发件箱里已有终态时，迟到的 running 不能把它盖掉。
    public static bool IsTerminal(string status) =>
        status is Success or Failed or UserStopped;
}

// 与 XINGE 服务端 failure_kind 取值一致：账号类失败会暂停账号当日派单，source 会拉黑剧目，download 释放名额并不再执行后续步骤，upload 按上传失败处理。
public static class XingeAutoPublishFailureKinds
{
    public const string AccountLogin = "account_login";
    public const string AccountDailyLimit = "account_daily_limit";
    public const string AccountRemoteDisabled = "account_remote_disabled";
    public const string Source = "source";
    public const string Download = "download";
    public const string Upload = "upload";
    public const string QueueBusy = "queue_busy";
    public const string Interrupted = "interrupted";
    public const string UserStopped = "user_stopped";
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

    // 已回传终态（success/failed/user_stopped）后不再自动续跑。新的派单会覆写这份标记。
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

    // 今天派到这个账号的剧保持勾选。以标记写入时的本机日期为准，已完成或已失败的也留着。
    public static bool ShouldKeepChecked(XingeAutoPublishMarker? marker, string? accountProfileId, DateTime localDay)
    {
        if (marker is null || marker.AssignmentId <= 0 || marker.CreatedAt == default)
            return false;
        // 用户刚手动停掉的剧先取消勾选，等下次派单覆写标记后再勾上执行。
        if (string.Equals(marker.FinalStatus, XingeAutoPublishStatuses.UserStopped, StringComparison.OrdinalIgnoreCase))
            return false;
        if (marker.CreatedAt.ToLocalTime().Date != localDay.Date)
            return false;

        var markerAccount = (marker.AccountProfileId ?? "").Trim();
        var account = (accountProfileId ?? "").Trim();
        return markerAccount.Length == 0
            || account.Length == 0
            || string.Equals(markerAccount, account, StringComparison.OrdinalIgnoreCase);
    }

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
    public const string TikTokQueueBusyDetail = "当前已有 TikTok 队列在执行，请等待完成后再发起新任务。";

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
        if (text.Contains("未启用远程分发", StringComparison.Ordinal))
            return XingeAutoPublishFailureKinds.AccountRemoteDisabled;
        if (text.Contains("当前已有 TikTok 队列在执行", StringComparison.Ordinal) ||
            text.Contains("队列仍在执行", StringComparison.Ordinal) ||
            text.Contains("队列尚未结束", StringComparison.Ordinal) ||
            text.Contains("未接纳新项目", StringComparison.Ordinal))
            return XingeAutoPublishFailureKinds.QueueBusy;
        if (ContainsAny(text, DailyLimitKeywords))
            return XingeAutoPublishFailureKinds.AccountDailyLimit;
        if (ContainsAny(text, AccountLoginKeywords))
            return XingeAutoPublishFailureKinds.AccountLogin;
        if (stepKey == QueueStepRegistry.Download)
            return XingeAutoPublishFailureKinds.Download;
        return stepKey == QueueStepRegistry.MaterialValidate
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

public enum XingeAutoPublishRecoveryAction
{
    None,
    Resume,
    ReportSuccess,
    ReportInterrupted,
}

public static class XingeAutoPublishRecovery
{
    public const string NotExecutableDetail = "目标目录没有可执行项目";

    // 命令还停在 dispatched 时由服务端重投，客户端不用另报。已接收但本地没有项目才回报中断。
    public static XingeAutoPublishRecoveryAction Decide(string? serverStatus, XingeAutoPublishMarker? marker, bool projectExists)
    {
        var status = (serverStatus ?? "").Trim().ToLowerInvariant();
        if (status is "success" or "failed" or "released" or "retry_wait")
            return XingeAutoPublishRecoveryAction.None;
        if (string.Equals(marker?.FinalStatus, XingeAutoPublishStatuses.Success, StringComparison.OrdinalIgnoreCase))
            return XingeAutoPublishRecoveryAction.ReportSuccess;
        // 用户手动停止后派单标记已结束。这里不能再 Resume，也不能报「项目丢失」触发服务端立刻重投。
        if (string.Equals(marker?.FinalStatus, XingeAutoPublishStatuses.UserStopped, StringComparison.OrdinalIgnoreCase))
            return XingeAutoPublishRecoveryAction.None;
        if (marker?.IsActive == true && projectExists)
            return XingeAutoPublishRecoveryAction.Resume;
        if (status is "accepted" or "running")
            return XingeAutoPublishRecoveryAction.ReportInterrupted;
        return XingeAutoPublishRecoveryAction.None;
    }
}

public static class XingeAutoPublishRecoveryQueue
{
    /// <summary>
    /// 恢复执行前只重新勾选这些项目。已完成步骤保留，失败和已停止留给队列开跑时再改回待执行。
    /// 返回顺序与 <paramref name="projectDirs"/> 一致。
    /// </summary>
    public static IReadOnlyList<QueueProjectItem> EnableForResume(
        IEnumerable<QueueProjectItem> items,
        IReadOnlyCollection<string> projectDirs)
    {
        var byDir = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || item.Archived || string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            var full = Normalize(item.ProjectDir);
            if (full.Length == 0 || byDir.ContainsKey(full))
                continue;
            byDir[full] = item;
        }

        var enabled = new List<QueueProjectItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectDir in projectDirs)
        {
            var full = Normalize(projectDir);
            if (full.Length == 0 || !seen.Add(full) || !byDir.TryGetValue(full, out var item))
                continue;
            item.Enabled = true;
            enabled.Add(item);
        }

        return enabled;
    }

    private static string Normalize(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return "";
        try
        {
            return Path.GetFullPath(projectDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }
}
