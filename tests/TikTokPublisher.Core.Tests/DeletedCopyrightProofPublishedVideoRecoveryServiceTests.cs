using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class DeletedCopyrightProofPublishedVideoRecoveryServiceTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "published-proof-recovery-" + Guid.NewGuid().ToString("N"));

    public DeletedCopyrightProofPublishedVideoRecoveryServiceTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best effort cleanup for files that may still be scanned by a test runner.
        }
    }

    [Fact]
    public void ResolveRequiredEpisodeCount_uses_account_material_selection()
    {
        var settings = new ClientSettings
        {
            TiktokProjectImageRenderEpisodeLimit = 6,
        };
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            ],
        };

        DeletedCopyrightProofPublishedVideoRecoveryService
            .ResolveRequiredEpisodeCount(settings, account)
            .Should()
            .Be(1);

        account.TiktokCopyrightMaterialTypes =
        [
            TikTokPublishConstants.EditingProjectFilesMaterialType,
        ];
        DeletedCopyrightProofPublishedVideoRecoveryService
            .ResolveRequiredEpisodeCount(settings, account)
            .Should()
            .Be(6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(12, 4)]
    public void ResolveEpisodeDownloadConcurrency_caps_each_series_at_four(
        int pendingEpisodeCount,
        int expected)
    {
        DeletedCopyrightProofPublishedVideoRecoveryService
            .ResolveEpisodeDownloadConcurrency(pendingEpisodeCount)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void Recover_builds_proof_only_project_from_downloaded_platform_videos()
    {
        var staging = DeletedCopyrightProofPublishedVideoRecoveryService.ResolveStagingDirectory(
            _workspace,
            "已发布新剧",
            "series-123");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "第001集.mp4"), new byte[128]);
        File.WriteAllBytes(Path.Combine(staging, "第002集.mp4"), new byte[256]);

        var history = new QueueProjectItem
        {
            NewTitle = "已发布新剧",
            UploadCompletedAt = "2026-07-30T23:00:00+08:00",
            ProofMaterialStatementDate = "2026年7月30日",
        };
        var snapshot = new TikTokExecutionProjectSnapshot(
            _workspace,
            "2026-07-31T00:00:00+08:00",
            history);
        var account = new TikTokAccountProfile
        {
            Id = "account-2",
            Name = "账号二",
        };

        var result = DeletedCopyrightProofPublishedVideoRecoveryService.Recover(
            _workspace,
            snapshot,
            new TikTokPublishedVideoRecoverySource(
                "series-123",
                "https://www.tiktokdramacenter.com/series/detail/series-123",
                staging,
                PlatformEpisodeCount: 43,
                DownloadedEpisodeCount: 2),
            account);

        result.Ok.Should().BeTrue(result.Message);
        result.Project.Should().NotBeNull();
        var project = result.Project!;
        project.NewTitle.Should().Be("已发布新剧");
        project.OriginalTitle.Should()
            .Be(DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle);
        project.EpisodeCount.Should().Be(2);
        project.AccountProfileId.Should().Be("account-2");
        project.UploadSeriesStatus.Should().Be(QueueStepStatus.Completed);
        project.StepStates[QueueStepKeys.GenerateProofMaterial]
            .Should()
            .Be(QueueStepStatus.Pending);
        project.QueueEntryDramaType.Should()
            .Be(DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType);

        Directory.EnumerateFiles(Path.Combine(project.ProjectDir, "videos"), "*.mp4")
            .Should()
            .HaveCount(2);

        var metadata = JsonNode.Parse(
            File.ReadAllText(Path.Combine(project.ProjectDir, "shortdrama-project.json")))!
            .AsObject();
        metadata["tiktokPublishedRecovery"]!.GetValue<bool>().Should().BeTrue();
        metadata["tiktokSeriesId"]!.GetValue<string>().Should().Be("series-123");
        metadata["tiktokPlatformEpisodeCount"]!.GetValue<int>().Should().Be(43);
        metadata["tiktokDownloadedEpisodeCount"]!.GetValue<int>().Should().Be(2);
        metadata["episodeCount"]!.GetValue<int>().Should().Be(2);

        var persisted = WorkspaceQueueService.ScanProjects(_workspace)
            .Single(item => string.Equals(
                item.NewTitle,
                "已发布新剧",
                StringComparison.Ordinal));
        persisted.StepStates[QueueStepKeys.GenerateProofMaterial]
            .Should()
            .Be(QueueStepStatus.Pending);
        persisted.UploadSeriesStatus.Should().Be(QueueStepStatus.Completed);
    }
}
