using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokProjectImageTemplateOption(string Id, string Name)
{
    public string SelectionLabel => $"{Name}（{Id}）";
}

/// <summary>系统设置、工作流配置与工程图服务共用的内置截图模板目录。</summary>
public static class TikTokProjectImageTemplateCatalog
{
    private static readonly IReadOnlyList<TikTokProjectImageTemplateOption> Options =
        Array.AsReadOnly(
            new TikTokProjectImageTemplateOption[]
            {
                new(
                    ClientSettingsDefaults.TiktokProjectImageTemplateId,
                    ClientSettingsDefaults.TiktokProjectImageTemplateName),
                new("image-template-project-image-4", "图片模板工程图4"),
                new("image-template-project-image-5", "图片模板工程图5"),
                new("image-template-project-image-6", "图片模板工程图6"),
                new("image-template-project-image-7", "图片模板工程图7"),
                new("image-template-project-image-8", "图片模板工程图8"),
                new("image-template-project-image-9", "图片模板工程图9"),
                new("image-template-project-image-10", "图片模板工程图10"),
                new("image-template-project-image-11", "图片模板工程图11"),
            });

    private static readonly IReadOnlyDictionary<string, TikTokProjectImageTemplateOption> OptionsById =
        Options.ToDictionary(option => option.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TikTokProjectImageTemplateOption> BuiltInOptions => Options;

    public static bool TryGet(string? templateId, out TikTokProjectImageTemplateOption option)
    {
        var normalizedId = (templateId ?? string.Empty).Trim();
        if (OptionsById.TryGetValue(normalizedId, out var resolved))
        {
            option = resolved;
            return true;
        }

        option = null!;
        return false;
    }

    public static string ResolveName(string? templateId)
    {
        var normalizedId = (templateId ?? string.Empty).Trim();
        if (normalizedId.Length == 0)
            normalizedId = ClientSettingsDefaults.TiktokProjectImageTemplateId;

        return TryGet(normalizedId, out var option)
            ? option.Name
            : normalizedId;
    }

    public static string CreateSelectionLabel(string? templateId)
    {
        var normalizedId = (templateId ?? string.Empty).Trim();
        if (normalizedId.Length == 0)
            normalizedId = ClientSettingsDefaults.TiktokProjectImageTemplateId;

        return TryGet(normalizedId, out var option)
            ? option.SelectionLabel
            : $"未内置模板（保留原值）：{normalizedId}";
    }
}
