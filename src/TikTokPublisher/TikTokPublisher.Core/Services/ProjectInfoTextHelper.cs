using System.Text;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class ProjectInfoTextHelper
{
    private static readonly Regex OriginalSynopsisLine = new(
        @"(?m)^[^\S\r\n]*原简介[^\S\r\n]*[:：][^\S\r\n]*(?<value>[^\r\n]*)(?:\r\n|\n|\r|\z)");
    private static readonly Regex CurrentSynopsisLine = new(@"(?m)^[^\S\r\n]*(?:简介|描述|剧情简介)[^\S\r\n]*[:：]");

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

    internal static string SingleLineSynopsis(string text) => string.Join(' ',
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static bool WriteOriginalSynopsisIfMissing(string infoPath, string synopsis, string originalTitle)
    {
        if (!ProjectOriginalSynopsisService.IsUsable(synopsis, originalTitle))
            return false;

        var snapshot = File.ReadAllBytes(infoPath);
        using var reader = new StreamReader(new MemoryStream(snapshot), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        var contents = reader.ReadToEnd();
        var originalFields = OriginalSynopsisLine.Matches(contents);
        if (originalFields.Any(field => ProjectOriginalSynopsisService.IsUsable(field.Groups["value"].Value, originalTitle)))
            return false;

        var originalLine = $"{ProjectOriginalSynopsisService.FieldName}: {SingleLineSynopsis(synopsis)}";
        var newline = Regex.Match(contents, @"\r\n|\n|\r");
        var lineBreak = newline.Success ? newline.Value : Environment.NewLine;
        var currentField = CurrentSynopsisLine.Match(contents);
        // Append the source line immediately above the publishable synopsis.
        if (originalFields.Count > 0)
        {
            contents = OriginalSynopsisLine.Replace(contents, _ => "");
            currentField = CurrentSynopsisLine.Match(contents);
        }

        var updated = currentField.Success
            ? contents.Insert(currentField.Index, originalLine + lineBreak)
            : contents + (contents.Length == 0 || contents.EndsWith(lineBreak, StringComparison.Ordinal) ? "" : lineBreak)
              + originalLine + lineBreak;

        var temporary = Path.Combine(Path.GetDirectoryName(infoPath)!, $".original-synopsis-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, updated, reader.CurrentEncoding);
            if (!snapshot.AsSpan().SequenceEqual(File.ReadAllBytes(infoPath)))
                throw new IOException("短剧信息在补齐期间发生变化，请重新执行。");

            File.Move(temporary, infoPath, overwrite: true);
            return true;
        }
        finally
        {
            ResilientFileSystem.TryDeleteFile(temporary);
        }
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
