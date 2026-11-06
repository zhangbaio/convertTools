using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouFormOption(string Label, int Value);

/// <summary>快手分账表单的观众、短剧分类和情节选项，与官方目录一致。</summary>
public static class KuaishouFormCatalog
{
    public static IReadOnlyList<KuaishouFormOption> Audience { get; }
    public static IReadOnlyList<KuaishouFormOption> Categories { get; }
    public static IReadOnlyList<KuaishouFormOption> Plots { get; }

    static KuaishouFormCatalog()
    {
        var name = typeof(KuaishouFormCatalog).Assembly.GetManifestResourceNames()
            .First(item => item.EndsWith("series_form_options.json", StringComparison.OrdinalIgnoreCase));
        using var stream = typeof(KuaishouFormCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少快手表单选项。");
        using var document = JsonDocument.Parse(stream);
        var fields = document.RootElement.GetProperty("fields");
        Audience = Read(fields, "audience_gender");
        Categories = Read(fields, "mini_series_tag_id_list");
        Plots = Read(fields, "plot_list");
    }

    public static bool TryMatch(IReadOnlyList<KuaishouFormOption> options, string? text, out KuaishouFormOption? option)
    {
        var value = (text ?? "").Trim();
        option = options.FirstOrDefault(item =>
            item.Label.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            item.Value.ToString() == value);
        return option is not null;
    }

    private static IReadOnlyList<KuaishouFormOption> Read(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var field) ||
            !field.TryGetProperty("options", out var options))
            return [];
        var result = new List<KuaishouFormOption>();
        foreach (var item in options.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var labelValue) ? labelValue.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(label) || !item.TryGetProperty("value", out var value) || !value.TryGetInt32(out var id))
                continue;
            result.Add(new KuaishouFormOption(label, id));
        }
        return result;
    }
}
