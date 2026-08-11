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
    IReadOnlyDictionary<string, IReadOnlyList<ProjectImageTemplateRegion>> Regions)
{
    public ProjectImageTemplateRegion? GetRegion(string key)
    {
        return GetRegions(key).FirstOrDefault();
    }

    public IReadOnlyList<ProjectImageTemplateRegion> GetRegions(string key)
    {
        return Regions.TryGetValue(key, out var regions)
            ? regions
            : Array.Empty<ProjectImageTemplateRegion>();
    }

    public bool HasRegion(string key)
    {
        return GetRegions(key).Count > 0;
    }
}

public sealed record ProjectImageTemplateManifest(
    string Id,
    string Name,
    int Count,
    IReadOnlyList<ProjectImageTemplatePage> Templates,
    bool RenderTimelineOverlay = false,
    long AssetVersion = 0,
    bool ExtractDialogue = true)
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
        var renderTimelineOverlay = GetBool(root, "render_timeline_overlay");
        var assetVersion = GetLong(root, "asset_version") ?? GetLong(root, "version") ?? 0;
        var extractDialogue = GetBool(root, "extract_dialogue", defaultValue: true);

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

            var regions = new Dictionary<string, IReadOnlyList<ProjectImageTemplateRegion>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in regionsElement.EnumerateObject())
            {
                var parsed = ParseRegions(property.Value);
                if (parsed.Count == 0)
                {
                    continue;
                }

                regions[property.Name] = parsed;
            }

            pages.Add(new ProjectImageTemplatePage(file, regions));
        }

        var resolvedCount = count > 0 ? count : pages.Count;
        return new ProjectImageTemplateManifest(
            id,
            name,
            resolvedCount,
            pages,
            renderTimelineOverlay,
            assetVersion,
            extractDialogue);
    }

    private static IReadOnlyList<ProjectImageTemplateRegion> ParseRegions(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var region = ParseRegion(element);
            return region is null ? Array.Empty<ProjectImageTemplateRegion>() : new[] { region };
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProjectImageTemplateRegion>();
        }

        var regions = new List<ProjectImageTemplateRegion>();
        foreach (var item in element.EnumerateArray())
        {
            var region = ParseRegion(item);
            if (region is not null)
            {
                regions.Add(region);
            }
        }

        return regions;
    }

    private static ProjectImageTemplateRegion? ParseRegion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var width = GetInt(element, "width") ?? 0;
        var height = GetInt(element, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new ProjectImageTemplateRegion(
            X: GetInt(element, "x") ?? 0,
            Y: GetInt(element, "y") ?? 0,
            Width: width,
            Height: height,
            Note: GetString(element, "note") ?? string.Empty);
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

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    private static bool GetBool(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => defaultValue
        };
    }
}
