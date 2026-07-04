using System.Text.Json;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Config;

/// <summary>TikTok 发布全局默认（对齐 Python 发布配置子集）。</summary>
public sealed class TikTokPublishConfig
{
    public bool Enabled { get; set; } = true;
    public string RunStrategy { get; set; } = "all"; // all / resume / retry_failed
    public string FinalAction { get; set; } = "none"; // none / save / publish
    public bool PauseOnError { get; set; } = true;

    public string DramaName { get; set; } = "";
    public string DescriptionTemplate { get; set; } = "";
    public bool FillDescription { get; set; } = true;
    public bool ReplaceCover { get; set; }
    public string CoverImagePath { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string FilePath => Path.Combine(AppPaths.DataRoot, "tiktok-publish-config.json");

    public static TikTokPublishConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TikTokPublishConfig>(File.ReadAllText(FilePath), Options)
                       ?? new TikTokPublishConfig();
        }
        catch { /* 回退默认 */ }
        return new TikTokPublishConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
