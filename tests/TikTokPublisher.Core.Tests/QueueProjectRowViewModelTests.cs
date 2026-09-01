using Avalonia.Media;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProjectRowViewModelTests
{
    [Fact]
    public void Proof_failure_overrides_historical_upload_success_color()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            proofStatus: QueueStepStatus.Failed,
            lastError: "补全版权证明失败");

        row.IsUploadCompleted.Should().BeTrue();
        row.HasFailure.Should().BeTrue();
        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#FFC2C9"));
    }

    [Fact]
    public void Historical_upload_success_uses_accessible_theme_color_without_failure()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            proofStatus: QueueStepStatus.Completed);

        row.HasFailure.Should().BeFalse();
        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#6EE7B7"));
    }

    [Fact]
    public void Completed_title_color_stays_stable_when_status_is_still_running()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            proofStatus: QueueStepStatus.Completed,
            statusText: QueueStepStatus.Running,
            currentStep: QueueStepRegistry.UploadSeries);

        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#6EE7B7"));
    }

    [Fact]
    public void Drama_title_uses_dark_table_specific_state_colors()
    {
        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Pending,
            proofStatus: QueueStepStatus.Pending,
            statusText: QueueStepStatus.Running,
            currentStep: QueueStepRegistry.UploadSeries).DramaTitleBrush)
            .Should().Be(Color.Parse("#8EDBFF"));

        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Failed,
            proofStatus: QueueStepStatus.Pending,
            statusText: QueueStepStatus.Failed).DramaTitleBrush)
            .Should().Be(Color.Parse("#FFC2C9"));

        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Pending,
            proofStatus: QueueStepStatus.Pending).DramaTitleBrush)
            .Should().Be(Color.Parse("#F7FBFF"));
    }

    [Fact]
    public void Log_project_list_uses_the_same_failure_first_tone()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            proofStatus: QueueStepStatus.Failed,
            lastError: "补证明失败");
        var logs = new LogService();

        logs.UpdateSnapshot([row], "D:\\workspace", queueRunning: false);

        var project = logs.Projects.Single(item => item.ProjectPath == row.Item.ProjectDir);
        project.StatusTone.Should().Be("failed");
        ColorOf(project.Foreground).Should().Be(Color.Parse("#B42318"));
    }

    private static QueueProjectRowViewModel CreateRow(
        string uploadStatus,
        string proofStatus,
        string? lastError = null,
        string? statusText = null,
        string currentStep = "")
    {
        var item = new QueueProjectItem
        {
            ProjectDir = "D:\\workspace\\project",
            DisplayName = "测试剧集",
            NewTitle = "测试剧集",
            Enabled = true,
            CurrentStep = currentStep,
            StatusText = statusText ?? uploadStatus,
            LastError = lastError ?? string.Empty,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = uploadStatus,
                [QueueStepKeys.GenerateProofMaterial] = proofStatus,
            },
        };
        return new QueueProjectRowViewModel(item);
    }

    private static Color ColorOf(IBrush brush) =>
        brush.Should().BeOfType<SolidColorBrush>().Which.Color;
}
