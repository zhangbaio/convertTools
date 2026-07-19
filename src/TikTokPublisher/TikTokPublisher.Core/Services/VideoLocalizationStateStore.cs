using System.Text.Json;

namespace TikTokPublisher.Core.Services;

public sealed class VideoLocalizationState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, VideoLocalizationEpisodeState> Episodes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VideoLocalizationEpisodeState
{
    public string VolcRunId { get; set; } = "";
    public string VolcAideoTaskId { get; set; } = "";
    public string VolcOutputVid { get; set; } = "";
    public string VolcOutputFileName { get; set; } = "";
    public string BaiduMediaId { get; set; } = "";
    public string BaiduTaskId { get; set; } = "";
    public string BaiduOutputUrl { get; set; } = "";
    public string AliyunInputObjectKey { get; set; } = "";
    public string AliyunOutputObjectKey { get; set; } = "";
    public string AliyunTaskId { get; set; } = "";
    public bool SubtitleRemoved { get; set; }
    public string ElevenLabsDubbingId { get; set; } = "";
    public string VolcTranslationTaskId { get; set; } = "";
    public string VolcTranslationTargetVid { get; set; } = "";
    public string DubbingProvider { get; set; } = "";
    public string DubbingStatus { get; set; } = "";
    public bool DubbedVideoDownloaded { get; set; }
    public bool SpeechRateChecked { get; set; }
    public bool SpeechRateRepaired { get; set; }
    public int FastSegmentCount { get; set; }
    public string CleanDubbedVideoPath { get; set; } = "";
    public string FinalSrtPath { get; set; } = "";
    public bool EnglishSrtDownloaded { get; set; }
    public bool SubtitlesBurned { get; set; }
}

public static class VideoLocalizationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string GetPath(string projectDir) =>
        Path.Combine(projectDir, ".tiktok", "video-localization-state.json");

    public static string GetLocalizationDirectory(string videoPath)
    {
        var stagingDir = Path.GetDirectoryName(Path.GetFullPath(videoPath))
            ?? throw new InvalidOperationException("无法确定视频所在目录。");
        var workflowProjectDir = Directory.GetParent(stagingDir)?.FullName
            ?? throw new InvalidOperationException("无法确定视频工作流目录。");
        return Path.Combine(workflowProjectDir, ".tiktok", "localization");
    }

    public static VideoLocalizationState Load(string projectDir)
    {
        var path = GetPath(projectDir);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<VideoLocalizationState>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch
        {
            // A corrupt sidecar must not make the whole upload queue unreadable.
        }
        return new VideoLocalizationState();
    }

    public static VideoLocalizationEpisodeState GetEpisode(VideoLocalizationState state, string videoPath)
    {
        var key = Path.GetFileName(videoPath);
        if (!state.Episodes.TryGetValue(key, out var episode))
            state.Episodes[key] = episode = new VideoLocalizationEpisodeState();
        return episode;
    }

    public static void Save(string projectDir, VideoLocalizationState state)
    {
        var path = GetPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, true);
    }
}
