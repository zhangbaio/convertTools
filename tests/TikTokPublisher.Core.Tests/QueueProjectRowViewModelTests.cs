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
        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#B42318"));
    }

    [Fact]
    public void Historical_upload_success_stays_green_without_current_failure()
    {
        var row = CreateRow(
            uploadStatus: QueueStepStatus.Completed,
            proofStatus: QueueStepStatus.Completed,
            lastError: "");

        row.HasFailure.Should().BeFalse();
        ColorOf(row.DramaTitleBrush).Should().Be(Color.Parse("#047857"));
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

    [Fact]
    public void Ai_production_reports_running_without_progress_and_names_each_step()
    {
        var item = new QueueProjectItem
        {
            StepStates =
            {
                [QueueStepKeys.GenerateEpisodeScript] = QueueStepStatus.Completed,
                [QueueStepKeys.GenerateAiDramaMaterials] = QueueStepStatus.Running,
            },
        };
        var row = new QueueProjectRowViewModel(item);

        row.AiProductionStatus.Should().Be("执行中");
        row.AiProductionTooltip.Should().Contain("剧本 · 已完成")
            .And.Contain("AI素材 · 执行中")
            .And.Contain("AI大纲 · 待执行")
            .And.Contain("角色图 · 待执行");
    }

    [Theory]
    [InlineData(QueueStepStatus.Completed, QueueStepStatus.Completed)]
    [InlineData(QueueStepStatus.Failed, QueueStepStatus.Failed)]
    [InlineData(QueueStepStatus.Stopped, QueueStepStatus.Stopped)]
    [InlineData(QueueStepStatus.Skipped, QueueStepStatus.Completed)]
    [InlineData(QueueStepStatus.Pending, QueueStepStatus.Pending)]
    public void Ai_production_rolls_up_enabled_steps(string materialsStatus, string expectedStatus)
    {
        var item = new QueueProjectItem
        {
            StepStates =
            {
                [QueueStepKeys.GenerateEpisodeScript] = QueueStepStatus.Completed,
                [QueueStepKeys.GenerateAiDramaMaterials] = materialsStatus,
            },
            LastError = materialsStatus == QueueStepStatus.Failed ? "素材生成失败原因" : "",
        };
        var row = new QueueProjectRowViewModel(item);
        row.SetAiProductionSteps([QueueStepKeys.GenerateEpisodeScript, QueueStepKeys.GenerateAiDramaMaterials]);

        row.AiProductionStatus.Should().Be(expectedStatus);
        if (materialsStatus == QueueStepStatus.Failed)
            row.AiProductionTooltip.Should().Contain("AI素材 · 失败").And.Contain(item.LastError);
    }

    [Fact]
    public void Mixed_ai_production_selection_preserves_legacy_enabled_steps_until_explicitly_changed()
    {
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepKeys.GenerateEpisodeScript, QueueStepKeys.GenerateProofMaterial],
        };

        var selected = QueueAiProductionGroup.SelectedSteps(options);
        var checkState = QueueAiProductionGroup.CheckState(selected.Count);

        checkState.Should().BeNull();
        QueueAiProductionGroup.ResolveSelection(checkState, options).Should()
            .Equal(QueueStepKeys.GenerateEpisodeScript);
        QueueAiProductionGroup.ResolveSelection(true, options).Should().Equal(
            QueueStepKeys.GenerateEpisodeScript, QueueStepKeys.GenerateAiDramaMaterials,
            QueueStepKeys.GenerateAiScriptOutline, QueueStepKeys.GenerateRoleVector);
        QueueAiProductionGroup.ResolveSelection(false, options).Should().BeEmpty();
    }

    private static QueueProjectRowViewModel CreateRow(
        string uploadStatus,
        string proofStatus,
        string lastError)
    {
        var item = new QueueProjectItem
        {
            ProjectDir = "D:\\workspace\\project",
            DisplayName = "测试剧集",
            NewTitle = "测试剧集",
            Enabled = true,
            StatusText = uploadStatus,
            LastError = lastError,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = uploadStatus,
                [QueueStepKeys.GenerateProofMaterial] = proofStatus,
            },
        };
        return new QueueProjectRowViewModel(item);
    }

    private static Color ColorOf(IBrush brush) =>
        brush.Should().BeOfType<SolidColorBrush>().Subject.Color;
}
