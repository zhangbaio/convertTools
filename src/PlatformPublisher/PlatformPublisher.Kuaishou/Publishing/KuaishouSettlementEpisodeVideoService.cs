using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouSettlementEpisodeVideoService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "material-videos", "material-clip-output", "素材剪辑输出", "videos-智能重剪前",
    };

    private static readonly Regex SettlementEpisodePattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly Regex SortEpisodePattern = new(@"第\s*(\d+)\s*集|[Ee][Pp]\s*(\d+)|(\d+)", RegexOptions.Compiled);
    private static readonly Regex InvalidFileNameChars = new(@"[<>:""/\\|?*\u0000-\u001f]", RegexOptions.Compiled);

    public static IReadOnlyList<string> Prepare(string sourceDirectory, string workflowDirectory, string seriesTitle)
    {
        var sourceDir = Path.GetFullPath(sourceDirectory);
        var workflowDir = Path.GetFullPath(workflowDirectory);
        var videosDir = Path.Combine(workflowDir, "videos");
        var sourceVideos = Directory.Exists(sourceDir) ? Collect(sourceDir) : [];
        if (sourceVideos.Count == 0 && Directory.Exists(videosDir))
            sourceVideos = Collect(videosDir);
        if (sourceVideos.Count == 0)
            return [];

        var titleStem = SafeFileStem(seriesTitle);
        if (string.IsNullOrWhiteSpace(titleStem))
            titleStem = SafeFileStem(Path.GetFileName(workflowDir).TrimStart('_'));
        if (string.IsNullOrWhiteSpace(titleStem))
            titleStem = "短剧";

        var planned = ResolveEpisodeNumbers(sourceVideos);
        Directory.CreateDirectory(videosDir);
        foreach (var (episodeNumber, sourcePath) in planned)
        {
            var suffix = VideoExtensions.Contains(Path.GetExtension(sourcePath))
                ? Path.GetExtension(sourcePath).ToLowerInvariant()
                : ".mp4";
            var targetPath = Path.GetFullPath(Path.Combine(videosDir, $"{titleStem}-第{episodeNumber}集{suffix}"));
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(targetPath))
            {
                var targetSize = new FileInfo(targetPath).Length;
                if (targetSize > 0 && targetSize == new FileInfo(sourcePath).Length)
                    continue;
                if (targetSize > 0)
                    throw new InvalidOperationException(
                        $"第{episodeNumber}集已存在不同的视频文件：{targetPath}（{targetSize} 字节）与 {sourcePath}（{new FileInfo(sourcePath).Length} 字节）冲突。");
                File.Delete(targetPath);
            }

            MoveFile(sourcePath, targetPath);
        }

        IReadOnlyList<string> prepared = Collect(videosDir);
        var titled = prepared.Where(path => Path.GetFileNameWithoutExtension(path).StartsWith(titleStem, StringComparison.Ordinal)).ToArray();
        if (titled.Length > 0)
            prepared = titled;
        ValidatePrepared(prepared, videosDir);
        return prepared
            .Select(path => (Path: path, Episode: SettlementEpisode(Path.GetFileNameWithoutExtension(path))!.Value))
            .OrderBy(item => item.Episode)
            .Select(item => item.Path)
            .ToArray();
    }

    private static IReadOnlyList<(int Episode, string Path)> ResolveEpisodeNumbers(IReadOnlyList<string> sourceVideos)
    {
        var numbered = sourceVideos.Select(path => (Episode: SettlementEpisode(Path.GetFileNameWithoutExtension(path)), Path: path)).ToArray();
        if (numbered.All(item => item.Episode is null))
            return numbered.Select((item, index) => (index + 1, item.Path)).ToArray();

        var missing = numbered.Where(item => item.Episode is null).Select(item => Path.GetFileName(item.Path)).Take(5).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"剧集视频命名不一致，无法确定集号：{string.Join('、', missing)}。请使用“第N集”命名。");

        var duplicates = numbered.GroupBy(item => item.Episode!.Value).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"剧集视频集号重复：{string.Join('、', duplicates.Select(value => $"第{value}集"))}。");
        var actual = numbered.Select(item => item.Episode!.Value).OrderBy(value => value).ToArray();
        if (!actual.SequenceEqual(Enumerable.Range(1, actual.Length)))
        {
            var absent = Enumerable.Range(1, actual.Max()).Except(actual).Select(value => $"第{value}集");
            throw new InvalidOperationException($"剧集集号不连续，缺少 {string.Join('、', absent)}。");
        }

        return numbered.Select(item => (item.Episode!.Value, item.Path)).ToArray();
    }

    private static void ValidatePrepared(IReadOnlyList<string> prepared, string videosDir)
    {
        var episodes = new List<int>();
        foreach (var path in prepared)
        {
            var episode = SettlementEpisode(Path.GetFileNameWithoutExtension(path));
            if (episode is null)
                throw new InvalidOperationException($"整理后的剧集视频缺少集号：{Path.GetFileName(path)}（目录：{videosDir}）");
            episodes.Add(episode.Value);
        }

        var duplicates = episodes.GroupBy(value => value).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"整理后的剧集视频集号重复：{string.Join('、', duplicates.Select(value => $"第{value}集"))}（目录：{videosDir}）。");
        var ordered = episodes.OrderBy(value => value).ToArray();
        if (!ordered.SequenceEqual(Enumerable.Range(1, ordered.Length)))
        {
            var absent = Enumerable.Range(1, ordered.Max()).Except(ordered).Select(value => $"第{value}集");
            throw new InvalidOperationException($"剧集集号不连续，缺少 {string.Join('、', absent)}（目录：{videosDir}）。");
        }
    }

    private static List<string> Collect(string directory)
    {
        var videos = new List<string>();
        Walk(directory, videos);
        return videos
            .OrderBy(path => SortKey(path, directory))
            .ToList();
    }

    private static void Walk(string directory, List<string> videos)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(path)))
                continue;
            var stem = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
            if (stem.EndsWith(".transcode", StringComparison.Ordinal) || stem.EndsWith(".pad", StringComparison.Ordinal))
                continue;
            videos.Add(Path.GetFullPath(path));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (ExcludedDirectories.Contains(Path.GetFileName(child)))
                continue;
            Walk(child, videos);
        }
    }

    private static (int Rank, int Episode, string Path) SortKey(string path, string baseDirectory)
    {
        var relative = Path.GetRelativePath(baseDirectory, path).Replace('\\', '/').ToLowerInvariant();
        var episode = SortEpisode(Path.GetFileNameWithoutExtension(path));
        return episode is null ? (1, int.MaxValue, relative) : (0, episode.Value, relative);
    }

    private static int? SettlementEpisode(string stem)
    {
        var match = SettlementEpisodePattern.Match(stem);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) && value > 0 ? value : null;
    }

    private static int? SortEpisode(string stem)
    {
        var match = SortEpisodePattern.Match(stem);
        if (!match.Success)
            return null;
        var text = match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        return int.TryParse(text, out var value) && value > 0 ? value : null;
    }

    private static string SafeFileStem(string? value)
    {
        var text = InvalidFileNameChars.Replace(value ?? "", "");
        text = Regex.Replace(text, @"\s+", "").Trim(' ', '.');
        if (text.Length > 80)
            text = text[..80];
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
        };
        for (var index = 1; index < 10; index++)
        {
            reserved.Add($"COM{index}");
            reserved.Add($"LPT{index}");
        }

        if (reserved.Contains(text))
            text += "_";
        return text;
    }

    private static void MoveFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Move(sourcePath, targetPath);
        }
        catch (IOException)
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            File.Delete(sourcePath);
        }
    }
}
