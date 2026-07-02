using System.Text;

namespace ChannelsPublisher.Clip;

/// <summary>一句带时间戳的字幕（毫秒）。</summary>
public sealed record SubtitleSegment(int StartMs, int EndMs, string Text);

/// <summary>一集源视频。</summary>
public sealed record EpisodeSource(int EpisodeIndex, string VideoPath);

/// <summary>一个候选高光片段（含 4 维评分与综合分）。</summary>
public sealed class ClipCandidate
{
    public int EpisodeIndex { get; init; }
    public string VideoPath { get; init; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = "";
    public double Conflict { get; set; }
    public double Twist { get; set; }
    public double Emotion { get; set; }
    public double Cliffhanger { get; set; }
    public double Total { get; set; }

    // 可选信号（音频能量 0-10 / 镜头密度 0-10）；未计算则 0。
    public double AudioEnergy { get; set; }
    public double ShotDensity { get; set; }

    // LLM 复评分产出的元数据（用于发表文案 sidecar）。
    public string Summary { get; set; } = "";
    public string Title { get; set; } = "";
    public string RecommendReason { get; set; } = "";
    public List<string> Tags { get; set; } = new();

    public int DurationMs => Math.Max(0, EndMs - StartMs);
    // 钩子分：悬念为主、反转次之、综合辅助（移植自 rendering_highlight hook_score）。
    public double HookScore => 0.45 * Cliffhanger + 0.35 * Twist + 0.20 * Total;
}

/// <summary>剪辑引擎选项（调用方从 ClipConfig + 用户 ASR 配置映射而来）。</summary>
public sealed class ClipEngineOptions
{
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;
    public List<string> Modes { get; set; } = new() { "highlight" }; // highlight/mashup（切片/解说后续）
    public int ClipCount { get; set; } = 3;            // 每模式产出短片条数
    public int ClipMinSeconds { get; set; } = 60;      // 每条时长下限
    public int ClipMaxSeconds { get; set; } = 120;     // 每条时长上限
    public string RenderSpeed { get; set; } = "fast";  // fast/balanced/quality
    public bool HardwareEncode { get; set; } = true;   // 预留（当前用 libx264 CRF）

    // 可选选段信号
    public bool AudioEnergy { get; set; } = true;      // ffmpeg ebur128 响度加权
    public bool EnableLlmScore { get; set; }           // AI 复评分（需 AI 文本接口）

    // 火山在线 ASR
    public string VolcAppId { get; set; } = "";
    public string VolcAccessToken { get; set; } = "";
    public string AsrLanguage { get; set; } = "zh-CN";

    // AI 文本接口（LLM 复评分 / 解说脚本 / 文案，OpenAI 兼容 chat/completions）
    public string AiEndpoint { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";

    // 解说（commentary）：火山 TTS 复用 Volc 凭据；语音/旁白比例/风格
    public string TtsVoiceType { get; set; } = "BV701_streaming";
    public string TtsCluster { get; set; } = "volcano_tts";
    public double TtsSpeedRatio { get; set; } = 1.0;
    public double CommentaryNarrationRatio { get; set; } = 70.0;      // 旁白占比 40-100
    public string CommentaryStyleStrength { get; set; } = "standard"; // subtle/standard/strong
    public bool BurnSubtitles { get; set; } = true;                   // 解说段：抹除原字幕(底部模糊带)+烧录解说字幕

    // 原创度后处理（对成片做确定性轻扰动，降低重复率）
    public bool OrigEnabled { get; set; }
    public bool OrigZoom { get; set; } = true;
    public bool OrigColor { get; set; }
    public bool OrigSpeed { get; set; }
    public bool OrigFade { get; set; }
    public string OrigStickerDir { get; set; } = "";                  // 贴纸/水印 PNG 目录，随机叠一张到随机角落

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
}

public sealed record ClipEngineResult(bool Ok, IReadOnlyList<string> Outputs, string? Error);

/// <summary>四维关键词表（移植自 material_clip/constants.py）。</summary>
internal static class Keywords
{
    public static readonly string[] Conflict = { "闭嘴", "滚", "住手", "你敢", "你疯了", "离婚", "打", "放手", "别碰我", "混蛋" };
    public static readonly string[] Twist = { "原来", "竟然", "居然", "你是", "她就是", "真相", "身份", "幕后", "秘密", "不是" };
    public static readonly string[] Emotion = { "我爱你", "求你", "不要走", "别离开", "对不起", "哭", "喜欢你", "救我", "心疼" };
    public static readonly string[] Cliffhanger = { "到底是谁", "为什么", "怎么会", "难道", "你猜", "等一下", "还没结束", "真的是你" };
}

/// <summary>SRT 时间与写出。</summary>
public static class Srt
{
    public static string ToClock(int ms)
    {
        int total = ms / 1000, msPart = ms % 1000;
        int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
        return $"{h:D2}:{m:D2}:{s:D2},{msPart:D3}";
    }

    public static string Write(IReadOnlyList<SubtitleSegment> segs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(ToClock(segs[i].StartMs)).Append(" --> ").Append(ToClock(segs[i].EndMs)).Append('\n');
            sb.Append(segs[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }
}
