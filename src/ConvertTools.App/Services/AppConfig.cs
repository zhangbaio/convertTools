using System.Text.Json;

namespace ConvertTools.App.Services;

/// <summary>ConvertTools 全局配置（ffmpeg 路径 / AI 接口 / 默认结束动作），
/// 持久化到 %LocalAppData%/ConvertTools/config.json。转码/设置等共享。</summary>
public sealed class AppConfig
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string AiEndpoint { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string DefaultFinalAction { get; set; } = "none";

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertTools");
    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>进程内当前配置（保存后刷新）。</summary>
    public static AppConfig Current { get; private set; } = Load();

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile), Options) ?? new AppConfig();
        }
        catch { /* 损坏配置回退默认 */ }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, Options));
        Current = this;
    }

    public AppConfig Clone() => new()
    {
        FfmpegPath = FfmpegPath,
        AiEndpoint = AiEndpoint,
        AiApiKey = AiApiKey,
        AiModel = AiModel,
        DefaultFinalAction = DefaultFinalAction,
    };
}
