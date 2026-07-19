using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueRunOptionsTests
{
    [Fact]
    public void FromDictionary_uses_default_steps_when_option_is_missing()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>());

        options.EnabledSteps.Should().BeEmpty();
    }

    [Fact]
    public void FromDictionary_preserves_empty_enabled_steps_when_option_exists()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?>()
        });

        options.EnabledSteps.Should().BeEmpty();
    }

    [Fact]
    public void Project_image_generation_is_not_user_selectable_or_enabled_by_default()
    {
        QueueStepRegistry.UserSelectable
            .Select(step => step.Key)
            .Should().NotContain(QueueStepRegistry.GenerateProjectImages);
        QueueStepRegistry.DefaultEnabledSteps
            .Should().NotContain(QueueStepRegistry.GenerateProjectImages);
    }

    [Fact]
    public void All_queue_steps_are_disabled_by_default()
    {
        QueueStepRegistry.DefaultEnabledSteps.Should().BeEmpty();
        new QueueRunOptions().EnabledSteps.Should().BeEmpty();
    }

    [Fact]
    public void Proof_material_generation_follows_project_images_and_is_selectable_but_disabled_by_default()
    {
        QueueStepRegistry.All
            .Select(step => step.Key)
            .Should().ContainInOrder(
                QueueStepRegistry.GenerateProjectImages,
                QueueStepRegistry.GenerateProofMaterial);
        QueueStepRegistry.UserSelectable
            .Select(step => step.Key)
            .Should().Contain(QueueStepRegistry.GenerateProofMaterial);
        QueueStepRegistry.DefaultEnabledSteps
            .Should().NotContain(QueueStepRegistry.GenerateProofMaterial);
        QueueStepRegistry.LabelOf(QueueStepRegistry.GenerateProofMaterial)
            .Should().Be("生成证明材料");
    }

    [Fact]
    public void NormalizeStepStates_backfills_legacy_uploaded_project_without_overwriting_explicit_pending_proof()
    {
        var legacyUploaded = new QueueProjectItem
        {
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
        var explicitlyReset = new QueueProjectItem
        {
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Pending,
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };

        legacyUploaded.NormalizeStepStates();
        explicitlyReset.NormalizeStepStates();

        legacyUploaded.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Completed);
        explicitlyReset.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Pending);
    }

    [Fact]
    public void Legacy_video_translate_option_migrates_to_the_complete_localization_chain()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?> { QueueStepKeys.LegacyVideoTranslate },
        });

        options.EnabledSteps.Should().ContainInOrder(
            QueueStepKeys.RemoveSubtitles,
            QueueStepKeys.DubVideo,
            QueueStepKeys.AddSubtitles);
    }

    [Theory]
    [InlineData(QueueStepStatus.Completed, QueueStepStatus.Completed, QueueStepStatus.Completed, QueueStepStatus.Completed)]
    [InlineData(QueueStepStatus.Failed, QueueStepStatus.Failed, QueueStepStatus.Pending, QueueStepStatus.Pending)]
    public void Legacy_video_translate_state_migrates_without_skipping_prerequisites(
        string legacyStatus, string removeStatus, string dubStatus, string addStatus)
    {
        var item = new QueueProjectItem
        {
            CurrentStep = QueueStepKeys.LegacyVideoTranslate,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.LegacyVideoTranslate] = legacyStatus,
            },
        };

        item.NormalizeStepStates();

        item.CurrentStep.Should().Be(QueueStepKeys.RemoveSubtitles);
        item.StepStates.Should().NotContainKey(QueueStepKeys.LegacyVideoTranslate);
        item.StepStates[QueueStepKeys.RemoveSubtitles].Should().Be(removeStatus);
        item.StepStates[QueueStepKeys.DubVideo].Should().Be(dubStatus);
        item.StepStates[QueueStepKeys.AddSubtitles].Should().Be(addStatus);
    }
}
