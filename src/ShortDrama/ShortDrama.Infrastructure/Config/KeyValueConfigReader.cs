using System.Globalization;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Config;

internal static class KeyValueConfigReader
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到配置文件: {path}", path);
        }

        var content = File.ReadAllText(path);
        var trimmed = content.TrimStart();
        return trimmed.StartsWith('{')
            ? ReadJsonMap(content)
            : ReadLegacyKeyValueMap(content);
    }

    private static IReadOnlyDictionary<string, string> ReadLegacyKeyValueMap(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            map[key] = value;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ReadJsonMap(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                JsonValueKind.Array or JsonValueKind.Object => property.Value.GetRawText(),
                _ => string.Empty
            };
        }

        return map;
    }

    public static string SerializeJson(IDictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static object? NormalizeValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return value;
    }
}
