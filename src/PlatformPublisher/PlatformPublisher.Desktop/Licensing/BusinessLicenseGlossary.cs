using System.Text.Json;

namespace PlatformPublisher.Desktop.Licensing;

public sealed class BusinessLicenseGlossary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, string>> _fields = Empty();

    public string Path { get; }

    public BusinessLicenseGlossary(string path)
    {
        Path = path;
        Load();
    }

    public string? Find(string field, string chinese)
    {
        chinese = chinese.Trim();
        if (chinese.Length == 0) return null;
        lock (_gate)
            return _fields.TryGetValue(field, out var map) && map.TryGetValue(chinese, out var english) && !string.IsNullOrWhiteSpace(english)
                ? english
                : null;
    }

    public void Remember(string field, string chinese, string english)
    {
        chinese = chinese.Trim();
        english = english.Trim();
        if (chinese.Length == 0 || english.Length == 0) return;
        lock (_gate)
        {
            if (!_fields.TryGetValue(field, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                _fields[field] = map;
            }
            map[chinese] = english;
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(_fields, JsonOptions));
        }
    }

    private void Load()
    {
        if (!File.Exists(Path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path));
            if (loaded is null) return;
            _fields = loaded.ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _fields = Empty();
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Empty() =>
        new(StringComparer.Ordinal)
        {
            ["name"] = new(StringComparer.Ordinal),
            ["type"] = new(StringComparer.Ordinal),
            ["address"] = new(StringComparer.Ordinal),
            ["scope"] = new(StringComparer.Ordinal),
            ["authority"] = new(StringComparer.Ordinal),
        };
}
