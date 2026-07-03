namespace TikTokPublisher.Core.Publishing;

/// <summary>发表结束动作。默认 None=只填不发（安全）。</summary>
public enum FinalAction
{
    None,
    Draft,
    Publish,
}

/// <summary>一条待发布素材（TikTok 短剧中心剧集上传）。</summary>
public sealed class PublishItem
{
    public string VideoPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ShortTitle { get; set; } = "";
    public string? CoverPath { get; set; }
    public string? DramaName { get; set; }
    public string? ProjectKey { get; set; }
    public string? ProjectDir { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string GenreCategory { get; set; } = "";
    public int EpisodeIndex { get; set; }
    public int EpisodeCount { get; set; } = 1;

    public string DisplayName => string.IsNullOrEmpty(VideoPath) ? "(空)" : Path.GetFileName(VideoPath);
}

public sealed class PublishResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public bool StopQueue { get; init; }

    public static PublishResult Success(string message = "完成") => new() { Ok = true, Message = message };
    public static PublishResult Fail(string message) => new() { Ok = false, Message = message };
    public static PublishResult FailAndStopQueue(string message) => new() { Ok = false, Message = message, StopQueue = true };
}

public sealed record PublishProgress(
    string AccountId,
    string AccountName,
    string ItemName,
    string Message,
    bool Done,
    bool Ok);
