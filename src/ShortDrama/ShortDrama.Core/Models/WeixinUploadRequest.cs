namespace ShortDrama.Core.Models;

public sealed record WeixinUploadRequest(
    string ProjectKey,
    string ProjectDir,
    string DisplayName,
    string? ConfigPath,
    string? ConfigName)
{
    public Action<WeixinMaterialPublishItemResult>? MaterialItemCompleted { get; init; }
}

public sealed record WeixinMaterialPublishItemResult(
    string VideoPath,
    string Status,
    string Message,
    DateTimeOffset CompletedAt);

public sealed record WeixinUploadResult(
    bool Ok,
    string ProjectDir,
    string? ConfigPath,
    string? Message = null);
