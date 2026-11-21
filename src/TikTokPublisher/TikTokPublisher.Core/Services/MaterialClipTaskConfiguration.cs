using System.Text.Json;
using ChannelsPublisher.Clip;
using ChannelsPublisher.Core.Config;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed class MaterialClipTaskConfiguration
{
    private readonly ClipConfig _clip;

    public MaterialClipTaskConfiguration(ClipConfig defaults)
    {
        _clip = JsonSerializer.Deserialize<ClipConfig>(JsonSerializer.Serialize(defaults))!;
        if (!_clip.Modes.Any(mode => mode.Enabled))
            _clip.Mode("highlight").Enabled = true;
    }

    public MaterialClipTaskConfiguration Copy() => new(_clip);

    public IReadOnlyList<string> EnabledModes => _clip.Modes.Where(mode => mode.Enabled).Select(mode => mode.Key).ToArray();
    public bool StoryHighlights { get => _clip.StoryHighlights; set => _clip.StoryHighlights = value; }
    public bool HardSubtitles { get => _clip.StoryHardSubtitles; set => _clip.StoryHardSubtitles = value; }
    public bool VisualReview { get => _clip.StoryVisualReview; set => _clip.StoryVisualReview = value; }

    public void SetModeEnabled(string mode, bool enabled) => _clip.Mode(mode).Enabled = enabled;

    public IReadOnlyList<ClipTimeRange> GetRanges(string mode) => _clip.RangesFor(mode).Take(10)
        .Select(range => new ClipTimeRange(range.MinSeconds, range.MaxSeconds)).ToArray();

    public void SetRanges(string mode, IReadOnlyList<ClipTimeRange> ranges)
    {
        if (ranges.Count is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(ranges), "每种模式需要设置 1–10 条成片。");
        _clip.Mode(mode).Count = ranges.Count;
        _clip.RangesByMode[mode] = ranges.Select(range =>
            new ClipDurationRange { MinSeconds = range.MinSeconds, MaxSeconds = range.MaxSeconds }).ToList();
    }

    public string? Validate()
    {
        if (EnabledModes.Count == 0)
            return "请至少选择一种剪辑模式。";
        foreach (var mode in EnabledModes)
        {
            var ranges = GetRanges(mode);
            for (var index = 0; index < ranges.Count; index++)
            {
                var range = ranges[index];
                if (range.MinSeconds < 1 || range.MaxSeconds > 1800 || range.MinSeconds > range.MaxSeconds)
                    return $"{_clip.Mode(mode).Label}第 {index + 1} 条的时长须在 1–1800 秒内，且下限不能大于上限。";
            }
        }
        return null;
    }

    public ClipEngineOptions BuildOptions(MaterialClipConnections services, ClientSettings client,
        string title, string outputFolder)
    {
        if (Validate() is { } error)
            throw new InvalidDataException(error);
        var options = MaterialClipSettingsStore.BuildOptions(new MaterialClipSettings { Clip = _clip, Services = services }, client);
        options.ProjectTitle = title;
        options.OutputDirectory = outputFolder;
        return options;
    }
}
