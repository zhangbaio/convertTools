using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record CopyrightProofMaterialPlan(
    IReadOnlyList<string> MaterialTypes,
    IReadOnlyList<string> RequiredSteps,
    TikTokSourceFileInfoPackageSelection SourceInfoSelection,
    IReadOnlyList<string> ArtifactDescriptions)
{
    public IReadOnlyList<string> GenerationSteps => RequiredSteps
        .Where(step => !string.Equals(step, QueueStepRegistry.UploadSeries, StringComparison.Ordinal))
        .ToArray();

    public bool HasAdditionalGenerationSteps => GenerationSteps.Any(step =>
        !string.Equals(step, QueueStepRegistry.GenerateProofMaterial, StringComparison.Ordinal));

    public string DescribeArtifacts() => ArtifactDescriptions.Count == 0
        ? "账号未配置可自动生成的版权材料。"
        : "将按账号“上传材料”配置生成或复用：" +
          string.Join("、", ArtifactDescriptions) + "。";
}

/// <summary>
/// Converts the account-level copyright upload configuration into the exact queue
/// steps and upload-package selection used by copyright-proof recovery runs.
/// </summary>
public static class CopyrightProofMaterialPlanBuilder
{
    public static CopyrightProofMaterialPlan Build(
        TikTokAccountProfile account,
        CopyrightProofExecutionMode executionMode)
    {
        ArgumentNullException.ThrowIfNull(account);

        var materialTypes = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(account.TiktokCopyrightMaterialTypes)
            .ToArray();
        var selected = materialTypes.ToHashSet(StringComparer.Ordinal);
        var steps = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new List<string>();

        var includeProductionAgreement = selected.Contains(
            TikTokPublishConstants.ProductionAgreementMaterialType);
        var includeFilingLicense = selected.Contains(
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType);
        var includeAiScreenshots = selected.Contains(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
        var includeEditingProject = selected.Contains(
            TikTokPublishConstants.EditingProjectFilesMaterialType);
        var includeSourceInfo = selected.Contains(
            TikTokPublishConstants.SourceFileInformationMaterialType);

        // TikTok requires at least four files in the source-information field. Once this
        // material is selected, the outline and script become required package components
        // even when the ordinary production queue did not explicitly enable those steps.
        var includeOutline = includeSourceInfo;
        var includeScript = includeSourceInfo;
        var includeRoleVector = includeSourceInfo &&
                                account.TiktokUploadSourceInfoRoleVector;
        var includeRoleScene = includeSourceInfo &&
                               account.TiktokUploadSourceInfoRoleSceneScreenshot;

        // The explicit AI-outline upload checkbox is authoritative even when the
        // normal production queue does not enable the outline step.
        if (includeAiScreenshots && account.TiktokUploadAiScriptOutlineWithScreenshots)
            includeOutline = true;

        if (includeSourceInfo)
        {
            var normalized = TikTokSourceFileInfoPackageSelection.WithPlatformMinimum(new(
                includeOutline,
                includeScript,
                includeRoleVector,
                includeRoleScene));
            includeOutline = normalized.IncludeOutline;
            includeScript = normalized.IncludeScript;
            includeRoleVector = normalized.IncludeRoleVector;
            includeRoleScene = normalized.IncludeRoleSceneScreenshot;
        }

        if (includeOutline)
            steps.Add(QueueStepRegistry.GenerateAiScriptOutline);
        if (includeScript)
            steps.Add(QueueStepRegistry.GenerateEpisodeScript);
        if (includeRoleVector)
        {
            // The explicit upload checkbox is authoritative. Role-vector generation
            // consumes the AI drama material package, so recovery runs add both steps.
            steps.Add(QueueStepRegistry.GenerateAiDramaMaterials);
            steps.Add(QueueStepRegistry.GenerateRoleVector);
        }

        if (includeProductionAgreement || includeAiScreenshots ||
            includeEditingProject || includeSourceInfo)
        {
            steps.Add(QueueStepRegistry.GenerateProofMaterial);
        }

        if (includeFilingLicense)
            steps.Add(QueueStepRegistry.GenerateTimestampCertificate);

        if (executionMode == CopyrightProofExecutionMode.GenerateAndEdit)
            steps.Add(QueueStepRegistry.UploadSeries);

        if (includeProductionAgreement)
            artifacts.Add("证明材料.pdf");
        if (includeFilingLicense)
            artifacts.Add("可信时间戳认证证书.pdf");
        if (includeAiScreenshots)
            artifacts.Add("AI 生成过程截图 4 张");
        if (includeOutline)
            artifacts.Add("AI剧本大纲.pdf");
        if (includeEditingProject)
            artifacts.Add("剪辑工程图至少 4 张");
        if (includeSourceInfo)
        {
            artifacts.Add("01_剧本与项目资料.png");
            if (includeScript) artifacts.Add("剧本.pdf");
            if (includeRoleVector) artifacts.Add("角色矢量图.png");
            if (includeRoleScene) artifacts.Add("02_角色场景或项目素材.png");
        }

        var sourceInfoSelection = new TikTokSourceFileInfoPackageSelection(
            includeOutline && includeSourceInfo,
            includeScript,
            includeRoleVector,
            includeRoleScene);
        var requiredSteps = QueueStepRegistry.OrderEnabledSteps(steps).ToArray();
        return new CopyrightProofMaterialPlan(
            materialTypes,
            requiredSteps,
            sourceInfoSelection,
            artifacts.Distinct(StringComparer.Ordinal).ToArray());
    }
}
