using System.Text.Json;
using FluentAssertions;
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

            WorkspaceBindingService.Bind(workspace, "acct-current", "当前账号");
            WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle();

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            item.ProjectDir.Should().Be(Path.GetFullPath(source));
            item.AccountProfileId.Should().Be("acct-current");
            item.AccountProfileName.Should().Be("当前账号");
            item.EpisodeCount.Should().Be(2);
            Path.GetFileName(item.PrimaryVideoPath).Should().Be("第1集.mp4");
        }
        finally
        {
            DeleteBestEffort(workspace);
            DeleteBestEffort(downloadRoot);
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
