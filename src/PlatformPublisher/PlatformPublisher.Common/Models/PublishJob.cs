namespace PlatformPublisher.Common.Models;

public enum PublishJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Blocked,
}

public enum PublishJobKind
{
    Series,
    DirectoryMaterials,
    SystemHighlight,
    ProjectMaterials,
    LocalVideos,
    CustomVideos,
}

public static class PublishJobKindExtensions
{
    public static string DisplayName(this PublishJobKind kind) => kind switch
    {
        PublishJobKind.Series => "剧集上传",
        PublishJobKind.DirectoryMaterials => "目录批量发表",
        PublishJobKind.SystemHighlight => "系统高光发表",
        PublishJobKind.ProjectMaterials => "项目素材发表",
        PublishJobKind.LocalVideos => "本地视频发表",
        PublishJobKind.CustomVideos => "自选视频发表",
        _ => kind.ToString(),
    };
}

public sealed class PublishJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public PublishPlatform Platform { get; set; } = PublishPlatform.WeixinChannel;
    public PublishJobKind Kind { get; set; } = PublishJobKind.Series;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public bool DeclareOriginal { get; set; } = true;
    public bool HideLocation { get; set; } = true;
    public bool AllowDuplicatePublish { get; set; }
    public string DramaTitle { get; set; } = string.Empty;
    public int PublishCount { get; set; } = 1;
    public string PublishVideoTypes { get; set; } = "混剪,解说,切片";
    public bool RegenerateHighlightsAfterPublish { get; set; }
    public string PublishDescription { get; set; } = "热门短剧，精彩内容持续更新。";
    public List<string> CustomVideoFiles { get; set; } = [];
    public string PlatformOptionsJson { get; set; } = string.Empty;
    public DateTimeOffset? ScheduledAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastCompletedAt { get; set; }
    public PublishJobStatus Status { get; set; } = PublishJobStatus.Pending;
    public string StatusMessage { get; set; } = "等待执行";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
