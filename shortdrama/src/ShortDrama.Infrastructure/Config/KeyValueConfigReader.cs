namespace ShortDrama.Infrastructure.Config;

internal static class KeyValueConfigReader
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到配置文件: {path}", path);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(path))
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
}
