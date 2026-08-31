using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadStateStoreRecoveryTests
{
    [Fact]
    public void Copyright_proof_completion_marker_is_persisted_and_cleared_on_retry()
    {
        var workflow = Path.Combine(
            Path.GetTempPath(),
            $"tiktok-proof-completion-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workflow);

            TikTokUploadStateStore.MarkCopyrightProofStepCompleted(workflow, "acct-1");

            TikTokUploadStateStore.HasCopyrightProofCompleted(workflow, "acct-1").Should().BeTrue();
            TikTokUploadStateStore.HasCopyrightProofCompleted(workflow, "acct-2").Should().BeFalse();
            TikTokUploadStateStore.LoadState(workflow)
                .Should().ContainKey("copyright_proof_completed_at");

            TikTokUploadStateStore.MarkCopyrightProofStepStarted(workflow);

            TikTokUploadStateStore.HasCopyrightProofCompleted(workflow, "acct-1").Should().BeFalse();
            var state = TikTokUploadStateStore.LoadState(workflow);
            state.Should().ContainKey("copyright_proof_started_at");
            state.Should().NotContainKey("copyright_proof_completed_at");
        }
        finally
        {
            if (Directory.Exists(workflow))
                Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Recovers_the_latest_confirmed_detail_url_from_failure_snapshots()
    {
        var workflow = CreateWorkflow();
        try
        {
            WriteSnapshot(workflow, "20260824-145217-971",
                "https://www.tiktokdramacenter.com/series/draft/7677486179335394320");
            WriteSnapshot(workflow, "20260825-132648-206",
                "https://www.tiktokdramacenter.com/series/draft");

            TikTokUploadStateStore.RecoverEditDetailUrlFromFailureSnapshots(workflow)
                .Should().Be("https://www.tiktokdramacenter.com/series/draft/7677486179335394320");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Ignores_untrusted_or_non_detail_snapshot_urls()
    {
        TikTokUploadStateStore.NormalizeSeriesDraftDetailUrl(
                "https://example.test/series/draft/7677486179335394320")
            .Should().BeEmpty();
        TikTokUploadStateStore.NormalizeSeriesDraftDetailUrl(
                "https://www.tiktokdramacenter.com/series/draft")
            .Should().BeEmpty();
    }

    [Fact]
    public void Recording_a_current_detail_url_replaces_stale_not_found_and_resists_later_misses()
    {
        var workflow = CreateWorkflow();
        try
        {
            TikTokUploadStateStore.RecordPlatformSeriesNotFound(
                workflow,
                "pre_upload_search",
                ["测试剧"]);

            TikTokUploadStateStore.TryRecordPlatformSeriesFromUrl(
                    workflow,
                    "https://www.tiktokdramacenter.com/series/draft/7677486179335394320?from=create",
                    "测试剧",
                    "failure_page_url")
                .Should().BeTrue();
            TikTokUploadStateStore.RecordPlatformSeriesNotFound(
                workflow,
                "later_search",
                ["测试剧"]);

            TikTokUploadStateStore.LoadCachedEditDetailUrl(workflow)
                .Should().Be("https://www.tiktokdramacenter.com/series/draft/7677486179335394320");
            var lookup = TikTokUploadStateStore.LoadState(workflow)["platform_series_lookup"];
            lookup.GetProperty("source").GetString().Should().Be("failure_page_url");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    private static string CreateWorkflow()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-state-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        return workflow;
    }

    private static void WriteSnapshot(string workflow, string name, string url)
    {
        var directory = Path.Combine(workflow, "upload-failure-snapshots", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "metadata.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["url"] = url,
            }));
    }
}
