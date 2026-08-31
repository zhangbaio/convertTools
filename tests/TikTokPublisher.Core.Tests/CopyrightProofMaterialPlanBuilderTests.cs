using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class CopyrightProofMaterialPlanBuilderTests
{
    [Fact]
    public void Build_enables_every_generation_step_required_by_upload_configuration()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.ProductionAgreementMaterialType,
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                TikTokPublishConstants.SourceFileInformationMaterialType,
            ],
            TiktokUploadAiScriptOutlineWithScreenshots = true,
            TiktokUploadSourceInfoRoleVector = true,
            TiktokUploadSourceInfoRoleSceneScreenshot = true,
            TiktokQueueEnabledSteps =
            [
                QueueStepRegistry.GenerateEpisodeScript,
                QueueStepRegistry.GenerateRoleVector,
            ],
        };

        var plan = CopyrightProofMaterialPlanBuilder.Build(
            account,
            CopyrightProofExecutionMode.GenerateAndEdit);

        plan.RequiredSteps.Should().Equal(
            QueueStepRegistry.GenerateEpisodeScript,
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateRoleVector,
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.GenerateTimestampCertificate,
            QueueStepRegistry.UploadSeries);
        plan.SourceInfoSelection.Should().Be(
            new TikTokSourceFileInfoPackageSelection(true, true, true, true));
        plan.ArtifactDescriptions.Should().Contain(
        [
            "证明材料.pdf",
            "可信时间戳认证证书.pdf",
            "AI 生成过程截图 4 张",
            "AI剧本大纲.pdf",
            "剪辑工程图至少 4 张",
            "01_剧本与项目资料.png",
            "剧本.pdf",
            "角色矢量图.png",
            "02_角色场景或项目素材.png",
        ]);
    }

    [Fact]
    public void Build_explicit_ai_outline_checkbox_overrides_normal_queue_steps()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
            ],
            TiktokUploadAiScriptOutlineWithScreenshots = true,
            TiktokQueueEnabledSteps = [],
        };

        var plan = CopyrightProofMaterialPlanBuilder.Build(
            account,
            CopyrightProofExecutionMode.GenerateMaterialOnly);

        plan.RequiredSteps.Should().Equal(
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateProofMaterial);
        plan.RequiredSteps.Should().NotContain(QueueStepRegistry.UploadSeries);
        plan.SourceInfoSelection.IncludeOutline.Should().BeFalse();
        plan.ArtifactDescriptions.Should().Contain("AI剧本大纲.pdf");
    }

    [Fact]
    public void Build_source_information_uses_only_enabled_production_artifacts()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.SourceFileInformationMaterialType,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
            ],
            TiktokQueueEnabledSteps = [QueueStepRegistry.GenerateAiScriptOutline],
        };

        var plan = CopyrightProofMaterialPlanBuilder.Build(
            account,
            CopyrightProofExecutionMode.GenerateAndEdit);

        plan.SourceInfoSelection.Should().Be(
            new TikTokSourceFileInfoPackageSelection(true, false, false, false));
        plan.RequiredSteps.Should().Equal(
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.UploadSeries);
        plan.ArtifactDescriptions.Should().Contain("AI剧本大纲.pdf");
        plan.ArtifactDescriptions.Should().NotContain("剧本.pdf");
        plan.ArtifactDescriptions.Should().NotContain("角色矢量图.png");
    }

    [Fact]
    public void Build_explicit_role_vector_checkbox_adds_generation_dependencies()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.SourceFileInformationMaterialType,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
            ],
            TiktokUploadSourceInfoRoleVector = true,
            TiktokQueueEnabledSteps = [],
        };

        var plan = CopyrightProofMaterialPlanBuilder.Build(
            account,
            CopyrightProofExecutionMode.GenerateMaterialOnly);

        plan.SourceInfoSelection.IncludeRoleVector.Should().BeTrue();
        plan.RequiredSteps.Should().Equal(
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateRoleVector,
            QueueStepRegistry.GenerateProofMaterial);
        plan.ArtifactDescriptions.Should().Contain("角色矢量图.png");
    }
}
