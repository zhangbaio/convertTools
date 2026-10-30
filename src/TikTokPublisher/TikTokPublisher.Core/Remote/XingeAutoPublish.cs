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
    public const string AccountRemoteDisabled = "account_remote_disabled";
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
        if (text.Contains("未启用远程分发", StringComparison.Ordinal))
            return XingeAutoPublishFailureKinds.AccountRemoteDisabled;
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
