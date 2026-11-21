using ChannelsPublisher.Clip;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class StoryHighlightEvidenceTests
{
    [Fact]
    public void StableHardSubtitleBecomesATimedCueAndPersistentOverlayIsFiltered()
    {
        var observations = Enumerable.Range(0, 30)
            .Select(frame => new SubtitleObservation(frame, .30, .84, .40, .025, "常驻标语")).Concat([
                new(8, .30, .65, .40, .03, "我决定留下"),
                new(9, .30, .65, .40, .03, "我 决定 留下"),
                new(11, .30, .65, .40, .03, "仅出现一次的误识别"),
            ]).ToArray();

        var cue = Assert.Single(ClipHardSubtitles.BuildTimeline(observations, 500, 15000));

        Assert.Equal(new SubtitleSegment(4000, 4750, "我决定留下"), cue);
    }

    [Fact]
    public void HardSubtitleOnlyFillsGapsInThePrimaryDialogue()
    {
        SubtitleSegment[] primary = [new(0, 2000, "已经识别的对白")];
        SubtitleSegment[] hard = [new(500, 1500, "重复对白"), new(2700, 3800, "只出现在画面里的话")];

        Assert.Equal(hard[1], Assert.Single(ClipHardSubtitles.MissingDialogue(primary, hard)));
    }

    [Fact]
    public void DialogueBandKeepsNewCaptionsAndRejectsNearbyDocumentText()
    {
        var observations = Enumerable.Range(0, 8).Select(frame =>
            new SubtitleObservation(frame, .30, .68, .40, .03, frame < 4 ? "第一句字幕" : "第二句字幕")).Concat([
                new(8, .30, .75, .40, .025, "文书中的文字"), new(9, .30, .75, .40, .025, "文书中的文字"),
                new(10, .32, .68, .36, .03, "补入这一句"), new(11, .32, .68, .36, .03, "补入这一句"),
            ]).ToArray();
        SubtitleSegment[] primary = [new(0, 4000, "已有对白")];

        var missing = ClipHardSubtitles.MissingDialogue(primary, ClipHardSubtitles.BuildTimeline(observations, 500, 15000, primary));

        Assert.Equal("补入这一句", Assert.Single(missing).Text);
    }

    [Fact]
    public void VisualNominationsIncludeSilentShotsAndLeaveClearOfSpeech()
    {
        var source = new ClipSourceAnalysis(new(1, "episode.mp4"), new(12000, 720, 1280, true, false), [],
            [new(0, 3100, "这是清晰的有声对白")], [3500, 6900]);

        var clips = StoryVisualReview.SilentShots(source).ToArray();

        Assert.NotEmpty(clips);
        Assert.All(clips, clip =>
        {
            Assert.True(clip.IsVisualOnly);
            Assert.True(clip.StartMs >= 3500);
        });
    }

    [Fact]
    public void VisionResponseOnlyReturnsValidUniqueCandidateScores()
    {
        var response = """{"scenes":[{"id":1,"score":8,"summary":"睁眼时出现年代字幕"},{"id":1,"score":9},{"id":2,"score":100},{"id":3,"score":"7"},{"id":4,"score":7}]}""";

        Assert.Equal((1, 8d, "睁眼时出现年代字幕"), Assert.Single(StoryVisualReview.Parse(response, 3)));
    }

    [Fact]
    public void OverlappingVisualEvidenceIsRetainedWithoutRepeatingTheFootage()
    {
        var speech = new ClipCandidate { VideoPath = "ep.mp4", StartMs = 1000, EndMs = 10000, Text = "对白和转场" };
        var visual = new ClipCandidate { VideoPath = "ep.mp4", StartMs = 8000, EndMs = 9500, IsVisualOnly = true,
            VisualDescription = "醒来，并出现年代字幕" };

        var selected = Assert.Single(StoryHighlightPlanner.MergeVisualOverlap([visual, speech]));

        Assert.Equal((1000, 10000), (selected.StartMs, selected.EndMs));
        Assert.Equal(visual.VisualDescription, selected.VisualDescription);
        Assert.Empty(speech.VisualDescription);
    }

    [Fact]
    public void ReviewedSilentShotCanConnectTwoSelectedLinesWithoutReplacingTheEnding()
    {
        var left = new ClipCandidate { VideoPath = "ep.mp4", EpisodeIndex = 1, StartMs = 0, EndMs = 3000, Text = "你看清楚了" };
        var right = new ClipCandidate { VideoPath = "ep.mp4", EpisodeIndex = 1, StartMs = 6000, EndMs = 9000, Text = "我决定留下" };
        var visual = new ClipCandidate { VideoPath = "ep.mp4", EpisodeIndex = 1, StartMs = 3300, EndMs = 5700,
            IsVisualOnly = true, VisualDescription = "手里的信被展开" };
        var source = new ClipSourceAnalysis(new(1, "ep.mp4"), new(10000, 720, 1280, true, false), [left, right, visual],
            [new(0, 3000, left.Text), new(6000, 9000, right.Text)], [3300, 5700]);
        var continuity = new StoryHighlightContinuity([source], new Dictionary<string, HighlightCutTimeline>
        {
            [source.Source.VideoPath] = new(source),
        });

        var selected = continuity.Improve([left, right], new(6, 9), [], 0, new ClipEngineOptions { StoryVisualReview = true }, null);

        Assert.Equal(new[] { left, visual, right }, selected);
        Assert.Equal(new[] { left, right }, continuity.Improve([left, right], new(6, 7), [], 0,
            new ClipEngineOptions { StoryVisualReview = true }, null));
    }
}
