using ChannelsPublisher.Clip;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class StoryHighlightTests
{
    [Fact]
    public void CutSnapsToNearbyShotsInSilenceWithoutEnteringDialogue()
    {
        var clip = Clip(1, 1000, 4500);
        var timeline = new HighlightCutTimeline(Source(clip, [new(1000, 4500, "保留完整对白")],
            [800, 1300, 4200, 4600]));

        Assert.Equal((800, 4600), timeline.Cover(clip));
        Assert.Equal((800, 4600), timeline.Fit(800, 4600, 3600, 4000, 3400));
    }

    [Fact]
    public void CutKeepsWholeDialogueWhenItFitsAndDeclinesWhenItDoesNot()
    {
        var clip = Clip(1, 0, 10000);
        var timeline = new HighlightCutTimeline(Source(clip, [new(0, 3000, "铺垫"), new(3500, 6500, "完整反转")]));

        Assert.Equal((0, 6500), timeline.Fit(0, 10000, 5000, 7000, 4500));
        Assert.Null(timeline.Fit(0, 10000, 5000, 6000, 4500));
    }

    [Fact]
    public void LocalFallbackUsesAWholeLineFromAnotherEpisodeWhenOverlapIsForbidden()
    {
        var first = Source(Clip(1, 1000, 5500, 10), [new(1000, 5500, "本段已经用过")]);
        var second = Source(Clip(2, 1000, 5500, 3), [new(1000, 5500, "下一集的完整对白")]);
        var sources = new[] { first, second };
        var mode = new ClipModeOptions("highlight", [new(4, 5)], 0, false);

        var selected = ConfiguredClipPlanner.Plan(mode, new(4, 5), sources, [[Clip(1, 900, 5600)]],
            new ClipEngineOptions(), Timelines(sources));

        var clip = Assert.Single(selected);
        Assert.Equal(2, clip.EpisodeIndex);
        Assert.Equal((1000, 5500), (clip.StartMs, clip.EndMs));
        Assert.Equal("下一集的完整对白", clip.Text);
    }

    [Fact]
    public void StoryCandidatesIncludeLowScoringNeighboringSetupAndKeepModelOrder()
    {
        var first = Source(Clip(1, 10000, 15000, 1), [new(10000, 15000, "证据藏在抽屉里")]);
        var second = Source(Clip(2, 1000, 6000, 10), [new(1000, 6000, "打开抽屉揭晓真相")]);
        var sources = new[] { first, second };
        var candidates = StoryHighlightPlanner.Candidates(sources, Timelines(sources), new(9, 11), [], new());

        Assert.Equal(new[] { 1, 2 }, candidates.Select(clip => clip.EpisodeIndex));
        var selected = StoryHighlightPlanner.Validate([2, 1], candidates, new(9, 11), [], 0);
        Assert.Equal(new[] { 2, 1 }, selected.Select(clip => clip.EpisodeIndex));
        Assert.Equal(10000, selected.Sum(clip => clip.DurationMs));
    }

    [Fact]
    public void StoryPlanRejectsInvalidIdsOverlapAndInvalidDuration()
    {
        ClipCandidate[] candidates = [Clip(1, 1000, 6000), Clip(2, 2000, 7000), Clip(1, 5000, 10000)];
        var range = new ClipTimeRange(9, 11);

        Assert.Throws<InvalidDataException>(() => StoryHighlightPlanner.Validate([1, 4], candidates, range, [], 0));
        Assert.Throws<InvalidDataException>(() => StoryHighlightPlanner.Validate([1, 1], candidates, range, [], 0));
        Assert.Throws<InvalidDataException>(() => StoryHighlightPlanner.Validate([1, 3], candidates, range, [], 0));
        Assert.Throws<InvalidDataException>(() => StoryHighlightPlanner.Validate([1], candidates, range, [], 0));
        Assert.Throws<InvalidDataException>(() => StoryHighlightPlanner.Validate([1, 2], candidates, range, [[candidates[0]]], 0.5));
    }

    private static ClipCandidate Clip(int episode, int start, int end, double score = 1) => new()
    {
        VideoPath = $"episode-{episode}.mp4", EpisodeIndex = episode, StartMs = start, EndMs = end, Total = score,
    };

    private static ClipSourceAnalysis Source(ClipCandidate clip, IReadOnlyList<SubtitleSegment> dialogue, IReadOnlyList<int>? shots = null) =>
        new(new(clip.EpisodeIndex, clip.VideoPath), new(20000, 720, 1280, true, false), [clip], dialogue, shots ?? []);

    private static Dictionary<string, HighlightCutTimeline> Timelines(IEnumerable<ClipSourceAnalysis> sources) =>
        sources.ToDictionary(source => source.Source.VideoPath, source => new HighlightCutTimeline(source));
}
