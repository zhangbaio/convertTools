using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokRoleRecoveryOptimizationTests
{
    [Fact]
    public void Recovery_episodes_are_grouped_into_three_episode_batches()
    {
        TikTokReferenceSourcePackageService.ResolveRoleReferenceRecoveryBatches(
                Enumerable.Range(9, 8).ToArray())
            .Should().BeEquivalentTo(
                new[]
                {
                    new[] { 9, 10, 11 },
                    new[] { 12, 13, 14 },
                    new[] { 15, 16 },
                },
                options => options.WithStrictOrdering());
    }

    [Fact]
    public void Supplemental_frames_keep_quality_and_timeline_diversity_before_vision_review()
    {
        var root = Path.Combine(Path.GetTempPath(), $"role-recovery-prefilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var frames = Enumerable.Range(1, 12)
                .Select(index => Path.Combine(root, $"补充_第09集_{index:D2}.jpg"))
                .ToArray();
            foreach (var path in frames)
            {
                using var image = new Image<Rgba32>(160, 160, new Rgba32(38, 52, 68));
                image.SaveAsJpeg(path);
            }

            var selected = TikTokReferenceSourcePackageService.SelectSupplementalRoleRecoveryFrames(
                frames,
                TikTokReferenceSourcePackageService.RoleRecoveryModelFramesPerEpisode);

            selected.Should().HaveCount(6).And.OnlyHaveUniqueItems();
            selected.Should().Contain(path =>
                string.Compare(Path.GetFileName(path), "补充_第09集_07.jpg", StringComparison.Ordinal) >= 0,
                "预筛不能只取视频前半段或质量排序最前的连续帧");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Default_recovery_batch_stays_within_single_vision_review_capacity()
    {
        const int alreadyMatched = 5 - 1;
        var submitted = alreadyMatched +
                        TikTokReferenceSourcePackageService.RoleRecoveryEpisodeBatchSize *
                        TikTokReferenceSourcePackageService.RoleRecoveryModelFramesPerEpisode;

        submitted.Should().BeLessThanOrEqualTo(
            TikTokReferenceSourcePackageService.ResolveVisionCandidateMaximum(5));
    }

    [Theory]
    [InlineData(null, TikTokReferenceSourcePackageService.LocalRoleReferenceSelectionMode)]
    [InlineData("unknown", TikTokReferenceSourcePackageService.LocalRoleReferenceSelectionMode)]
    [InlineData("legacy", TikTokReferenceSourcePackageService.LocalRoleReferenceSelectionMode)]
    [InlineData(" AI_FULL_REVIEW ", TikTokReferenceSourcePackageService.AiFullReviewRoleReferenceSelectionMode)]
    public void Role_reference_selection_mode_is_normalized_safely(string? configured, string expected)
    {
        TikTokReferenceSourcePackageService.ResolveRoleReferenceSelectionMode(new ClientSettings
            {
                TiktokRoleReferenceSelectionMode = configured!,
            })
            .Should().Be(expected);
    }

    [Fact]
    public void Ai_full_review_keeps_all_valid_frames_while_legacy_uses_prefiltered_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"role-review-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var all = Enumerable.Range(1, 12)
                .Select(index => Path.Combine(root, $"frame-{index:D2}.jpg"))
                .ToArray();
            foreach (var path in all) File.WriteAllBytes(path, [1]);
            var legacy = all.Take(6).ToArray();

            TikTokReferenceSourcePackageService.ResolveRoleRecoveryModelCandidates(
                    all, legacy, TikTokReferenceSourcePackageService.LocalRoleReferenceSelectionMode)
                .Should().Equal(legacy);
            TikTokReferenceSourcePackageService.ResolveRoleRecoveryModelCandidates(
                    all, legacy, TikTokReferenceSourcePackageService.AiFullReviewRoleReferenceSelectionMode)
                .Should().Equal(all);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ai_full_review_does_not_truncate_large_initial_candidate_pool()
    {
        TikTokReferenceSourcePackageService.ResolveVisionDiscoveryCandidateCount(
                128,
                TikTokReferenceSourcePackageService.LocalRoleReferenceSelectionMode)
            .Should().Be(96);
        TikTokReferenceSourcePackageService.ResolveVisionDiscoveryCandidateCount(
                128,
                TikTokReferenceSourcePackageService.AiFullReviewRoleReferenceSelectionMode)
            .Should().Be(128);
    }

    [Fact]
    public void Local_review_capacity_matches_one_identity_batch()
    {
        TikTokReferenceSourcePackageService.VisionIdentityBatchCapacityForTests
            .Should().Be(24);
    }

    [Theory]
    [InlineData(4, 5, 3, 2, false, false)]
    [InlineData(4, 5, 3, 3, false, true)]
    [InlineData(4, 5, 3, 0, true, true)]
    [InlineData(2, 5, 3, 9, true, false)]
    [InlineData(5, 5, 3, 9, true, false)]
    public void Minimum_fallback_requires_minimum_and_stagnation_or_exhaustion(
        int actual,
        int target,
        int minimum,
        int noGrowthBatches,
        bool allEpisodesChecked,
        bool expected)
    {
        TikTokReferenceSourcePackageService.ShouldUseMinimumRoleFallback(
                actual,
                target,
                minimum,
                noGrowthBatches,
                allEpisodesChecked)
            .Should().Be(expected);
    }

    [Fact]
    public void Ai_review_failure_fallback_does_not_swallow_user_cancellation()
    {
        TikTokReferenceSourcePackageService.IsRoleReferenceAiReviewFailure(
                new TimeoutException("timeout"),
                CancellationToken.None)
            .Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        TikTokReferenceSourcePackageService.IsRoleReferenceAiReviewFailure(
                new TaskCanceledException(),
                cancellation.Token)
            .Should().BeFalse();
    }
}
