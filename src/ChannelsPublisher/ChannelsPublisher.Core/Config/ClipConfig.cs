using System.Text.Json;
using ChannelsPublisher.Core.Services;

namespace ChannelsPublisher.Core.Config;

/// <summary>一个剪辑模式（高光/混剪…）的开关与产出条数。</summary>
public sealed class ClipModeSetting
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Force { get; set; }       // 强制重生成（忽略缓存）
    public int Count { get; set; } = 5;   // 每集产出条数
}

/// <summary>全局剪辑配置（移植自 Python material_clip/config_dialog）。本机全局生效。
/// 原创度/画质/并发喂 C# Prep；剪辑模式/LLM/音频等项持久化，供 prep 契约或后续剪辑管线消费。
/// 持久化到 %LocalAppData%/ChannelsPublisher/clip-config.json。</summary>
public sealed class ClipConfig
{
    // 剪辑模式（一键创作时参与）
    public List<ClipModeSetting> Modes { get; set; } = new()
    {
        new ClipModeSetting { Key = "highlight", Label = "高光", Enabled = true, Count = 5 },
        new ClipModeSetting { Key = "mashup", Label = "混剪", Enabled = false, Count = 5 },
    };

    // 选段质量
    public bool EpisodeQuota { get; set; }       // 按集时长分配选段名额
    public bool EnableLlmScore { get; set; }     // AI 复评分
    public bool AudioEnergy { get; set; }        // 音频能量加权
    public bool ShotDensity { get; set; }        // 镜头密度加权
    public bool LlmArrange { get; set; }         // LLM 叙事重排
    public bool SmoothSelection { get; set; } = true; // 片段去碎
    public bool PublishMeta { get; set; } = true;     // 生成发表文案

    // 并发 / 导出
    public int PipelineConcurrent { get; set; } = 4;  // CPU 步骤并发集数
    public int NetConcurrent { get; set; } = 4;       // 网络并发
    public string OutputQuality { get; set; } = "1080P";
    public int VideoBitrate { get; set; } = 0;        // 0=自动
    public string EncodeMode { get; set; } = "auto";  // auto/cbr/vbr
    public string VideoCodec { get; set; } = "h264";
    public string RenderSpeed { get; set; } = "medium";
    public bool HardwareEncode { get; set; } = true;

    // 开头标题卡
    public bool TitleCard { get; set; }
    public int TitleCardSeconds { get; set; } = 4;

    // 原创度扰动（喂 C# Prep）
    public bool OrigEnabled { get; set; }
    public bool OrigZoom { get; set; } = true;
    public bool OrigColor { get; set; }
    public bool OrigSpeed { get; set; }
    public bool OrigFade { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static string FilePath => Path.Combine(AppPaths.DataRoot, "clip-config.json");

    public static ClipConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClipConfig>(File.ReadAllText(FilePath), Options) ?? new ClipConfig();
        }
        catch { /* 损坏配置回退默认 */ }
        return new ClipConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
