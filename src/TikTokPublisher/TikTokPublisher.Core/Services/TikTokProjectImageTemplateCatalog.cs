using ShortDrama.Infrastructure.Imaging;
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
                new("image-template-project-image-11", "图片模板工程图11"),
                new("image-template-project-image-13", "图片模板工程图13"),
            });

    private static readonly IReadOnlyDictionary<string, TikTokProjectImageTemplateOption> OptionsById =
        Options.ToDictionary(option => option.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TikTokProjectImageTemplateOption> BuiltInOptions => Options;

    public static bool TryGet(string? templateId, out TikTokProjectImageTemplateOption option)
    {
        var normalizedId = (templateId ?? string.Empty).Trim().Replace('_', '-');
        if (OptionsById.TryGetValue(normalizedId, out var resolved))
        {
            option = resolved;
            return true;
        }

        option = null!;
        return false;
    }

    public static string RuntimeId(string? templateId)
    {
        if (TryGet(templateId, out var option))
            return option.Id;

        return string.IsNullOrWhiteSpace(templateId) || ProjectImageTemplateCatalog.IsRetiredBuiltInId(templateId)
            ? ClientSettingsDefaults.TiktokProjectImageTemplateId
            : templateId.Trim();
    }

    public static string NormalizeId(string? templateId) =>
        TryGet(templateId, out var option)
            ? option.Id
            : ClientSettingsDefaults.TiktokProjectImageTemplateId;

    public static string ResolveName(string? templateId) =>
        OptionsById[NormalizeId(templateId)].Name;
}
