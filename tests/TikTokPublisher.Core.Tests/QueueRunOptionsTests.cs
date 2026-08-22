using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueRunOptionsTests
{
    [Fact]
    public void NormalizeStepStates_RecoversStepsSkippedByRemovedLiveActionDetection()
    {
        var item = new QueueProjectItem
        {
            StatusText = "真人剧已拦截",
            CurrentStep = "detect_live_action",
            LastError = "检测为真人剧",
            StepStates = new Dictionary<string, string>
            {
                ["detect_live_action"] = "真人剧已拦截",
                [QueueStepKeys.Download] = QueueStepStatus.Completed,
                [QueueStepKeys.RewriteInfo] = QueueStepStatus.Skipped,
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Skipped,
            },
        };

        item.NormalizeStepStates();

        item.StepStates.Should().NotContainKey("detect_live_action");
        item.StepStates[QueueStepKeys.Download].Should().Be(QueueStepStatus.Completed);
        item.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Pending);
        item.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Pending);
        item.StatusText.Should().Be(QueueStepStatus.Pending);
        item.CurrentStep.Should().BeEmpty();
        item.LastError.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeStepStates_DoesNotResetUnrelatedSkippedSteps()
    {
        var item = new QueueProjectItem
        {
            StatusText = QueueStepStatus.Pending,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.RewriteInfo] = QueueStepStatus.Skipped,
            },
        };

        item.NormalizeStepStates();

        item.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Skipped);
    }

    [Fact]
    public void CopyrightProofOnlyEntryMode_RoundTripsButIsNotPersisted()
    {
        var options = new QueueRunOptions
        {
            UploadEntryMode = QueueRunOptions.CopyrightProofOnlyEntryMode,
        };

        var transient = QueueRunOptions.FromDictionary(options.ToDictionary());
        var persistent = QueueRunOptions.FromDictionary(options.ToPersistentDictionary());

        Assert.True(transient.IsCopyrightProofOnlyRun());
        Assert.Equal(string.Empty, persistent.UploadEntryMode);
    }

    [Fact]
    public void CopyrightProofMaterialOnlyEntryMode_RoundTripsButIsNotPersisted()
    {
        var options = new QueueRunOptions
        {
            UploadEntryMode = QueueRunOptions.CopyrightProofMaterialOnlyEntryMode,
        };

        var transient = QueueRunOptions.FromDictionary(options.ToDictionary());
        var persistent = QueueRunOptions.FromDictionary(options.ToPersistentDictionary());

        Assert.True(transient.IsCopyrightProofMaterialOnlyRun());
        Assert.True(transient.IsCopyrightProofWorkflowRun());
        Assert.Equal(string.Empty, persistent.UploadEntryMode);
    }

    [Fact]
    public void ConfigureForCopyrightProofCompletion_reuses_completed_proof_but_still_runs_edit()
    {
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepRegistry.Download],
            ForceRerunCompletedSteps = true,
            AutoArchiveAfterUpload = true,
            SyncManagementAfterUpload = true,
        };

        options.ConfigureForCopyrightProofCompletion();

        options.EnabledSteps.Should().Equal(
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.UploadSeries);
        options.ForceRerunCompletedSteps.Should().BeFalse();
        options.AutoArchiveAfterUpload.Should().BeFalse();
        options.SyncManagementAfterUpload.Should().BeFalse();
        options.IsCopyrightProofOnlyRun().Should().BeTrue();
    }

    [Fact]
    public void ConfigureForCopyrightProof_generate_only_never_enables_TikTok_edit()
    {
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepRegistry.UploadSeries],
            ForceRerunCompletedSteps = true,
            AutoArchiveAfterUpload = true,
            SyncManagementAfterUpload = true,
            UploadEntryMode = QueueRunOptions.CopyrightProofOnlyEntryMode,
        };

        options.ConfigureForCopyrightProof(
            CopyrightProofExecutionMode.GenerateMaterialOnly);

        options.EnabledSteps.Should().Equal(QueueStepRegistry.GenerateProofMaterial);
        options.ForceRerunCompletedSteps.Should().BeFalse();
        options.AutoArchiveAfterUpload.Should().BeFalse();
        options.SyncManagementAfterUpload.Should().BeFalse();
        options.IsCopyrightProofOnlyRun().Should().BeFalse();
        options.IsCopyrightProofMaterialOnlyRun().Should().BeTrue();
        options.IsCopyrightProofWorkflowRun().Should().BeTrue();
    }

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
    public void Video_translation_is_unavailable_and_legacy_configuration_cannot_enable_it()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?>
            {
                QueueStepRegistry.Download,
                QueueStepRegistry.VideoTranslate,
            },
        });

        QueueStepRegistry.UserSelectable.Select(step => step.Key)
            .Should().NotContain(QueueStepRegistry.VideoTranslate);
        options.EnabledSteps.Should().Equal(QueueStepRegistry.Download);
        options.IsStepEnabled(QueueStepRegistry.VideoTranslate).Should().BeFalse();
        options.OrderedEnabledSteps().Should().NotContain(QueueStepRegistry.VideoTranslate);
        options.ToDictionary()["enabled_steps"].Should().BeEquivalentTo(
            new[] { QueueStepRegistry.Download });
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
    public void NormalizeStepStates_does_not_invent_completed_artifacts_for_legacy_uploaded_project()
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

        legacyUploaded.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(
            QueueStepStatus.Pending,
            "平台上传完成不能证明本机仍保留证明材料文件");
        legacyUploaded.StepStates[QueueStepKeys.GenerateEpisodeScript].Should().Be(QueueStepStatus.Pending);
        legacyUploaded.StepStates[QueueStepKeys.GenerateAiScriptOutline].Should().Be(QueueStepStatus.Pending);
        legacyUploaded.StepStates[QueueStepKeys.GenerateAiDramaMaterials].Should().Be(QueueStepStatus.Pending);
        legacyUploaded.StepStates[QueueStepKeys.GenerateTimestampCertificate].Should().Be(QueueStepStatus.Pending);
        legacyUploaded.StepStates[QueueStepKeys.GenerateProjectImages].Should().Be(QueueStepStatus.Pending);
        legacyUploaded.StepStates[QueueStepKeys.SilenceDetect].Should().Be(QueueStepStatus.Pending);
        explicitlyReset.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Pending);
    }
}
