using System.Text.Json;
using ChannelsPublisher.Core.Services;

namespace ChannelsPublisher.Core.Config;

/// <summary>发表配置（移植自 Python publish_video_dialog）。作为发表的全局默认，
/// 持久化到 %LocalAppData%/ChannelsPublisher/publish-config.json。</summary>
public sealed class PublishConfig
{
    public bool Enabled { get; set; } = true;

    // 来源 / 集数
    public string VideoSourceMode { get; set; } = "directory"; // directory/new_drama_mount/downloaded_system_highlight/...
    public string EpisodeSelectionMode { get; set; } = "all";  // all / count / indexes
    public int StartEpisode { get; set; } = 1;
    public int PublishCount { get; set; } = 0;
    public string EpisodeIndexes { get; set; } = "";           // 如 1,3,5 或 1-5

    // 合并发布
    public bool MergePublish { get; set; }
    public int MergeGroupSize { get; set; } = 0;

    // 新剧挂载
    public string NewDramaMountTitle { get; set; } = "";

    // 运行 / 结束动作
    public string RunStrategy { get; set; } = "all";           // all / resume / retry_failed
    public string FinalAction { get; set; } = "none";          // none(只填不发) / draft / publish
    public bool PauseOnError { get; set; } = true;
    public bool FastMode { get; set; }

    // 描述
    public bool FillDescription { get; set; } = true;
    public bool AiDescription { get; set; }
    public bool AiUseDialogue { get; set; }
    public bool PrependHash { get; set; }
    public string DescriptionTemplate { get; set; } = "";

    // 表单选项（视频号发表页）
    public string Location { get; set; } = "";
    public string Link { get; set; } = "";                     // 如「视频号剧集」
    public string DramaName { get; set; } = "";                // 挂载的视频号剧集名
    public string Activity { get; set; } = "";

    // 封面 / 短标题 / 原创
    public bool ReplaceCover { get; set; }
    public string CoverImagePath { get; set; } = "";
    public bool FillShortTitle { get; set; }
    public int ShortTitleMax { get; set; } = 6;
    public bool DeclareOriginal { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static string FilePath => Path.Combine(AppPaths.DataRoot, "publish-config.json");

    public static PublishConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PublishConfig>(File.ReadAllText(FilePath), Options) ?? new PublishConfig();
        }
        catch { /* 损坏配置回退默认 */ }
        return new PublishConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
