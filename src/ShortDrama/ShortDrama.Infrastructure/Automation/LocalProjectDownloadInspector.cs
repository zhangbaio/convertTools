using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation;

public sealed record LocalDownloadInspection(
    int ExpectedEpisodeCount,
    int DownloadedEpisodeCount,
    int MaxEpisodeNumber,
    bool HasIncompleteArtifacts,
    IReadOnlyCollection<int> DownloadedEpisodeNumbers)
{
    public bool HasAnyDownloads => DownloadedEpisodeCount > 0;

    public bool IsComplete =>
        !HasIncompleteArtifacts &&
        ExpectedEpisodeCount > 0 &&
        DownloadedEpisodeNumbers.Count >= ExpectedEpisodeCount &&
        Enumerable.Range(1, ExpectedEpisodeCount).All(episode => DownloadedEpisodeNumbers.Contains(episode));
}

public static class LocalProjectDownloadInspector
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] IncompleteExtensions = [".aria2", ".part", ".partial", ".download", ".crdownload", ".tmp"];
    private static readonly Regex EpisodeNameRegex = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly Regex TrailingNumberRegex = new(@"(\d+)(?!.*\d)", RegexOptions.Compiled);

    public static LocalDownloadInspection Inspect(string projectDir)
    {
        var downloadedEpisodes = EnumerateDownloadedEpisodeNumbers(projectDir);
        var maxEpisodeNumber = downloadedEpisodes.Count > 0 ? downloadedEpisodes.Max() : 0;
        var expectedEpisodeCount = ReadConfiguredEpisodeCount(projectDir);
        if (expectedEpisodeCount <= 0)
        {
            expectedEpisodeCount = maxEpisodeNumber;
        }

        return new LocalDownloadInspection(
            ExpectedEpisodeCount: expectedEpisodeCount,
            DownloadedEpisodeCount: downloadedEpisodes.Count,
            MaxEpisodeNumber: maxEpisodeNumber,
            HasIncompleteArtifacts: HasIncompleteArtifactsInProject(projectDir),
            DownloadedEpisodeNumbers: downloadedEpisodes);
    }

    public static int ResolveConfiguredConcurrency(string projectDir, int defaultValue = 3)
    {
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return defaultValue;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return GetInt(document.RootElement, "concurrent") ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static HashSet<int> EnumerateDownloadedEpisodeNumbers(string projectDir)
    {
        var episodes = new HashSet<int>();
        if (!Directory.Exists(projectDir))
        {
            return episodes;
        }

        foreach (var path in Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var match = EpisodeNameRegex.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var episode))
            {
                episodes.Add(episode);
                continue;
            }

            match = TrailingNumberRegex.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out episode))
            {
                episodes.Add(episode);
            }
        }

        return episodes;
    }

    private static bool HasIncompleteArtifactsInProject(string projectDir)
    {
        if (!Directory.Exists(projectDir))
        {
            return false;
        }

        return Directory.EnumerateFiles(projectDir, "*", SearchOption.TopDirectoryOnly)
            .Any(path => IncompleteExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static int ReadConfiguredEpisodeCount(string projectDir)
    {
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return GetInt(document.RootElement, "episodeCount") ?? GetInt(document.RootElement, "episode_count") ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }
}
