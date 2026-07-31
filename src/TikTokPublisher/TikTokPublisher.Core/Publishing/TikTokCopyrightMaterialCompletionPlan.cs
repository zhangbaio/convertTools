namespace TikTokPublisher.Core.Publishing;

/// <summary>
/// Describes which account-configured copyright materials are already present on
/// the TikTok copyright-proof page and which ones still need to be uploaded.
/// </summary>
public sealed record TikTokCopyrightMaterialCompletionPlan(
    IReadOnlyList<string> ConfiguredMaterialTypes,
    IReadOnlyList<string> ExistingMaterialTypes,
    IReadOnlyList<string> MissingMaterialTypes)
{
    public bool IsComplete => MissingMaterialTypes.Count == 0;

    public bool ShouldUpload(string materialType) =>
        MissingMaterialTypes.Contains(materialType, StringComparer.Ordinal);

    public static TikTokCopyrightMaterialCompletionPlan Create(
        IEnumerable<string>? configuredMaterialTypes,
        IEnumerable<string>? existingMaterialTypes)
    {
        var configured = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(configuredMaterialTypes)
            .ToArray();
        var configuredSet = configured.ToHashSet(StringComparer.Ordinal);
        var existing = (existingMaterialTypes ?? [])
            .Where(configuredSet.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var missing = configured
            .Where(materialType => !existingSet.Contains(materialType))
            .ToArray();

        return new TikTokCopyrightMaterialCompletionPlan(
            configured,
            existing,
            missing);
    }
}
