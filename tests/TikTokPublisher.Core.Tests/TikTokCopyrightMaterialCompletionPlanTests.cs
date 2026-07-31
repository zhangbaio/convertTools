using FluentAssertions;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokCopyrightMaterialCompletionPlanTests
{
    [Fact]
    public void Create_keeps_existing_pdf_and_marks_missing_auxiliary_materials()
    {
        var plan = TikTokCopyrightMaterialCompletionPlan.Create(
            [
                TikTokPublishConstants.ProductionAgreementMaterialType,
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
            ],
            [TikTokPublishConstants.ProductionAgreementMaterialType]);

        plan.IsComplete.Should().BeFalse();
        plan.ExistingMaterialTypes.Should().Equal(
            TikTokPublishConstants.ProductionAgreementMaterialType);
        plan.MissingMaterialTypes.Should().Equal(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            TikTokPublishConstants.EditingProjectFilesMaterialType);
        plan.ShouldUpload(TikTokPublishConstants.ProductionAgreementMaterialType).Should().BeFalse();
        plan.ShouldUpload(TikTokPublishConstants.AiGenerationScreenshotsMaterialType).Should().BeTrue();
        plan.ShouldUpload(TikTokPublishConstants.EditingProjectFilesMaterialType).Should().BeTrue();
    }

    [Fact]
    public void Create_is_complete_only_when_every_configured_type_exists()
    {
        var configured = new[]
        {
            TikTokPublishConstants.ProductionAgreementMaterialType,
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            TikTokPublishConstants.EditingProjectFilesMaterialType,
        };

        var plan = TikTokCopyrightMaterialCompletionPlan.Create(
            configured,
            configured.Concat(["unconfigured_material"]));

        plan.IsComplete.Should().BeTrue();
        plan.MissingMaterialTypes.Should().BeEmpty();
        plan.ExistingMaterialTypes.Should().Equal(configured);
    }

    [Fact]
    public void Create_preserves_configured_order_and_ignores_unknown_existing_types()
    {
        var plan = TikTokCopyrightMaterialCompletionPlan.Create(
            [
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                TikTokPublishConstants.ProductionAgreementMaterialType,
            ],
            ["unknown", TikTokPublishConstants.ProductionAgreementMaterialType]);

        plan.ConfiguredMaterialTypes.Should().Equal(
            TikTokPublishConstants.EditingProjectFilesMaterialType,
            TikTokPublishConstants.ProductionAgreementMaterialType);
        plan.ExistingMaterialTypes.Should().Equal(
            TikTokPublishConstants.ProductionAgreementMaterialType);
        plan.MissingMaterialTypes.Should().Equal(
            TikTokPublishConstants.EditingProjectFilesMaterialType);
    }
}
