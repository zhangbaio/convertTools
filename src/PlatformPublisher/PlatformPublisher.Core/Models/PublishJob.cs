namespace PlatformPublisher.Core.Models;

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
}

public static class PublishJobKindExtensions
{
    public static string DisplayName(this PublishJobKind kind) => kind switch
    {
        PublishJobKind.Series => "剧集上传",
        PublishJobKind.DirectoryMaterials => "目录批量发表",
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
    public string AccountName { get; set; } = string.Empty;
    public bool DeclareOriginal { get; set; } = true;
    public bool HideLocation { get; set; } = true;
    public bool AllowDuplicatePublish { get; set; }
    public PublishJobStatus Status { get; set; } = PublishJobStatus.Pending;
    public string StatusMessage { get; set; } = "等待执行";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
