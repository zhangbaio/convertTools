using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaProjectBootstrapper : IDramaProjectBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public async Task<DramaProjectBootstrapResult> BootstrapAsync(
        DramaProjectBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RootDir))
        {
            throw new InvalidOperationException("项目根目录不能为空。");
        }

        if (!Directory.Exists(request.RootDir))
        {
            throw new DirectoryNotFoundException($"项目根目录不存在: {request.RootDir}");
        }

        if (string.IsNullOrWhiteSpace(request.Drama.BookId))
        {
            throw new InvalidOperationException("短剧缺少 book_id。");
        }

        var displayName = string.IsNullOrWhiteSpace(request.Drama.Title)
            ? request.Drama.BookId.Trim()
            : request.Drama.Title.Trim();
        var projectKey = ResolveProjectKey(request.RootDir, displayName, request.Drama.BookId);
        var sourceProjectDir = Path.Combine(request.RootDir, projectKey);
        var workflowDirName = $"_{projectKey}";
        var workflowProjectDir = Path.Combine(request.RootDir, "workflow", workflowDirName);
        var created = !Directory.Exists(sourceProjectDir);

        Directory.CreateDirectory(sourceProjectDir);

        var metadataPath = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        var category = request.Drama.Category?.Trim() ?? string.Empty;

        var episodes = string.IsNullOrWhiteSpace(request.Episodes)
            ? "all"
            : request.Episodes.Trim();

        var quality = string.IsNullOrWhiteSpace(request.Quality)
            ? "1080P"
            : request.Quality.Trim();
        var concurrent = Math.Clamp(request.Concurrent, 1, 10);
        var episodeNumberMode = NormalizeEpisodeNumberMode(request.EpisodeNumberMode);
        var metadata = new
        {
            projectKey,
            sourceName = request.Drama.Title.Trim(),
            displayName,
            bookId = request.Drama.BookId.Trim(),
            title = displayName,
            originalTitle = request.Drama.Title.Trim(),
            intro = request.Drama.Intro?.Trim() ?? string.Empty,
            category,
            episodeCount = Math.Max(0, request.Drama.EpisodeTotal),
            favoriteCount = Math.Max(0, request.Drama.FavoriteCount),
            posterUrl = request.Drama.PosterUrl?.Trim() ?? string.Empty,
            configDir = string.Empty,
            episodes,
            quality,
            concurrent,
            episodeNumberMode,
            workflowDirName,
            workflowProjectDir,
            sourceProjectDir,
            queueEntryDramaType = request.QueueEntryDramaType?.Trim() ?? string.Empty,
            queue_entry_drama_type = request.QueueEntryDramaType?.Trim() ?? string.Empty,
            createdAt = DateTimeOffset.Now.ToString("O")
        };

        var metadataNode = JsonSerializer.SerializeToNode(metadata, JsonOptions)!.AsObject();
        if (!created)
        {
            MergeExistingMetadata(metadataPath, metadataNode);
        }

        await File.WriteAllTextAsync(
            metadataPath,
            metadataNode.ToJsonString(JsonOptions),
            cancellationToken);

        return new DramaProjectBootstrapResult(
            ProjectKey: projectKey,
            DisplayName: displayName,
            SourceProjectDir: sourceProjectDir,
            Created: created);
    }

    private static void MergeExistingMetadata(string metadataPath, JsonObject refreshed)
    {
        JsonObject? existing;
        try
        {
            existing = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
        }
        catch
        {
            existing = null;
        }

        if (existing is null)
        {
            return;
        }

        // Re-adding an existing downloaded drama refreshes its source/download data, but it
        // must not detach a renamed workflow or discard the generated title stored in metadata.
        // Start with the existing document so fields owned by later workflow steps survive.
        var refreshedProperties = refreshed.ToArray();
        refreshed.Clear();
        foreach (var (key, value) in existing)
        {
            refreshed[key] = value?.DeepClone();
        }

        foreach (var (key, value) in refreshedProperties)
        {
            if ((key is "workflowDirName" or "workflowProjectDir" or "createdAt") &&
                HasNonEmptyValue(existing, key))
            {
                continue;
            }

            refreshed[key] = value?.DeepClone();
        }
    }

    private static bool HasNonEmptyValue(JsonObject metadata, string key) =>
        metadata[key] is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text);

    private static string ResolveProjectKey(string rootDir, string title, string bookId)
    {
        var sanitizedTitle = SanitizeDirectoryName(title);
        var titleDir = Path.Combine(rootDir, sanitizedTitle);
        if (!Directory.Exists(titleDir))
        {
            return sanitizedTitle;
        }

        var metadataBookId = TryReadBookId(titleDir);
        if (string.IsNullOrWhiteSpace(metadataBookId) ||
            string.Equals(metadataBookId, bookId, StringComparison.Ordinal))
        {
            return sanitizedTitle;
        }

        return $"{sanitizedTitle}_{bookId.Trim()}";
    }

    private static string SanitizeDirectoryName(string title)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedTitle = new string(title.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .Trim('.')
            .Replace('/', '_')
            .Replace('\\', '_');

        return string.IsNullOrWhiteSpace(sanitizedTitle)
            ? "drama"
            : sanitizedTitle;
    }

    private static string? TryReadBookId(string projectDir)
    {
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(metadataPath));
            return node?["bookId"]?.GetValue<string>() ?? node?["book_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeEpisodeNumberMode(string? value)
    {
        return string.Equals(value?.Trim(), "continuous", StringComparison.OrdinalIgnoreCase)
            ? "continuous"
            : "source";
    }
}
