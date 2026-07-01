namespace ChannelsPublisher.Core.Publishing;

/// <summary>发表结束动作。默认 None=只填不发（安全，草稿态在页面里）。</summary>
public enum FinalAction
{
    None,
    Draft,
    Publish,
}

/// <summary>一条待发布素材。字段对应 P1 已验证的视频号发表表单项。</summary>
public sealed class PublishItem
{
    public string VideoPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string ShortTitle { get; set; } = "";
    public string? CoverPath { get; set; }
    /// <summary>要挂载的视频号剧集名（新剧名，视频号注册名）。</summary>
    public string? DramaName { get; set; }
    public bool DeclareOriginal { get; set; }

    public string DisplayName => string.IsNullOrEmpty(VideoPath) ? "(空)" : Path.GetFileName(VideoPath);
}

public sealed class PublishResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public static PublishResult Success(string message = "完成") => new() { Ok = true, Message = message };
    public static PublishResult Fail(string message) => new() { Ok = false, Message = message };
}

/// <summary>发布进度事件（供 UI 状态展示）。</summary>
public sealed record PublishProgress(
    string AccountId,
    string AccountName,
    string ItemName,
    string Message,
    bool Done,
    bool Ok);
