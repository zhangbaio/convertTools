using System.Text.Json;

namespace TikTokPublisher.Core.Publishing;

/// <summary>一条发布任务的可序列化形态（Python prep 产出 / 导入用）。</summary>
public sealed class PublishTaskDto
{
    /// <summary>目标账号：账号名或账号 Id（导入时解析匹配）。</summary>
    public string Account { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string ShortTitle { get; set; } = "";
    public string? CoverPath { get; set; }
    /// <summary>挂载的TikTok 短剧中心剧集名（新剧名）。</summary>
    public string? DramaName { get; set; }
    public bool DeclareOriginal { get; set; }

    public PublishItem ToItem() => new()
    {
        VideoPath = VideoPath,
        Description = Description,
        ShortTitle = ShortTitle,
        CoverPath = CoverPath,
        DramaName = DramaName,
    };
}

/// <summary>发布任务清单文件（与现有 Python 素材准备对接的 JSON 契约）。
///
/// 约定：Python prep（来源扫描/AI描述/原创度/封面）产出此结构，.NET 侧消费。
/// {
///   "finalAction": "none|save|draft|publish",
///   "tasks": [ { "account": "账号名", "videoPath": "...", "description": "...",
///               "shortTitle": "...", "coverPath": "...", "dramaName": "...", "declareOriginal": true } ]
/// }</summary>
public sealed class PublishTaskFile
{
    public string FinalAction { get; set; } = "none";
    public List<PublishTaskDto> Tasks { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static PublishTaskFile Load(string path)
        => JsonSerializer.Deserialize<PublishTaskFile>(File.ReadAllText(path), JsonOptions) ?? new PublishTaskFile();

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public FinalAction ResolveFinalAction() => FinalActionExtensions.Parse(FinalAction);
}

public static class FinalActionExtensions
{
    public static FinalAction Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "save" or "draft" or "保存" or "草稿" or "保存草稿" => TikTokPublisher.Core.Publishing.FinalAction.Draft,
        "publish" or "发表" or "发布" => TikTokPublisher.Core.Publishing.FinalAction.Publish,
        _ => TikTokPublisher.Core.Publishing.FinalAction.None,
    };

    public static string ToLabel(this FinalAction action) => action switch
    {
        TikTokPublisher.Core.Publishing.FinalAction.Draft => "保存草稿",
        TikTokPublisher.Core.Publishing.FinalAction.Publish => "直接发表",
        _ => "只填不发",
    };
}
