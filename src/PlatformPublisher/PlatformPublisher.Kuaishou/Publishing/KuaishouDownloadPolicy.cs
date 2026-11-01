using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手下载跳过规则：本地导入且已有视频时不下载；带 bookId 或待下载标记的远程项目必须下载。
/// </summary>
public static class KuaishouDownloadPolicy
{
    public const string RemoteDownloadPendingKey = "remoteDownloadPending";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    public static bool ShouldSkip(string projectDirectory)
    {
        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        var candidates = new[] { directories.SourceDirectory, directories.WorkflowDirectory, projectDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        if (candidates.Any(candidate => IndicatesRemoteDownload(ReadMetadata(candidate))))
            return false;
        if (candidates.Any(candidate => HasRemoteIdentity(ReadMetadata(candidate))) &&
            !candidates.Any(HasUsableVideos))
            return false;
        return candidates.Any(candidate => IsLocalImport(ReadMetadata(candidate)));
    }

    private static bool IndicatesRemoteDownload(JsonElement metadata) =>
        IsPending(metadata) || (HasBookId(metadata) && !IsLocalImport(metadata));

    private static bool HasRemoteIdentity(JsonElement metadata) =>
        IsPending(metadata) || HasBookId(metadata);

    private static bool IsLocalImport(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;
        if (ReadBool(metadata, "localImported") || ReadBool(metadata, "localManualImport") || ReadBool(metadata, "downloadDisabled"))
            return true;
        var importMode = ReadString(metadata, "importMode");
        if (string.Equals(importMode, "local", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(ReadString(metadata, "queueEntryDramaType"), "local_manual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPending(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object && ReadBool(metadata, RemoteDownloadPendingKey);

    private static bool HasBookId(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;
        return ReadString(metadata, "bookId").Length > 0 || ReadString(metadata, "book_id").Length > 0;
    }

    private static bool HasUsableVideos(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.'))
                    continue;
                if (Directory.Exists(entry))
                {
                    if (name.Equals("workflow", StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(entry);
                    continue;
                }

                if (VideoExtensions.Contains(Path.GetExtension(entry)))
                {
                    try
                    {
                        if (new FileInfo(entry).Length > 0)
                            return true;
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        return false;
    }

    private static JsonElement ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, "shortdrama-project.json");
        if (!File.Exists(path))
            return default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool ReadBool(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static string ReadString(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
    }
}
