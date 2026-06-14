using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Parsing;

public sealed class TxtProjectInfoParser : IProjectInfoParser
{
    private const string FileName = "短剧信息.txt";
    private const decimal CostPerMinuteYuan = 1500m;
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];

    public async Task<ProjectInfo> ParseAsync(string projectDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new ArgumentException("项目目录不能为空。", nameof(projectDir));
        }

        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {projectDir}");
        }

        var sourceFilePath = Path.Combine(projectDir, FileName);
        if (!File.Exists(sourceFilePath))
        {
            var fallback = TryBuildFallbackProjectInfo(projectDir, sourceFilePath);
            if (fallback is not null)
            {
                return fallback;
            }

            throw new FileNotFoundException($"未找到项目说明文件: {sourceFilePath}", sourceFilePath);
        }

        var lines = await File.ReadAllLinesAsync(sourceFilePath, Encoding.UTF8, cancellationToken);
        var map = ParseKeyValueLines(lines);

        var originalTitle = GetOptional(map, "原剧名") ?? string.Empty;
        var newTitle = GetOptional(map, "新剧名");
        var title = FirstNonEmpty(newTitle, originalTitle);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("缺少剧名，需至少提供“新剧名”或“原剧名”。");
        }

        var episodeCount = ParsePositiveInt(GetRequired(map, "集数"), "集数");
        var totalMinutes = ParseMinutes(GetRequired(map, "时长"));
        var costAmountWan = ParseCostWan(GetRequired(map, "成本"));
        var companyName = GetRequired(map, "制作公司");

        if (costAmountWan >= 100m)
        {
            throw new InvalidOperationException($"总投资额必须小于100万元，当前为 {costAmountWan.ToString(CultureInfo.InvariantCulture)} 万元。");
        }

        return new ProjectInfo(
            OriginalTitle: originalTitle,
            Title: title,
            Tagline: GetOptional(map, "推荐语"),
            Synopsis: GetOptional(map, "简介"),
            ShortTitle: GetOptional(map, "短标题"),
            Tags: GetOptional(map, "标签"),
            EpisodeCount: episodeCount,
            TotalMinutes: totalMinutes,
            CostAmountWan: costAmountWan,
            CompanyName: companyName,
            ProjectDir: projectDir,
            SourceFilePath: sourceFilePath);
    }

    private static ProjectInfo? TryBuildFallbackProjectInfo(string projectDir, string sourceFilePath)
    {
        var metadataDir = ResolveMetadataDirectory(projectDir);
        if (string.IsNullOrWhiteSpace(metadataDir))
        {
            return null;
        }

        var metadataPath = Path.Combine(metadataDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            var originalTitle = GetString(root, "originalTitle")
                ?? GetString(root, "title")
                ?? Path.GetFileName(metadataDir);
            var title = GetString(root, "title") ?? originalTitle;
            var episodeCount = GetInt(root, "episodeCount")
                ?? GetInt(root, "episode_count")
                ?? CountVideoFiles(metadataDir)
                ?? CountVideoFiles(Path.Combine(projectDir, "videos"))
                ?? 1;
            var totalMinutes = Math.Max(1, episodeCount);

            return new ProjectInfo(
                OriginalTitle: originalTitle,
                Title: title,
                Tagline: string.Empty,
                Synopsis: string.Empty,
                ShortTitle: string.Empty,
                Tags: string.Empty,
                EpisodeCount: Math.Max(1, episodeCount),
                TotalMinutes: totalMinutes,
                CostAmountWan: CalculateCostAmountWan(totalMinutes),
                CompanyName: "未填写公司",
                ProjectDir: projectDir,
                SourceFilePath: metadataPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveMetadataDirectory(string projectDir)
    {
        if (File.Exists(Path.Combine(projectDir, "shortdrama-project.json")))
        {
            return projectDir;
        }

        var directory = new DirectoryInfo(projectDir);
        if (directory.Parent is null)
        {
            return null;
        }

        if (string.Equals(directory.Parent.Name, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            var rootDir = directory.Parent.Parent?.FullName;
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                return null;
            }

            var normalizedName = directory.Name.TrimStart('_');
            var directCandidate = Path.Combine(rootDir, normalizedName);
            if (File.Exists(Path.Combine(directCandidate, "shortdrama-project.json")))
            {
                return directCandidate;
            }

            foreach (var candidate in Directory.EnumerateDirectories(rootDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(candidate);
                if (name.StartsWith(".", StringComparison.Ordinal) ||
                    string.Equals(name, "workflow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "config", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var metadataPath = Path.Combine(candidate, "shortdrama-project.json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = document.RootElement;
                    var title = GetString(root, "title");
                    var originalTitle = GetString(root, "originalTitle");
                    if (string.Equals(title, normalizedName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(originalTitle, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore invalid metadata files and continue searching.
                }
            }
        }

        return null;
    }

    private static int? CountVideoFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var count = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        return count > 0 ? count : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }

    private static Dictionary<string, string> ParseKeyValueLines(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0)
            {
                separatorIndex = line.IndexOf('：');
            }

            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            map[key] = value;
        }

        return map;
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> map, string key)
    {
        if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        throw new InvalidOperationException($"缺少必填字段: {key}");
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> map, string key)
    {
        if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static int ParsePositiveInt(string input, string fieldName)
    {
        var digits = ExtractNumericToken(input);
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result <= 0)
        {
            throw new InvalidOperationException($"{fieldName}格式不正确: {input}");
        }

        return result;
    }

    private static int ParseMinutes(string input)
    {
        var normalized = input
            .Replace("分鐘", "分钟", StringComparison.Ordinal)
            .Replace("min", "分钟", StringComparison.OrdinalIgnoreCase)
            .Trim();

        var digits = ExtractNumericToken(normalized);
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            throw new InvalidOperationException($"时长格式不正确: {input}");
        }

        return minutes;
    }

    private static decimal ParseCostWan(string input)
    {
        var normalized = input
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();

        var number = ExtractDecimalToken(normalized);
        if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            throw new InvalidOperationException($"成本格式不正确: {input}");
        }

        if (normalized.Contains("万元", StringComparison.Ordinal) || normalized.Contains("万", StringComparison.Ordinal))
        {
            return amount;
        }

        if (normalized.Contains("元", StringComparison.Ordinal))
        {
            return decimal.Round(amount / 10000m, 0, MidpointRounding.AwayFromZero);
        }

        return amount;
    }

    private static decimal CalculateCostAmountWan(int totalMinutes)
    {
        var minutes = Math.Max(1, totalMinutes);
        var totalYuan = decimal.Round(minutes * CostPerMinuteYuan, 0, MidpointRounding.AwayFromZero);
        return decimal.Round(totalYuan / 10000m, 0, MidpointRounding.AwayFromZero);
    }

    private static string ExtractNumericToken(string input)
    {
        var chars = input.Where(char.IsDigit).ToArray();
        if (chars.Length == 0)
        {
            throw new InvalidOperationException($"未找到数字: {input}");
        }

        return new string(chars);
    }

    private static string ExtractDecimalToken(string input)
    {
        var chars = input.Where(c => char.IsDigit(c) || c == '.').ToArray();
        if (chars.Length == 0)
        {
            throw new InvalidOperationException($"未找到金额数字: {input}");
        }

        return new string(chars);
    }
}
