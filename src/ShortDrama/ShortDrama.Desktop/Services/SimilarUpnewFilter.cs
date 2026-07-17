using ShortDrama.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public static class SimilarUpnewFilter
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static IReadOnlyList<DramaSearchItem> Filter(
        IEnumerable<DramaSearchItem> items,
        IEnumerable<string> terms,
        string sensitivity)
    {
        var normalizedTerms = terms
            .Select(NormalizeText)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTerms.Length == 0)
        {
            return [];
        }

        var threshold = ResolveThreshold(sensitivity);
        return items
            .Select(item => new
            {
                Item = item,
                Score = normalizedTerms.Max(term => Score(term, item))
            })
            .Where(item => item.Score >= threshold)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => ParsePublishTime(item.Item.PublishTime))
            .ThenBy(item => item.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Item)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseTerms(string value)
    {
        return (value ?? string.Empty)
            .Replace('，', ',')
            .Replace('、', ',')
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();
    }

    private static double Score(string term, DramaSearchItem item)
    {
        var title = NormalizeText(item.Title);
        var category = NormalizeText(item.Category);
        var intro = NormalizeText(item.Intro);

        var titleScore = TextScore(term, title);
        var categoryScore = TextScore(term, category) * 0.84d;
        var introScore = TextScore(term, intro) * 0.68d;

        return Math.Max(titleScore, Math.Max(categoryScore, introScore));
    }

    private static double TextScore(string term, string text)
    {
        if (term.Length == 0 || text.Length == 0)
        {
            return 0;
        }

        if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return term.Length == text.Length ? 1.0d : 0.92d;
        }

        if (term.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(0.88d, text.Length / (double)term.Length);
        }

        var termTokens = Tokenize(term);
        var textTokens = Tokenize(text);
        if (termTokens.Count == 0 || textTokens.Count == 0)
        {
            return CharacterDice(term, text);
        }

        var overlap = termTokens.Count(textTokens.Contains);
        var tokenScore = overlap <= 0 ? 0d : overlap / (double)Math.Max(termTokens.Count, textTokens.Count);
        return Math.Max(tokenScore, CharacterDice(term, text) * 0.82d);
    }

    private static HashSet<string> Tokenize(string text)
    {
        return TokenRegex.Matches(text)
            .Select(match => match.Value)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static double CharacterDice(string left, string right)
    {
        var leftSet = left.ToHashSet();
        var rightSet = right.ToHashSet();
        if (leftSet.Count == 0 || rightSet.Count == 0)
        {
            return 0;
        }

        leftSet.IntersectWith(rightSet);
        return 2d * leftSet.Count / (left.Length + right.Length);
    }

    private static string NormalizeText(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    private static double ResolveThreshold(string sensitivity)
    {
        return (sensitivity ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "loose" => 0.42d,
            "strict" => 0.72d,
            _ => 0.56d
        };
    }

    private static DateTime ParsePublishTime(string value)
    {
        if (long.TryParse((value ?? string.Empty).Trim(), out var numeric))
        {
            var seconds = numeric > 10_000_000_000L ? numeric / 1000 : numeric;
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
            ? parsed
            : DateTime.MinValue;
    }
}
