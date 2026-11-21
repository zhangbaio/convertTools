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

    [Fact]
    public void RebirthPlanKeepsTheRevealAndPlaysEarlierScenesBeforeIt()
    {
        var earlier = Clip(1, 1000, 5000, 3);
        var wish = Clip(1, 7000, 14000, 10);
        var afterwards = Clip(2, 3000, 10000, 8);
        var first = new ClipSourceAnalysis(new(1, wish.VideoPath), new(30000, 720, 1280, true, false), [earlier, wish],
            [new(1000, 5000, "欠的钱我会想办法还"), new(7000, 14000, "只要能重来我一定对家里人好"), new(23000, 24000, "真回来了")],
            [16000]);
        var second = Source(afterwards, [new(3000, 10000, "这一次我来保护我们一家人")]);
        ClipSourceAnalysis[] sources = [first, second];
        var timelines = Timelines(sources);
        var continuity = new StoryHighlightContinuity(sources, timelines);
        var range = new ClipTimeRange(18, 25);

        Assert.Contains(StoryHighlightPlanner.Candidates(sources, timelines, range, [], new(), continuity),
            clip => clip.StartMs == wish.StartMs && clip.EndMs == 18500 && continuity.Role(clip) == "保留时空转场画面");
        var selected = continuity.Improve([wish, afterwards, earlier], range, [], 0, new(), null);

        Assert.Equal(new[] { earlier.StartMs, wish.StartMs, afterwards.StartMs }, selected.Select(clip => clip.StartMs));
        Assert.Equal(18500, selected[1].EndMs);
        Assert.True(StoryHighlightPlanner.FitsConstraints(selected, range, [], 0));
    }

    [Fact]
    public void EndingContinuesPastAnUnpunctuatedQuestionToTheCompleteDecision()
    {
        var question = Clip(1, 2000, 8000);
        var source = Source(question, [new(2000, 8000, "咱们是不是先缓一缓再看看"), new(11000, 16000, "现在背贷款日子会越过越紧咱们先不买了")]);
        ClipSourceAnalysis[] sources = [source];
        var timelines = Timelines(sources);
        var continuity = new StoryHighlightContinuity(sources, timelines);
        var range = new ClipTimeRange(6, 15);

        var selected = Assert.Single(continuity.Improve([question], range, [], 0, new(), null));

        Assert.Equal((2000, 16000), (selected.StartMs, selected.EndMs));
        Assert.EndsWith("咱们先不买了", selected.Text);
        Assert.Equal("人物决定或答复", continuity.Role(selected));
        Assert.Same(question, Assert.Single(continuity.Improve([question], new(6, 8), [], 0, new(), null)));
        Assert.Same(question, Assert.Single(continuity.Improve([question], range, [[Clip(1, 11000, 16000)]], 0, new(), null)));
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
