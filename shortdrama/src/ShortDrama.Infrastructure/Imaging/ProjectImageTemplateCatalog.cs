namespace ShortDrama.Infrastructure.Imaging;

public sealed record ProjectImageTemplateDescriptor(
    string Id,
    string Name,
    string TemplateDirectory,
    int Count);

public static class ProjectImageTemplateCatalog
{
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

    public static string ResolveTemplateDirectory(string templateRoot, string templateId, string fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            return fallbackDirectory;
        }

        if (string.IsNullOrWhiteSpace(templateRoot))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            return templateRoot;
        }

        var descriptor = Discover(templateRoot)
            .FirstOrDefault(item => string.Equals(item.Id, templateId, StringComparison.OrdinalIgnoreCase));
        return descriptor?.TemplateDirectory ?? templateRoot;
    }
}
