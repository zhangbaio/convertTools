using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class PublishedRecoveryOriginalTitleCompletionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"complete-recovery-title-{Guid.NewGuid():N}");

    [Fact]
    public void Complete_updates_project_metadata_and_preserves_upload_state()
    {
        var source = Path.Combine(_root, "恢复新剧名_版权恢复");
        var workflow = Path.Combine(_root, "workflow", "_恢复新剧名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);
        File.WriteAllText(
            Path.Combine(source, "短剧信息.txt"),
            $"原剧名: {DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle}\n新剧名: 恢复新剧名\n");
        File.WriteAllText(
            Path.Combine(workflow, "短剧信息.txt"),
            $"原剧名: {DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle}\n新剧名: 恢复新剧名\n");
        var completedAt = "2026-08-20T10:00:00+08:00";
        var item = new QueueProjectItem
        {
            ProjectDir = source,
            OriginalTitle = DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle,
            NewTitle = "恢复新剧名",
            QueueEntryDramaType = DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
            UploadCompletedAt = completedAt,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                [QueueStepKeys.GenerateEpisodeScript] = QueueStepStatus.Failed,
                [QueueStepKeys.GenerateAiScriptOutline] = QueueStepStatus.Failed,
                [QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Completed,
                [QueueStepKeys.DeleteSourceVideos] = QueueStepStatus.Completed,
            },
        };
        item.NormalizeStepStates();
        var drama = new DramaSearchItem
        {
            BookId = "pikachu:12345",
            Title = "真实原剧名",
            Intro = "真实原剧简介。",
            Category = "都市",
            EpisodeTotal = 80,
        };

        PublishedRecoveryOriginalTitleCompletionService.Complete(
            item,
            "真实原剧名",
            drama);

        item.OriginalTitle.Should().Be("真实原剧名");
        item.Description.Should().Be("真实原剧简介。");
        item.UploadCompletedAt.Should().Be(completedAt);
        item.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Completed);
        item.StepStates[QueueStepKeys.DeleteSourceVideos].Should().Be(QueueStepStatus.Completed);
        item.StepStates[QueueStepKeys.GenerateEpisodeScript].Should().Be(QueueStepStatus.Pending);
        item.StepStates[QueueStepKeys.GenerateAiScriptOutline].Should().Be(QueueStepStatus.Pending);
        item.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Pending);

        foreach (var directory in new[] { source, workflow })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(directory, "shortdrama-project.json")));
            var root = document.RootElement;
            root.GetProperty("originalTitle").GetString().Should().Be("真实原剧名");
            root.GetProperty("bookId").GetString().Should().Be("pikachu:12345");
            root.GetProperty("intro").GetString().Should().Be("真实原剧简介。");
            root.GetProperty("declaredEpisodeCount").GetInt32().Should().Be(80);
            root.GetProperty("recoveryOriginalTitleConfirmed").GetBoolean().Should().BeTrue();
            File.ReadAllText(Path.Combine(directory, "短剧信息.txt"))
                .Should().Contain("原剧名: 真实原剧名");
        }
    }

    [Fact]
    public void Complete_refuses_to_overwrite_different_real_original_title()
    {
        var source = Path.Combine(_root, "恢复新剧名_版权恢复");
        Directory.CreateDirectory(source);
        var item = new QueueProjectItem
        {
            ProjectDir = source,
            OriginalTitle = "已有真实原剧名",
            QueueEntryDramaType = DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
        };

        var action = () => PublishedRecoveryOriginalTitleCompletionService.Complete(
            item,
            "另一个原剧名",
            new DramaSearchItem());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*拒绝覆盖*");
    }

    private static void WriteMetadata(string directory, string source, string workflow)
    {
        File.WriteAllText(
            Path.Combine(directory, "shortdrama-project.json"),
            JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
                originalTitle = DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle,
                newTitle = "恢复新剧名",
                tiktokPublishedRecovery = true,
                queueEntryDramaType = DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
