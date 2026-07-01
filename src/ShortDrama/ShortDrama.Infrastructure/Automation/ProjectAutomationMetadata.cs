using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

internal sealed record ProjectAutomationMetadata(
    string? ProjectKey,
    string? SourceName,
    string? DisplayName,
    string? Title,
    DateTimeOffset? CreatedAt,
    string? Intro,
    string? BookId,
    string? PosterUrl,
    string Episodes,
    string Quality,
    int Concurrent,
    string? UploadConfigName,
    string? WorkflowDirName,
    string? WorkflowProjectDir)
{
    public static ProjectAutomationMetadata Resolve(string projectDir)
    {
        var defaults = new ProjectAutomationMetadata(
            ProjectKey: null,
            SourceName: null,
            DisplayName: null,
            Title: null,
            CreatedAt: null,
            Intro: null,
            BookId: null,
            PosterUrl: null,
            Episodes: "all",
            Quality: "1080P+",
            Concurrent: 3,
            UploadConfigName: null,
            WorkflowDirName: null,
            WorkflowProjectDir: null);

        foreach (var candidate in EnumerateMetadataFiles(projectDir))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                if (string.Equals(Path.GetFileName(candidate), "book_id.txt", StringComparison.OrdinalIgnoreCase))
                {
                    var bookId = File.ReadAllText(candidate).Trim();
                    if (!string.IsNullOrWhiteSpace(bookId))
                    {
                        return defaults with { BookId = bookId };
                    }
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(candidate), "pipeline-config.json", StringComparison.OrdinalIgnoreCase))
                {
                    var stepArgs = TryGetProperty(root, "steps", "download", "args");
                    if (stepArgs is null || stepArgs.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    return defaults with
                    {
                        BookId = GetString(stepArgs.Value, "book_id"),
                        PosterUrl = GetString(stepArgs.Value, "poster_url"),
                        Episodes = GetString(stepArgs.Value, "episodes") ?? defaults.Episodes,
                        Quality = GetString(stepArgs.Value, "quality") ??
                            GetString(stepArgs.Value, "level") ??
                            defaults.Quality,
                        Concurrent = GetInt(stepArgs.Value, "concurrent") ?? defaults.Concurrent
                    };
                }

                return defaults with
                {
                    ProjectKey = GetString(root, "projectKey") ?? GetString(root, "project_key"),
                    SourceName = GetString(root, "sourceName") ?? GetString(root, "source_name"),
                    DisplayName = GetString(root, "displayName") ?? GetString(root, "display_name"),
                    Title = GetString(root, "title") ?? GetString(root, "originalTitle") ?? GetString(root, "name"),
                    CreatedAt = GetDateTimeOffset(root, "createdAt") ?? GetDateTimeOffset(root, "created_at"),
                    Intro = GetString(root, "intro") ?? GetString(root, "description") ?? GetString(root, "desc"),
                    BookId = GetString(root, "bookId") ?? GetString(root, "book_id"),
                    PosterUrl = GetString(root, "posterUrl") ?? GetString(root, "poster_url"),
                    Episodes = GetString(root, "episodes") ?? defaults.Episodes,
                    Quality = GetString(root, "quality") ?? defaults.Quality,
                    Concurrent = GetInt(root, "concurrent") ?? defaults.Concurrent,
                    UploadConfigName = GetString(root, "uploadConfigName") ?? GetString(root, "upload_config_name"),
                    WorkflowDirName = GetString(root, "workflowDirName") ?? GetString(root, "workflow_dir_name"),
                    WorkflowProjectDir = GetString(root, "workflowProjectDir") ?? GetString(root, "workflow_project_dir")
                };
            }
            catch
            {
                // Ignore invalid metadata files and continue.
            }
        }

        return defaults;
    }

    private static IEnumerable<string> EnumerateMetadataFiles(string projectDir)
    {
        yield return Path.Combine(projectDir, "shortdrama-project.json");
        yield return Path.Combine(projectDir, "pipeline-config.json");
        yield return Path.Combine(projectDir, "book_id.txt");
    }

    private static JsonElement? TryGetProperty(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (!current.TryGetProperty(name, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
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

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }
}
