using System.Text.Json;

namespace TikTokPublisher.Core.Models;

public sealed record TikTokPublishAccountOption(string Country, string DisplayName, string Handle);

public sealed record TikTokPublishAccountVisibleOption(
    string Country,
    string DisplayName,
    string Handle,
    bool Checked);

public sealed record TikTokPublishAccountSelectionRetention(
    IReadOnlyList<string> KeptKeys,
    IReadOnlyList<string> DroppedKeys);

public sealed record TikTokPublishAccountSelectionPlan(
    IReadOnlyList<string> KeysToCheck,
    IReadOnlyList<string> KeysToUncheck,
    IReadOnlyList<string> MissingKeys);

public static class TikTokPublishAccountCatalog
{
    public const char KeySeparator = '\u001f';

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildKey(string? country, string? displayName)
    {
        var normalizedCountry = NormalizeLabel(country);
        var normalizedName = NormalizeLabel(displayName);
        if (normalizedCountry.Length == 0 || normalizedName.Length == 0)
            return "";
        return normalizedCountry + KeySeparator + normalizedName;
    }

    public static string DescribeKey(string? key)
    {
        var parts = (key ?? "").Split(KeySeparator, 2);
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            return $"{parts[0]} / {parts[1]}";
        return NormalizeLabel(key);
    }

    public static IReadOnlyList<TikTokPublishAccountOption> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var items = JsonSerializer.Deserialize<List<TikTokPublishAccountOption>>(json, JsonOptions);
            return Normalize(items);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<TikTokPublishAccountOption>? accounts) =>
        JsonSerializer.Serialize(Normalize(accounts), JsonOptions);

    public static IReadOnlyList<TikTokPublishAccountOption> Normalize(
        IEnumerable<TikTokPublishAccountOption>? accounts)
    {
        var results = new List<TikTokPublishAccountOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts ?? [])
        {
            var country = NormalizeLabel(account.Country);
            var displayName = NormalizeLabel(account.DisplayName);
            var key = BuildKey(country, displayName);
            if (key.Length == 0 || !seen.Add(key))
                continue;

            results.Add(new TikTokPublishAccountOption(
                country,
                displayName,
                NormalizeLabel(account.Handle)));
        }

        return results;
    }

    public static TikTokPublishAccountSelectionRetention Retain(
        IEnumerable<string>? selectedKeys,
        IEnumerable<TikTokPublishAccountOption>? catalog)
    {
        var available = Normalize(catalog)
            .Select(account => BuildKey(account.Country, account.DisplayName))
            .ToHashSet(StringComparer.Ordinal);
        var kept = new List<string>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in selectedKeys ?? [])
        {
            var key = NormalizeKey(raw);
            if (key.Length == 0 || !seen.Add(key))
                continue;

            if (available.Contains(key))
                kept.Add(key);
            else
                dropped.Add(key);
        }

        return new TikTokPublishAccountSelectionRetention(kept, dropped);
    }

    public static TikTokPublishAccountSelectionPlan Plan(
        IEnumerable<TikTokPublishAccountVisibleOption>? visible,
        IEnumerable<string>? selectedKeys)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in selectedKeys ?? [])
        {
            var key = NormalizeKey(raw);
            if (key.Length > 0)
                wanted.Add(key);
        }

        var visibleKeys = new HashSet<string>(StringComparer.Ordinal);
        var toCheck = new List<string>();
        var toUncheck = new List<string>();
        foreach (var option in visible ?? [])
        {
            var key = BuildKey(option.Country, option.DisplayName);
            if (key.Length == 0 || !visibleKeys.Add(key))
                continue;

            var shouldSelect = wanted.Contains(key);
            if (shouldSelect && !option.Checked)
                toCheck.Add(key);
            else if (!shouldSelect && option.Checked)
                toUncheck.Add(key);
        }

        var missing = wanted
            .Where(key => !visibleKeys.Contains(key))
            .ToArray();
        return new TikTokPublishAccountSelectionPlan(toCheck, toUncheck, missing);
    }

    private static string NormalizeKey(string? key)
    {
        var parts = (key ?? "").Split(KeySeparator, 2);
        return parts.Length == 2 ? BuildKey(parts[0], parts[1]) : "";
    }

    private static string NormalizeLabel(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
