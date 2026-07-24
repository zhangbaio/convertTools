using Avalonia.Media;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProjectRowViewModelTests
{
    [Fact]
    public void DramaTitleBrush_UsesAccessibleCompletedColorBeforeActiveState()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            statusText: QueueStepStatus.Running,
            currentStep: QueueStepRegistry.UploadSeries);

        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#6EE7B7"));
    }

    [Fact]
    public void DramaTitleBrush_UsesDarkTableSpecificStateColors()
    {
        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Pending,
            statusText: QueueStepStatus.Running,
            currentStep: QueueStepRegistry.UploadSeries).DramaTitleBrush)
            .Should().Be(Color.Parse("#8EDBFF"));

        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Failed,
            statusText: QueueStepStatus.Failed).DramaTitleBrush)
            .Should().Be(Color.Parse("#FFC2C9"));

        ColorOf(CreateRow(
            uploadStatus: QueueStepStatus.Pending,
            statusText: QueueStepStatus.Pending).DramaTitleBrush)
            .Should().Be(Color.Parse("#F7FBFF"));
    }

    private static QueueProjectRowViewModel CreateRow(
        string uploadStatus,
        string statusText,
        string currentStep = "") =>
        new(
            new QueueProjectItem
            {
                ProjectDir = "",
                OriginalTitle = "原剧名",
                NewTitle = "新剧名",
                CurrentStep = currentStep,
                StatusText = statusText,
                StepStates = new Dictionary<string, string>
                {
                    [QueueStepKeys.UploadSeries] = uploadStatus,
                },
            });

    private static Color ColorOf(IBrush brush) =>
        brush.Should().BeOfType<SolidColorBrush>().Which.Color;
}
