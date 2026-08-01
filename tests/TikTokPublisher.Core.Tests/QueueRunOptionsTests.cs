using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueRunOptionsTests
{
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
    public void Live_action_detection_runs_immediately_after_download_but_uses_dedicated_mode()
    {
        var options = new QueueRunOptions
        {
            EnabledSteps =
            [
                QueueStepRegistry.UploadSeries,
                QueueStepRegistry.RewriteInfo,
                QueueStepRegistry.Download,
            ],
            LiveActionDetectionMode = LiveActionDetectionRunMode.ForceEnable,
        };

        options.OrderedEnabledSteps().Should().Equal(
            QueueStepRegistry.Download,
            QueueStepRegistry.DetectLiveAction,
            QueueStepRegistry.RewriteInfo,
            QueueStepRegistry.UploadSeries);
        QueueStepRegistry.UserSelectable.Select(step => step.Key)
            .Should().NotContain(QueueStepRegistry.DetectLiveAction);
    }

    [Fact]
    public void Live_action_detection_mode_follows_each_account_and_supports_run_override()
    {
        var enabledAccount = new TikTokAccountProfile { TiktokLiveActionDetectionEnabled = true };
        var disabledAccount = new TikTokAccountProfile { TiktokLiveActionDetectionEnabled = false };
        var options = new QueueRunOptions();

        options.ShouldRunLiveActionDetection(enabledAccount).Should().BeTrue();
        options.ShouldRunLiveActionDetection(disabledAccount).Should().BeFalse();

        options.LiveActionDetectionMode = LiveActionDetectionRunMode.ForceEnable;
        options.ShouldRunLiveActionDetection(disabledAccount).Should().BeTrue();

        options.LiveActionDetectionMode = LiveActionDetectionRunMode.ForceSkip;
        options.ShouldRunLiveActionDetection(enabledAccount).Should().BeFalse();
        options.OrderedEnabledSteps().Should().NotContain(QueueStepRegistry.DetectLiveAction);
    }

    [Fact]
    public void Live_action_detection_run_override_round_trips_but_is_not_persisted()
    {
        var options = new QueueRunOptions
        {
            LiveActionDetectionMode = LiveActionDetectionRunMode.ForceEnable,
        };

        QueueRunOptions.FromDictionary(options.ToDictionary()).LiveActionDetectionMode
            .Should().Be(LiveActionDetectionRunMode.ForceEnable);
        QueueRunOptions.FromDictionary(options.ToPersistentDictionary()).LiveActionDetectionMode
            .Should().Be(LiveActionDetectionRunMode.FollowAccount);
    }

    [Fact]
    public void Legacy_live_action_step_migrates_to_account_default_once()
    {
        var account = new TikTokAccountProfile
        {
            TiktokQueueEnabledSteps =
            [
                QueueStepRegistry.Download,
                QueueStepRegistry.DetectLiveAction,
            ],
        };

        AccountStore.MigrateLegacyLiveActionDetectionConfig([account]).Should().BeTrue();

        account.TiktokLiveActionDetectionEnabled.Should().BeTrue();
        account.TiktokLiveActionDetectionConfigured.Should().BeTrue();
        account.TiktokQueueEnabledSteps.Should().Equal(QueueStepRegistry.Download);
        AccountStore.MigrateLegacyLiveActionDetectionConfig([account]).Should().BeFalse();
    }

    [Theory]
    [InlineData(CopyrightProofExecutionMode.GenerateMaterialOnly)]
    [InlineData(CopyrightProofExecutionMode.GenerateAndEdit)]
    public void Copyright_proof_workflows_never_enable_live_action_detection(
        CopyrightProofExecutionMode executionMode)
    {
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepRegistry.DetectLiveAction],
        };

        options.ConfigureForCopyrightProof(executionMode);

        options.EnabledSteps.Should().NotContain(QueueStepRegistry.DetectLiveAction);
        options.LiveActionDetectionMode.Should().Be(LiveActionDetectionRunMode.ForceSkip);
        options.OrderedEnabledSteps().Should().NotContain(QueueStepRegistry.DetectLiveAction);
    }

    [Fact]
    public void Live_action_result_round_trips_with_queue_payload()
    {
        var item = new QueueProjectItem
        {
            LiveActionClassification = "LiveAction",
            LiveActionConfidence = 0.93,
            LiveActionDetectionReason = "多帧均为真实演员实拍",
            LiveActionDetectedAt = "2026-08-01T12:00:00+08:00",
            LiveActionVideoFingerprint = "fingerprint",
            PipelineExcluded = true,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepRegistry.DetectLiveAction] = QueueStepStatus.Excluded,
                [QueueStepRegistry.RewriteInfo] = QueueStepStatus.Skipped,
            },
        };

        var restored = QueueProjectItem.FromPayload(item.ToPayload());

        restored.LiveActionClassification.Should().Be("LiveAction");
        restored.LiveActionConfidence.Should().BeApproximately(0.93, 0.0001);
        restored.LiveActionDetectionReason.Should().Be("多帧均为真实演员实拍");
        restored.LiveActionDetectedAt.Should().Be("2026-08-01T12:00:00+08:00");
        restored.LiveActionVideoFingerprint.Should().Be("fingerprint");
        restored.PipelineExcluded.Should().BeTrue();
        restored.IsPendingUpload.Should().BeFalse();
        restored.StepStates[QueueStepRegistry.DetectLiveAction].Should().Be(QueueStepStatus.Excluded);
        restored.StepStates[QueueStepRegistry.RewriteInfo].Should().Be(QueueStepStatus.Skipped);
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
}
