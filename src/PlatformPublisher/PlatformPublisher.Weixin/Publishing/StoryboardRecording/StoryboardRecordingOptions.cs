using System.Globalization;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Weixin.Publishing;

public enum StoryboardTemplate
{
    Comprehensive,
    ThreeView,
}

public sealed record StoryboardRecordingOptions(
    string Episodes = "", StoryboardTemplate Template = StoryboardTemplate.Comprehensive,
    bool UseArk = false, bool Force = false);

public sealed class StoryboardArkSettings(
    string visionEndpoint, string visionModel, string visionKey,
    string imageEndpoint, string imageModel, string imageKey)
{
    public string VisionEndpoint { get; } = visionEndpoint;
    public string VisionModel { get; } = visionModel;
    public string VisionKey { get; } = visionKey;
    public string ImageEndpoint { get; } = imageEndpoint;
    public string ImageModel { get; } = imageModel;
    public string ImageKey { get; } = imageKey;
}

public sealed record StoryboardRecordingResult(
    string Directory, IReadOnlyList<int> Episodes, IReadOnlyList<int> RecordingEpisodes,
    IReadOnlyList<string> Files, bool Reused);

public static partial class StoryboardRecordingRules
{
    public static void ValidateSelection(string selection) => Parse(selection);

    public static IReadOnlyList<int> SelectEpisodes(string selection, IReadOnlyCollection<int> available)
    {
        var ranges = Parse(selection);
        if (available.Count == 0)
            throw new InvalidOperationException("找不到带集号的视频，请使用「剧名-第N集」命名。");
        if (ranges is null || (ranges.Count == 0 && available.Count == 1))
            return available.Order().ToArray();
        if (ranges.Count == 0)
            ranges = [(1, 1)];

        var availableNumbers = available.ToHashSet();
        var numbers = new SortedSet<int>();
        foreach (var (first, last) in ranges)
        {
            for (var episode = first; ; episode++)
            {
                if (!availableNumbers.Contains(episode))
                    throw new InvalidOperationException($"找不到第{episode}集视频。请检查项目素材和文件名。");
                numbers.Add(episode);
                if (episode == last)
                    break;
            }
        }
        return numbers.ToArray();
    }

    public static IReadOnlyList<int> SelectRecordingEpisodes(IReadOnlyList<int> episodes)
    {
        if (episodes.Count <= 1)
            return episodes.ToArray();
        return episodes.Contains(1) && episodes.Contains(4) ? [4, 1] : [episodes.Max(), episodes.Min()];
    }

    public static int? EpisodeNumber(string fileName)
    {
        var match = EpisodePattern().Match(Path.GetFileNameWithoutExtension(fileName));
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var episode)
            && episode > 0 ? episode : null;
    }

    private static List<(int First, int Last)>? Parse(string selection)
    {
        var text = selection.Trim();
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase) || text == "全部")
            return null;
        var ranges = new List<(int, int)>();
        if (text.Length == 0)
            return ranges;
        foreach (var item in text.Split([',', '，', '、', ';', '；']))
        {
            var match = SelectionPattern().Match(item.Trim());
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var first))
                throw new ArgumentException("分镜录屏集数格式不正确，请填写 1-4、1,3,7 或「全部」。");
            var last = first;
            if ((match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out last)) || first < 1 || last < first)
                throw new ArgumentException($"分镜录屏的集数范围无效：{item.Trim()}");
            ranges.Add((first, last));
        }
        return ranges;
    }

    [GeneratedRegex(@"第\s*([0-9]+)\s*集")]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"^([0-9]+)(?:\s*[-–—~～]\s*([0-9]+))?$")]
    private static partial Regex SelectionPattern();
}
