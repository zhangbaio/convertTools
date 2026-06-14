using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

internal sealed record UploadExpectedMarkerStatus(int MatchedExpectedCount, int ExpectedCount, bool HasUploadedUi)
{
    public bool HasAllMatches => ExpectedCount > 0 && MatchedExpectedCount >= ExpectedCount;
}

internal static class WeixinUploadMarkerMatcher
{
    public static IReadOnlyList<string> BuildMarkers(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Array.Empty<string>();
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (!string.IsNullOrWhiteSpace(withoutExtension))
        {
            results.Add(withoutExtension);
        }

        var episodeMatch = Regex.Match(withoutExtension, @"第\s*\d+\s*集");
        if (episodeMatch.Success)
        {
            results.Add(episodeMatch.Value);
        }

        return results.ToArray();
    }

    public static UploadExpectedMarkerStatus Evaluate(
        string? text,
        IReadOnlyList<string>? linkTexts,
        IReadOnlyList<string> expectedPaths)
    {
        var normalizedText = (text ?? string.Empty).ToLowerInvariant();
        var normalizedLinks = (linkTexts ?? Array.Empty<string>())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.ToLowerInvariant())
            .ToArray();

        var markerGroups = expectedPaths
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(BuildMarkers)
            .Where(static markers => markers.Count > 0)
            .ToArray();

        var matchedCount = markerGroups.Count(markers =>
            markers.Any(marker =>
            {
                var token = marker.ToLowerInvariant();
                return normalizedText.Contains(token, StringComparison.Ordinal) ||
                       normalizedLinks.Any(link => link.Contains(token, StringComparison.Ordinal));
            }));

        var hasUploadedUi =
            normalizedText.Contains("重新选择", StringComparison.Ordinal) ||
            normalizedText.Contains("重新上传", StringComparison.Ordinal) ||
            normalizedText.Contains("删除", StringComparison.Ordinal) ||
            normalizedText.Contains("移除", StringComparison.Ordinal) ||
            normalizedText.Contains("预览", StringComparison.Ordinal);

        return new UploadExpectedMarkerStatus(matchedCount, markerGroups.Length, hasUploadedUi);
    }
}
