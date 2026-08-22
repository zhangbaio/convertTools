using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class RoleVectorProgressTrackerTests
{
    [Fact]
    public void ProgressTracker_RestoresCompletedVisionBatchAndCharacterFile()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"role-vector-progress-{Guid.NewGuid():N}");
        var source = Path.Combine(workflow, "候选.jpg");
        var character = Path.Combine(workflow, "角色.png");
        Directory.CreateDirectory(workflow);
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(character, [4, 5, 6]);
        try
        {
            var first = RoleVectorProgressTracker.Open(workflow, "fingerprint-a", forceRerun: false, log: null);
            first.MarkVisionBatch([9, 10, 11], [source]);
            first.MarkCharacter("主角1", source, character);

            var resumed = RoleVectorProgressTracker.Open(workflow, "fingerprint-a", forceRerun: false, log: null);

            resumed.CheckedEpisodes.Should().BeEquivalentTo([9, 10, 11]);
            resumed.GetSelectedSources().Should().Equal(Path.GetFullPath(source));
            resumed.CanReuseCharacter("主角1", source, character).Should().BeTrue();

            File.WriteAllBytes(character, [9, 9, 9]);
            resumed.CanReuseCharacter("主角1", source, character).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void ProgressTracker_DiscardsCheckpointWhenRequestFingerprintChanges()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"role-vector-progress-reset-{Guid.NewGuid():N}");
        var source = Path.Combine(workflow, "候选.jpg");
        Directory.CreateDirectory(workflow);
        File.WriteAllBytes(source, [1]);
        try
        {
            RoleVectorProgressTracker.Open(workflow, "old", forceRerun: false, log: null)
                .MarkVisionBatch([3], [source]);

            var reset = RoleVectorProgressTracker.Open(workflow, "new", forceRerun: false, log: null);

            reset.CheckedEpisodes.Should().BeEmpty();
            reset.GetSelectedSources().Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void ProgressFingerprint_ChangesWithRoleGenerationConfiguration()
    {
        var item = new QueueProjectItem
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "role-vector-fingerprint"),
            NewTitle = "新剧名",
            OriginalTitle = "原剧名",
        };
        var settings = new ClientSettings { TiktokRoleVectorViewMode = "multi_angle" };
        var first = TikTokRoleVectorService.ComputeProgressFingerprint(item, settings, 5, 3);

        settings.TiktokRoleVectorViewMode = "single";
        var changedMode = TikTokRoleVectorService.ComputeProgressFingerprint(item, settings, 5, 3);
        var changedCount = TikTokRoleVectorService.ComputeProgressFingerprint(item, settings, 4, 3);

        changedMode.Should().NotBe(first);
        changedCount.Should().NotBe(changedMode);
    }
}
