using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using Xunit;
using FluentAssertions;

namespace TikTokPublisher.Core.Tests;

public sealed class AccountUploadedSeriesCatalogTests
{
    [Fact]
    public void Compose_keeps_uploaded_rows_for_the_account_and_prefers_archive()
    {
        var queue = new[]
        {
            Queue("E:\\work\\已归档", "acct-1", uploaded: true, episodes: 12, completedAt: "2026-11-10 15:00:00", original: "原剧甲", rewritten: "新剧甲"),
            Queue("E:\\work\\还在队列", "acct-1", uploaded: true, episodes: 5, completedAt: "2026-11-10 18:00:00", original: "原剧乙", rewritten: "新剧乙"),
            Queue("E:\\work\\别人的", "acct-2", uploaded: true, episodes: 3, completedAt: "2026-11-10 19:00:00", original: "原剧丙", rewritten: "新剧丙"),
            Queue("E:\\work\\失败", "acct-1", uploaded: false, episodes: 8, completedAt: "", original: "原剧丁", rewritten: "新剧丁"),
        };
        var archived = new[]
        {
            Archive("acct-1", "原剧甲", "新剧甲", "2026-11-10T16:30:00", "E:\\work\\已归档", "E:\\archive\\甲"),
            Archive("acct-2", "原剧戊", "新剧戊", "2026-11-10T12:00:00", "E:\\work\\别人归档", "E:\\archive\\戊"),
        };

        var rows = AccountUploadedSeriesCatalog.Compose("acct-1", queue, archived);

        rows.Select(row => row.NewTitle).Should().Equal("新剧乙", "新剧甲");
        rows[0].EpisodeCount.Should().Be(5);
        rows[0].Status.Should().Be("成功");
        rows[0].CompletedAt.Should().Be("2026-11-10 18:00:00");
        rows[0].ProjectDirectory.Should().Be(Path.GetFullPath("E:\\work\\还在队列"));
        rows[1].EpisodeCount.Should().Be(12);
        rows[1].CompletedAt.Should().Be("2026-11-10 16:30:00");
        rows[1].ProjectDirectory.Should().Be(Path.GetFullPath("E:\\archive\\甲"));
    }

    [Fact]
    public void Compose_returns_nothing_without_an_account()
    {
        var queue = new[]
        {
            Queue("E:\\work\\剧", "acct-1", uploaded: true, episodes: 1, completedAt: "2026-11-10 18:00:00", original: "原", rewritten: "新"),
        };

        AccountUploadedSeriesCatalog.Compose("  ", queue, Array.Empty<ArchivedProjectItem>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Archived_episode_count_falls_back_to_archive_metadata_then_project_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "uploaded-series-" + Guid.NewGuid().ToString("N"));
        var workflow = Path.Combine(root, "archive", "workflow", "_新剧");
        var metaPath = Path.Combine(root, "archive", "meta", "新剧.json");
        try
        {
            Directory.CreateDirectory(workflow);
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            File.WriteAllText(Path.Combine(workflow, "tiktok-upload-state.json"),
                """{ "upload_step_attempted": true, "last_upload_completed_at": "2026-10-25T09:17:28" }""");
            File.WriteAllText(Path.Combine(workflow, "shortdrama-project.json"),
                """{ "episodeCount": 77, "episodes": "all" }""");

            var withoutMeta = Archive("acct-1", "原剧", "新剧", "2026-10-25T09:17:29", "", workflow);
            AccountUploadedSeriesCatalog.TryReadEpisodeCount(withoutMeta).Should().Be(77);

            File.WriteAllText(metaPath, """{ "episode_count": 65 }""");
            var withMeta = withoutMeta with { MetadataPath = metaPath };
            AccountUploadedSeriesCatalog.TryReadEpisodeCount(withMeta).Should().Be(65);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static QueueProjectItem Queue(
        string projectDir,
        string accountId,
        bool uploaded,
        int episodes,
        string completedAt,
        string original,
        string rewritten) =>
        new()
        {
            ProjectDir = projectDir,
            AccountProfileId = accountId,
            EpisodeCount = episodes,
            UploadCompletedAt = completedAt,
            OriginalTitle = original,
            NewTitle = rewritten,
            DisplayName = rewritten,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = uploaded ? QueueStepStatus.Completed : QueueStepStatus.Failed,
            },
        };

    private static ArchivedProjectItem Archive(
        string accountId,
        string original,
        string rewritten,
        string archivedAt,
        string workflowProjectDir,
        string archivedWorkflowDir) =>
        new(
            ProjectKey: rewritten,
            DisplayName: rewritten,
            OriginalTitle: original,
            NewTitle: rewritten,
            ArchivedAt: archivedAt,
            QueuedAt: "",
            MetadataPath: "",
            ArchiveProjectDir: archivedWorkflowDir,
            ArchiveSource: "tiktok",
            ArchivedSourceDir: "",
            ArchivedWorkflowDir: archivedWorkflowDir,
            AccountProfileId: accountId,
            WorkflowProjectDir: workflowProjectDir);
}
