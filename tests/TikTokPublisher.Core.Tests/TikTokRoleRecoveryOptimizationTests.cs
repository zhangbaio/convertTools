using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
}
