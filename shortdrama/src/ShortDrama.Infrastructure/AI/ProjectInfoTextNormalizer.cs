using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.AI;

internal static partial class ProjectInfoTextNormalizer
{
    [GeneratedRegex("[^0-9A-Za-z\\u4e00-\\u9fff]", RegexOptions.Compiled)]
    private static partial Regex InvalidTextRegex();

    [GeneratedRegex("[#,，、|/\\\\\\s]+", RegexOptions.Compiled)]
    private static partial Regex TagSplitRegex();

    public static string SanitizeShortTitle(string? value, int maxLength = 15)
    {
        var cleaned = InvalidTextRegex().Replace(value ?? string.Empty, string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        return cleaned[..Math.Min(Math.Max(1, maxLength), cleaned.Length)];
    }

    public static string SanitizeTag(string? value, int? maxLength = 8)
    {
        var cleaned = InvalidTextRegex().Replace(value ?? string.Empty, string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        if (maxLength is null || maxLength <= 0)
        {
            return cleaned;
        }

        return cleaned[..Math.Min(maxLength.Value, cleaned.Length)];
    }

    public static IReadOnlyList<string> NormalizeTags(string? value, int maxCount = 6, int maxLength = 8)
    {
        var rawTags = string.IsNullOrWhiteSpace(value)
            ? []
            : TagSplitRegex().Split(value).Where(item => !string.IsNullOrWhiteSpace(item));
        return NormalizeTags(rawTags, maxCount, maxLength);
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string?> values, int maxCount = 6, int maxLength = 8)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var tag = SanitizeTag(raw, maxLength);
            if (tag.Length == 0 || !seen.Add(tag))
            {
                continue;
            }

            tags.Add(tag);
            if (tags.Count >= Math.Max(1, maxCount))
            {
                break;
            }
        }

        return tags;
    }

    public static string FormatTags(IEnumerable<string?> values, string? leadingTag = null)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var leading = SanitizeTag(leadingTag, null);
        if (leading.Length > 0 && seen.Add(leading))
        {
            ordered.Add(leading);
        }

        foreach (var tag in NormalizeTags(values))
        {
            if (seen.Add(tag))
            {
                ordered.Add(tag);
            }
        }

        return string.Concat(ordered.Select(tag => $"#{tag}"));
    }
}
