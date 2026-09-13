using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokFileUploadTransportTests
{
    [Fact]
    public void Local_playwright_allows_a_video_larger_than_the_cdp_stream_limit()
    {
        var sizes = new[] { 55L * 1024 * 1024 };

        TikTokBrowserActions.RequiresCdpPathInjection(
                TikTokFileUploadTransport.LocalPlaywright,
                sizes)
            .Should().BeFalse();
    }

    [Fact]
    public void Cdp_requires_path_injection_for_a_video_larger_than_the_stream_limit()
    {
        var sizes = new[] { 55L * 1024 * 1024 };

        TikTokBrowserActions.RequiresCdpPathInjection(
                TikTokFileUploadTransport.CdpPathInjection,
                sizes)
            .Should().BeTrue();
    }

    [Fact]
    public void Cdp_requires_path_injection_when_a_batch_exceeds_the_stream_limit()
    {
        var sizes = new[] { 24L * 1024 * 1024, 24L * 1024 * 1024 };

        TikTokBrowserActions.RequiresCdpPathInjection(
                TikTokFileUploadTransport.CdpPathInjection,
                sizes)
            .Should().BeTrue();
    }

    [Fact]
    public void More_files_than_the_configured_batch_size_automatically_use_batches()
    {
        var options = new TikTokPublishOptions
        {
            UploadStrategy = "classic",
            UploadBatchSize = 3,
        };

        TikTokBatchUploadService.ShouldUseBatchedUpload(options, 60).Should().BeTrue();
        TikTokBatchUploadService.ShouldUseBatchedUpload(options, 3).Should().BeFalse();
    }

    [Fact]
    public void Explicit_batch_strategy_batches_even_a_single_file()
    {
        var options = new TikTokPublishOptions
        {
            UploadStrategy = "batch",
            UploadBatchSize = 3,
        };

        TikTokBatchUploadService.ShouldUseBatchedUpload(options, 1).Should().BeTrue();
    }

    [Fact]
    public void Edit_video_alignment_stops_before_the_first_duplicate_episode()
    {
        var rows = new[]
        {
            new TikTokBrowserActions.EditVideoRow(1, 1),
            new TikTokBrowserActions.EditVideoRow(2, 1),
            new TikTokBrowserActions.EditVideoRow(3, 2),
        };

        TikTokBrowserActions.FindAlignedEditVideoPrefixCount(rows).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(DisorderedVideoRows))]
    public void Edit_video_alignment_stops_before_disorder_or_slot_gaps(
        TikTokBrowserActions.EditVideoRow[] rows,
        int expectedAligned)
    {
        TikTokBrowserActions.FindAlignedEditVideoPrefixCount(rows).Should().Be(expectedAligned);
    }

    public static IEnumerable<object[]> DisorderedVideoRows()
    {
        yield return
        [
            new[]
            {
                new TikTokBrowserActions.EditVideoRow(1, 1),
                new TikTokBrowserActions.EditVideoRow(2, 3),
                new TikTokBrowserActions.EditVideoRow(3, 2),
            },
            1,
        ];
        yield return
        [
            new[]
            {
                new TikTokBrowserActions.EditVideoRow(1, 1),
                new TikTokBrowserActions.EditVideoRow(3, 3),
            },
            1,
        ];
        yield return
        [
            new[]
            {
                new TikTokBrowserActions.EditVideoRow(1, 1),
                new TikTokBrowserActions.EditVideoRow(2, 2),
                new TikTokBrowserActions.EditVideoRow(3, 3),
            },
            3,
        ];
    }

}
