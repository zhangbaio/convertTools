namespace ShortDrama.Infrastructure.Imaging;

public sealed record ProjectImageTemplateDescriptor(
    string Id,
    string Name,
    string TemplateDirectory,
    int Count);

public static class ProjectImageTemplateCatalog
{
    public static IReadOnlyList<string> DiscoverDefaultRoots(string? projectRoot = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(fullPath) || !ContainsTemplateManifest(fullPath) || !seen.Add(fullPath))
            {
                return;
            }

            results.Add(fullPath);
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            AddCandidate(Path.Combine(projectRoot, "templates", "project-image"));
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            AddCandidate(Path.Combine(current.FullName, "templates", "project-image"));
        }

        return results;
    }

    public static IReadOnlyList<ProjectImageTemplateDescriptor> Discover(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        var root = new DirectoryInfo(rootDirectory);
        var manifests = new List<string>();
        var directManifest = Path.Combine(root.FullName, "template.json");
        if (File.Exists(directManifest))
        {
            manifests.Add(directManifest);
        }

        manifests.AddRange(root.EnumerateDirectories()
            .Select(dir => Path.Combine(dir.FullName, "template.json"))
            .Where(File.Exists));

        var result = new List<ProjectImageTemplateDescriptor>();
        foreach (var manifestPath in manifests.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var templateDirectory = Path.GetDirectoryName(manifestPath)!;
                var manifest = ProjectImageTemplateManifest.Load(templateDirectory);
                result.Add(new ProjectImageTemplateDescriptor(
                    manifest.Id,
                    manifest.Name,
                    templateDirectory,
                    manifest.Count));
            }
            catch
            {
                // Ignore invalid template directories in the picker list; validation surfaces detail later.
            }
        }

        return result
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolveTemplateRoot(string templateRoot, string fallbackDirectory, string? projectRoot = null)
    {
        var resolved = TryResolveTemplateBase(templateRoot)
            ?? TryResolveTemplateBase(fallbackDirectory)
            ?? DiscoverDefaultRoots(projectRoot).FirstOrDefault();

        return resolved ?? string.Empty;
    }

    public static string ResolveTemplateDirectory(string templateRoot, string templateId, string fallbackDirectory, string? projectRoot = null)
    {
        var directDirectory = TryResolveTemplateDirectory(fallbackDirectory);
        if (!string.IsNullOrWhiteSpace(directDirectory))
        {
            return directDirectory;
        }

        foreach (var root in EnumerateResolutionRoots(templateRoot, projectRoot))
        {
            var resolved = TryResolveTemplateDirectoryFromRoot(root, templateId);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateResolutionRoots(string templateRoot, string? projectRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void YieldIfUnique(string? candidate, List<string> buffer)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                return;
            }

            buffer.Add(candidate);
        }

        var results = new List<string>();
        YieldIfUnique(TryResolveTemplateBase(templateRoot), results);
        foreach (var root in DiscoverDefaultRoots(projectRoot))
        {
            YieldIfUnique(root, results);
        }

        return results;
    }

    private static string? TryResolveTemplateDirectoryFromRoot(string templateRoot, string templateId)
    {
        var resolvedRoot = TryResolveTemplateBase(templateRoot);
        if (string.IsNullOrWhiteSpace(resolvedRoot))
        {
            return null;
        }

        var descriptors = Discover(resolvedRoot);
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return TryResolveTemplateDirectory(resolvedRoot)
                ?? descriptors.FirstOrDefault()?.TemplateDirectory;
        }

        return descriptors
            .FirstOrDefault(item => string.Equals(item.Id, templateId, StringComparison.OrdinalIgnoreCase))
            ?.TemplateDirectory;
    }

    private static string? TryResolveTemplateBase(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }

        if (File.Exists(fullPath) &&
            string.Equals(Path.GetFileName(fullPath), "template.json", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        return Directory.Exists(fullPath) && ContainsTemplateManifest(fullPath)
            ? fullPath
            : null;
    }

    private static string? TryResolveTemplateDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }

        if (File.Exists(fullPath) &&
            string.Equals(Path.GetFileName(fullPath), "template.json", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        return Directory.Exists(fullPath) && File.Exists(Path.Combine(fullPath, "template.json"))
            ? fullPath
            : null;
    }

    private static bool ContainsTemplateManifest(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(directory, "template.json")))
        {
            return true;
        }

        return Directory.EnumerateDirectories(directory)
            .Select(path => Path.Combine(path, "template.json"))
            .Any(File.Exists);
    }
}
