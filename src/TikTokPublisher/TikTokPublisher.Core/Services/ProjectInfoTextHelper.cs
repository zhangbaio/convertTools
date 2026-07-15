using System.Text;

namespace TikTokPublisher.Core.Services;

public static class ProjectInfoTextHelper
{
    public static Dictionary<string, string> ParseInfoFile(string infoPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(infoPath)) return result;

        foreach (var rawLine in File.ReadAllLines(infoPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var sepIndex = FindFieldSeparatorIndex(line);
            if (sepIndex <= 0) continue;

            var key = line[..sepIndex].Trim();
            var value = line[(sepIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                result[key] = value;
        }

        return result;
    }

    public static Dictionary<string, string> MergeProjectInfo(params string?[] infoPaths)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in infoPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            foreach (var (key, value) in ParseInfoFile(path))
                merged[key] = value;
        }

        return merged;
    }

    public static void UpdateFields(string infoPath, IReadOnlyDictionary<string, string> updates)
    {
        var normalized = updates
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.Ordinal);
        if (normalized.Count == 0) return;

        var lines = File.Exists(infoPath)
            ? File.ReadAllLines(infoPath, Encoding.UTF8).ToList()
            : new List<string>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            var sepIndex = FindFieldSeparatorIndex(line);
            if (sepIndex <= 0) continue;

            var key = line[..sepIndex].Trim();
            if (!normalized.TryGetValue(key, out var value)) continue;
            lines[i] = $"{key}: {value}";
            replaced.Add(key);
        }

        foreach (var (key, value) in normalized)
        {
            if (replaced.Contains(key)) continue;
            lines.Add($"{key}: {value}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
        File.WriteAllText(infoPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Encoding.UTF8);
    }

    internal static int FindFieldSeparatorIndex(string line)
    {
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] is ':' or '：')
                return index;
        }

        return -1;
    }
}
