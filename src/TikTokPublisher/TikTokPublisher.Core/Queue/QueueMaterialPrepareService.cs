namespace TikTokPublisher.Core.Queue;

/// <summary>对齐 Python <c>material_prepare_service.prepare_project_material_inputs</c>，只准备真实图片素材。</summary>
public static class QueueMaterialPrepareService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif",
    };

    public static Task<string?> PrepareMaterialInputsAsync(
        string projectDir,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(projectDir);
        var workflowDir = ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        var episodeCount = ProjectWorkspaceService.ResolveSourceEpisodeCount(projectDir);
        ProjectWorkspaceService.EnsureWorkflowInfo(projectDir, episodeCount, log);

        var posterPath = EnsureSourcePoster(context.SourceProjectDir, log, ct);
        if (posterPath is not null)
        {
            ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
            log?.Invoke($"素材封面已就绪：{Path.GetFileName(posterPath)}");
        }

        return Task.FromResult(posterPath ?? ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir));
    }

    private static string? EnsureSourcePoster(
        string sourceProjectDir,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var image = FindFirstImage(sourceProjectDir, Path.GetFileName(sourceProjectDir));
        if (image is not null)
        {
            if (IsPosterAlias(image))
                return image;

            var target = Path.Combine(sourceProjectDir, $"海报图片{Path.GetExtension(image)}");
            if (!File.Exists(target))
            {
                File.Copy(image, target, overwrite: false);
                log?.Invoke($"已生成项目海报别名：{Path.GetFileName(target)}");
            }

            return target;
        }

        return FindPosterAlias(sourceProjectDir);
    }

    private static string? FindPosterAlias(string projectDir)
    {
        foreach (var ext in ImageExtensions)
        {
            var path = Path.Combine(projectDir, $"海报图片{ext}");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static bool IsPosterAlias(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "海报图片", StringComparison.Ordinal);

    private static string? FindFirstImage(string projectDir, string preferredStem)
    {
        if (!Directory.Exists(projectDir)) return null;

        var candidates = Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return !stem.StartsWith("工程图_", StringComparison.Ordinal) &&
                       !stem.StartsWith("成本报表", StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nonAliasCandidates = candidates
            .Where(path => !IsPosterAlias(path))
            .ToList();

        return nonAliasCandidates.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == preferredStem)
               ?? nonAliasCandidates.FirstOrDefault()
               ?? candidates.FirstOrDefault();
    }
}
