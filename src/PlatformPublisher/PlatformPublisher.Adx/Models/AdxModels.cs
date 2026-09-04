using System.Text.Json.Serialization;

namespace PlatformPublisher.Adx.Models;

public sealed class AdxSettings
{
    public string BaseUrl { get; set; } = "https://adx.shjwh.top/admin/";
    public string Username { get; set; } = string.Empty;
    public int DefaultTopCount { get; set; } = 5;
    public int QueryLimit { get; set; } = 50;
    public int DownloadConcurrency { get; set; } = 3;
    public bool Headless { get; set; }

    public AdxSettings Normalize() => new()
    {
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "https://adx.shjwh.top/admin/" : BaseUrl.Trim(),
        Username = Username.Trim(),
        DefaultTopCount = Math.Clamp(DefaultTopCount, 1, 20),
        QueryLimit = Math.Clamp(QueryLimit, 1, 200),
        DownloadConcurrency = Math.Clamp(DownloadConcurrency, 1, 5),
        Headless = Headless,
    };

    public string Identity => $"{BaseUrl.Trim().TrimEnd('/').ToLowerInvariant()}\n{Username.Trim().ToLowerInvariant()}";
}

public enum AdxLoginState { NotConfigured, LoggedOut, Checking, LoggedIn, Expired, Failed }

public sealed record AdxLoginStatus(
    AdxLoginState State,
    string Username,
    bool PasswordConfigured,
    DateTimeOffset? LastVerifiedAt = null,
    string? Message = null);

public sealed record AdxCandidate(
    string MaterialId,
    int Rank,
    string? CoverUrl,
    long Exposure,
    long PlayCount,
    long LikeCount,
    bool Downloaded);

public sealed record AdxQueryRequest(
    string AccountId,
    string SeriesName,
    string OriginalTitle,
    string WorkflowDirectory,
    int Limit);

public sealed record AdxQueryResult(
    string QueryId,
    int Total,
    IReadOnlyList<AdxCandidate> Candidates);

public enum AdxCoverMode { PlatformDefault, Adx, Project }

public sealed record AdxDownloadRequest(
    string AccountId,
    string SeriesName,
    string OriginalTitle,
    string WorkflowDirectory,
    IReadOnlyList<string> MaterialIds,
    bool ReplaceCover = true,
    AdxCoverMode CoverMode = AdxCoverMode.Adx,
    string? ProjectCoverPath = null,
    IReadOnlyList<string>? RedownloadMaterialIds = null);

public sealed class AdxBatchItem
{
    public string MaterialId { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string VideoPath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public string Status { get; set; } = "downloaded";
    public string? Description { get; set; }
    public string? ShortTitle { get; set; }
}

public sealed class AdxItemPublishStatus
{
    public string Status { get; set; } = "pending";
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdxAccountPublishStatus
{
    public string Status { get; set; } = "pending";
    public DateTimeOffset UpdatedAt { get; set; }
    public Dictionary<string, AdxItemPublishStatus> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdxBatchManifest
{
    public int Version { get; set; } = 2;
    public string BatchId { get; set; } = string.Empty;
    public string WorkflowDir { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<AdxBatchItem> Items { get; set; } = [];
    public Dictionary<string, AdxAccountPublishStatus> PublishByAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ManifestPath { get; set; } = string.Empty;

    [JsonIgnore]
    public string DownloadDirectory => Path.GetDirectoryName(ManifestPath) ?? string.Empty;
}

public sealed record AdxDownloadResult(
    string DownloadDirectory,
    IReadOnlyList<AdxBatchItem> Items,
    string Message);

public sealed record AdxProgress(
    string Stage,
    string Message,
    int Current = 0,
    int Total = 0,
    bool IsError = false);

public sealed record AdxPublishItem(
    string MaterialId,
    string VideoPath,
    string? CoverPath,
    string? Description,
    string? ShortTitle,
    string ManifestPath);

public sealed class AdxPublishPayload
{
    public string OriginalTitle { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public string PublishOptionsJson { get; set; } = string.Empty;
    public List<AdxPublishItem> Items { get; set; } = [];
}
