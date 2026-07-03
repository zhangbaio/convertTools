using System.Text.Json;

namespace TikTokPublisher.Core.Models;

public sealed record ExpectedFullPriceOption(string Value, string Label);

public static class ExpectedFullPriceOptionsJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ExpectedFullPriceOption> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json, JsonOptions);
            if (items is null) return [];
            return items
                .Select(item => new ExpectedFullPriceOption(
                    item.GetValueOrDefault("value") ?? "",
                    item.GetValueOrDefault("label") ?? ""))
                .Where(o => !string.IsNullOrWhiteSpace(o.Value) && !string.IsNullOrWhiteSpace(o.Label))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<ExpectedFullPriceOption> options)
    {
        var payload = options
            .Select(o => new Dictionary<string, string>
            {
                ["value"] = o.Value,
                ["label"] = o.Label,
            })
            .ToList();
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
