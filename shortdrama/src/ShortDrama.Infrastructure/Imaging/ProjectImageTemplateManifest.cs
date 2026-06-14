using System.Text.Json;

namespace ShortDrama.Infrastructure.Imaging;

public sealed record ProjectImageTemplateRegion(
    int X,
    int Y,
    int Width,
    int Height,
    string Note = "");

public sealed record ProjectImageTemplatePage(
    string File,
    IReadOnlyDictionary<string, ProjectImageTemplateRegion> Regions);

public sealed record ProjectImageTemplateManifest(
    string Id,
    string Name,
    int Count,
    IReadOnlyList<ProjectImageTemplatePage> Templates)
{
    public static ProjectImageTemplateManifest Load(string templateDirectory)
    {
        var manifestPath = Path.Combine(templateDirectory, "template.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"缺少工程图模板清单文件: {manifestPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var id = GetString(root, "id") ?? Path.GetFileName(templateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var name = GetString(root, "name") ?? id;
        var count = GetInt(root, "count") ?? 0;

        if (!root.TryGetProperty("templates", out var templatesElement) || templatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"工程图模板清单格式错误：templates 缺失或不是数组: {manifestPath}");
        }

        var pages = new List<ProjectImageTemplatePage>();
        foreach (var pageElement in templatesElement.EnumerateArray())
        {
            var file = GetString(pageElement, "file");
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            if (!pageElement.TryGetProperty("regions", out var regionsElement) || regionsElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"工程图模板缺少区域定义: {manifestPath}::{file}");
            }

            var regions = new Dictionary<string, ProjectImageTemplateRegion>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in regionsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                regions[property.Name] = new ProjectImageTemplateRegion(
                    X: GetInt(property.Value, "x") ?? 0,
                    Y: GetInt(property.Value, "y") ?? 0,
                    Width: GetInt(property.Value, "width") ?? 0,
                    Height: GetInt(property.Value, "height") ?? 0,
                    Note: GetString(property.Value, "note") ?? string.Empty);
            }

            pages.Add(new ProjectImageTemplatePage(file, regions));
        }

        var resolvedCount = count > 0 ? count : pages.Count;
        return new ProjectImageTemplateManifest(id, name, resolvedCount, pages);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString()?.Trim();
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number)
            ? number
            : null;
    }
}
