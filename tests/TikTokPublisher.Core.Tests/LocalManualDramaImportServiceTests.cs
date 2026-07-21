using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LocalManualDramaImportServiceTests
{
    [Fact]
    public void Import_Creates_Project_Metadata_And_Queues_External_Local_Drama()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-workspace-{Guid.NewGuid():N}");
        var downloadRoot = Path.Combine(Path.GetTempPath(), $"manual-import-download-{Guid.NewGuid():N}");
        var source = Path.Combine(downloadRoot, "重回 95 订婚宴，我靠创业逆风翻盘");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "第2集.mp4"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.jpg"), [7, 8, 9]);
            File.WriteAllText(Path.Combine(source, "简介.txt"), "本地手动下载剧集简介");

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.SourceProjectDir.Should().Be(Path.GetFullPath(source));
            result.EpisodeCount.Should().Be(2);
            result.WorkflowProjectDir.Should().StartWith(Path.Combine(Path.GetFullPath(workspace), "workflow"));
            Directory.Exists(result.WorkflowProjectDir).Should().BeTrue();

            var metadataPath = Path.Combine(source, "shortdrama-project.json");
            File.Exists(metadataPath).Should().BeTrue();
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            metadata.RootElement.GetProperty("sourceProjectDir").GetString().Should().Be(Path.GetFullPath(source));
            metadata.RootElement.GetProperty("workflowProjectDir").GetString().Should().Be(result.WorkflowProjectDir);
            metadata.RootElement.GetProperty("episodeCount").GetInt32().Should().Be(2);
            metadata.RootElement.GetProperty("intro").GetString().Should().Be("本地手动下载剧集简介");
            File.Exists(Path.Combine(source, "海报原图.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(result.WorkflowProjectDir, "海报原图.jpg")).Should().BeTrue();

            WorkspaceBindingService.Bind(workspace, "acct-current", "当前账号");
            WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle();

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            item.ProjectDir.Should().Be(Path.GetFullPath(source));
            item.AccountProfileId.Should().Be("acct-current");
            item.AccountProfileName.Should().Be("当前账号");
            item.EpisodeCount.Should().Be(2);
            Path.GetFileName(item.PrimaryVideoPath).Should().Be("第1集.mp4");
            item.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "本地原始封面只是输入素材，必须按改写后的新剧名执行生成海报步骤");
        }
        finally
        {
            DeleteBestEffort(workspace);
            DeleteBestEffort(downloadRoot);
        }
    }

    [Fact]
    public void ScanProjects_Local_Manual_Poster_Requires_Current_Title_Generation_State()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-poster-state-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "本地原剧名");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.png"), [4, 5, 6]);

            var import = LocalManualDramaImportService.Import(workspace, source);
            var item = WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle().Subject;
            item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
            WorkspaceQueueService.SaveProjects(workspace, [item]);

            var repaired = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            repaired.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "历史版本误标完成但没有真实生成状态时必须自动修复");

            var inputPath = Path.Combine(source, "海报原图.png");
            var outputPath = Path.Combine(import.WorkflowProjectDir, TikTokPosterGenerationStateService.OutputFileName);
            File.WriteAllBytes(outputPath, [7, 8, 9]);
            TikTokPosterGenerationStateService.SaveGeneratedState(
                repaired,
                new ClientSettings(),
                inputPath,
                outputPath);
            repaired.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
            WorkspaceQueueService.SaveProjects(workspace, [repaired]);

            var current = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            current.StepStates[QueueStepKeys.GeneratePoster].Should().Be(QueueStepStatus.Completed);

            ProjectInfoTextHelper.UpdateFields(
                Path.Combine(import.WorkflowProjectDir, "短剧信息.txt"),
                new Dictionary<string, string> { ["新剧名"] = "全新剧名" });

            var renamed = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            renamed.NewTitle.Should().Be("全新剧名");
            renamed.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "海报生成状态中的剧名与当前新剧名不一致时必须重新生成");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Poster_State_Video_Frame_Fallback_Does_Not_Repeat_For_Unchanged_Request()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-poster-fallback-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "本地原剧名");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.png"), [4, 5, 6]);

            var import = LocalManualDramaImportService.Import(workspace, source);
            var item = WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle().Subject;
            var inputPath = Path.Combine(source, "海报原图.png");
            var outputPath = Path.Combine(import.WorkflowProjectDir, TikTokPosterGenerationStateService.OutputFileName);
            File.WriteAllBytes(outputPath, [7, 8, 9]);
            var requestedSettings = new ClientSettings { PosterMode = "video_frame" };

            TikTokPosterGenerationStateService.SaveGeneratedState(
                item,
                requestedSettings,
                inputPath,
                outputPath,
                effectivePosterMode: "original");

            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, requestedSettings).Should().BeFalse(
                "视频抽帧失败并回退成功后，相同请求不应在每次队列执行时重复生成");

            var changedSettings = requestedSettings.Clone();
            changedSettings.FrameExtractTime += 1.0;
            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, changedSettings).Should().BeTrue(
                "视频抽帧配置变化后仍应重新尝试生成");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite on Windows may still hold the queue db briefly after a save.
        }
    }
}
