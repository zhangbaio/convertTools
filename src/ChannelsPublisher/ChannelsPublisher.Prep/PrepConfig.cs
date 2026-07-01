using System.Text.Json;

namespace ChannelsPublisher.Prep;

/// <summary>素材准备（prep）配置：扫描来源 + 原创度 + AI描述 + 封面 + 输出。可从 JSON 加载。
/// 对应现有 Python prep 的各阶段设置。</summary>
public sealed class PrepConfig
{
    // 来源
    /// <summary>来源类型：directory / new_drama_mount / downloaded_system_highlight / material_video_download
    /// / material_clips（剪辑成片）/ project_materials（项目素材）/ source_videos（源视频）
    /// / custom_files（自选，用 CustomFiles）/ directory_publish（目录批量发表，子目录一视频）。</summary>
    public string SourceType { get; set; } = "directory";
    public string SourceDir { get; set; } = "";
    /// <summary>custom_files 来源：显式视频文件路径列表。</summary>
    public List<string> CustomFiles { get; set; } = new();
    public string Account { get; set; } = "";      // 目标账号（名/Id），写进产出任务
    public string? DramaName { get; set; }          // 挂载视频号剧集名
    public bool DeclareOriginal { get; set; }
    public string FinalAction { get; set; } = "none";
    public string OutputDir { get; set; } = "";     // 处理产物 + publish-tasks.json 输出目录

    public string FfmpegPath { get; set; } = "ffmpeg";

    // AI 描述（OpenAI 兼容 chat/completions）
    public bool AiEnabled { get; set; }
    public string AiEndpoint { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string DescriptionTemplate { get; set; } = ""; // 基础文案（无 AI 时直接用）

    // 原创度（ffmpeg 扰动，种子=文件名 → 同名可复现）
    public bool OriginalityEnabled { get; set; }
    public bool OrigZoom { get; set; } = true;
    public bool OrigColor { get; set; }
    public bool OrigSpeed { get; set; }
    public bool OrigFade { get; set; }
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;

    // 封面：sidecar（用视频旁的封面图）/ frame（抽帧）/ none
    public string CoverMode { get; set; } = "sidecar";
    public double CoverFrameSeconds { get; set; } = 1.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static PrepConfig Load(string path)
        => JsonSerializer.Deserialize<PrepConfig>(File.ReadAllText(path), JsonOptions) ?? new PrepConfig();

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
