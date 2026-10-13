using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 超长剧入队：开关打开时，下载和本地导入都把超过设定集数的剧截成前 N 集。
/// </summary>
public static class OverLimitEpisodePolicy
{
    public static int ResolveKeepCount(ClientSettings settings)
    {
        var configured = settings.TiktokOverLimitDownloadEpisodeCount <= 0
            ? ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount
            : settings.TiktokOverLimitDownloadEpisodeCount;
        return Math.Clamp(configured, 1, ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
    }

    public static bool ShouldTruncate(int episodeCount, ClientSettings? settings)
    {
        if (settings is null || !settings.TiktokAllowOverLimitUploadImport || episodeCount <= 0)
            return false;
        return episodeCount > ResolveKeepCount(settings);
    }

    public static string ResolveDownloadSelection(string? selection, int episodeTotal, ClientSettings? settings)
    {
        var text = string.IsNullOrWhiteSpace(selection) ? "all" : selection.Trim();
        if (!ShouldTruncate(episodeTotal, settings))
            return text;

        var keep = ResolveKeepCount(settings!);
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || SelectionExceeds(text, keep))
            return $"1-{keep}";
        return text;
    }

    public static bool TryReadOriginalEpisodeCount(string projectDir, out int originalEpisodeCount)
    {
        originalEpisodeCount = 0;
        var path = MetadataPath(projectDir);
        if (!File.Exists(path))
            return false;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
                return false;
            foreach (var key in new[] { "originalEpisodeCount", "original_episode_count" })
            {
                if (root[key] is JsonValue value && value.TryGetValue<int>(out var count) && count > 0)
                {
                    originalEpisodeCount = count;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool TryReadKeepCount(string projectDir, out int keepCount)
    {
        keepCount = 0;
        var path = MetadataPath(projectDir);
        if (!File.Exists(path))
            return false;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null || !ReadBool(root, "truncatedForTikTokUpload") && !ReadBool(root, "truncated_for_tiktok_upload"))
                return false;
            foreach (var key in new[] { "downloadEpisodeLimit", "download_episode_limit", "effectiveEpisodeCount", "effective_episode_count" })
            {
                if (root[key] is JsonValue value && value.TryGetValue<int>(out var count) && count > 0)
                {
                    keepCount = count;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static void MarkTruncated(string projectDir, int originalEpisodeCount, ClientSettings settings, Action<string>? log = null)
    {
        var keep = ResolveKeepCount(settings);
        if (originalEpisodeCount <= keep)
            return;

        var source = ResolveSourceDir(projectDir);
        var path = Path.Combine(source, "shortdrama-project.json");
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        root["originalEpisodeCount"] = originalEpisodeCount;
        root["original_episode_count"] = originalEpisodeCount;
        root["episodeCount"] = keep;
        root["episode_count"] = keep;
        root["effectiveEpisodeCount"] = keep;
        root["effective_episode_count"] = keep;
        root["downloadEpisodeLimit"] = keep;
        root["download_episode_limit"] = keep;
        root["episodes"] = $"1-{keep}";
        root["truncatedForTikTokUpload"] = true;
        root["truncated_for_tiktok_upload"] = true;
        Directory.CreateDirectory(source);
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        foreach (var infoDir in new[] { source, ProjectWorkspaceService.ResolveWorkflowProjectDir(source) })
        {
            if (string.IsNullOrWhiteSpace(infoDir))
                continue;
            var infoPath = Path.Combine(infoDir, "短剧信息.txt");
            if (File.Exists(infoPath))
                ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "集数", keep.ToString());
        }

        log?.Invoke($"超长剧只使用前 {keep} 集：原 {originalEpisodeCount} 集");
    }

    public static void EnsureTruncated(string projectDir, ClientSettings? settings = null, Action<string>? log = null)
    {
        settings ??= ClientSettingsStore.Load();
        if (!settings.TiktokAllowOverLimitUploadImport)
            return;
        if (TryReadKeepCount(projectDir, out _))
            return;

        var source = ResolveSourceDir(projectDir);
        var fileCount = ProjectVideoResolver.ResolveSourceVideos(source, allowStagedFallback: false, applyOverLimitSlice: false).Count;
        var declared = ReadDeclaredEpisodeCount(source);
        var original = Math.Max(fileCount, declared);
        if (!ShouldTruncate(original, settings))
            return;

        MarkTruncated(source, original, settings, log);
    }

    private static bool SelectionExceeds(string selection, int keep)
    {
        foreach (var part in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var pieces = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (pieces.Length == 2 &&
                    int.TryParse(pieces[0], out var start) &&
                    int.TryParse(pieces[1], out var end) &&
                    (start > keep || end > keep))
                    return true;
            }
            else if (int.TryParse(part, out var episode) && episode > keep)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadDeclaredEpisodeCount(string sourceDir)
    {
        var path = Path.Combine(sourceDir, "shortdrama-project.json");
        if (!File.Exists(path))
            return 0;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["episodeCount"] is JsonValue value && value.TryGetValue<int>(out var count))
                return count;
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private static string ResolveSourceDir(string projectDir)
    {
        try
        {
            var source = ProjectWorkspaceService.LoadContext(projectDir).SourceProjectDir;
            return string.IsNullOrWhiteSpace(source) ? Path.GetFullPath(projectDir) : source;
        }
        catch
        {
            return Path.GetFullPath(projectDir);
        }
    }

    private static string MetadataPath(string projectDir) =>
        Path.Combine(ResolveSourceDir(projectDir), "shortdrama-project.json");

    private static bool ReadBool(JsonObject root, string name)
    {
        if (root[name] is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag) && flag;
    }
}
