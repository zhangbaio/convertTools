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
            episodeCount = Math.Max(1, request.Drama.EpisodeTotal),
            posterUrl = request.Drama.PosterUrl?.Trim() ?? string.Empty,
            episodes,
            quality = "1080P+",
            concurrent = 3,
            workflowDirName,
            workflowProjectDir,
            createdAt = DateTimeOffset.Now.ToString("O")
        };

        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            cancellationToken);

        return new DramaProjectBootstrapResult(
            ProjectKey: projectKey,
            DisplayName: displayName,
            SourceProjectDir: sourceProjectDir,
            Created: created);
    }

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
}
