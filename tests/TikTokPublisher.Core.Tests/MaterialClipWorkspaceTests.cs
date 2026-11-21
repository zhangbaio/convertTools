using ChannelsPublisher.Clip;
using ChannelsPublisher.Core.Config;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class MaterialClipWorkspaceTests
{
    [Fact]
    public void TasksAndBatchCopiesKeepSeparateSettingsAndOutputDirectories()
    {
        var defaults = new ClipConfig { StoryHighlights = true, StoryHardSubtitles = true };
        var first = TaskFor("first", defaults);
        var second = TaskFor("second", defaults);
        first.Configuration.SetRanges("highlight", [new(60, 75), new(65, 75)]);
        second.Configuration.SetModeEnabled("highlight", false);
        second.Configuration.SetModeEnabled("slice", true);
        second.Configuration.SetRanges("slice", [new(30, 45), new(30, 45), new(30, 45)]);

        var firstRun = first.CreateRun(new(), new());
        var secondRun = second.CreateRun(new(), new());
        Assert.Equal(new ClipTimeRange(65, 75), Assert.Single(firstRun.ModeSettings!).Ranges[1]);
        Assert.True(firstRun.StoryHardSubtitles);
        Assert.Equal("slice", Assert.Single(secondRun.ModeSettings!).Mode);
        Assert.Equal(3, Assert.Single(secondRun.ModeSettings!).Ranges.Count);

        second.Configuration = first.Configuration.Copy();
        second.Configuration.SetRanges("highlight", [new(90, 120)]);
        Assert.Equal(2, first.Configuration.GetRanges("highlight").Count);
        Assert.Equal(second.OutputFolder, second.CreateRun(new(), new()).OutputDirectory);
        Assert.Equal(300, defaults.RangesFor("highlight")[0].MinSeconds);
    }

    [Fact]
    public void StoppedTaskKeepsItsRunSnapshotAndWaitingTaskCanStillStart()
    {
        var task = TaskFor("active", new ClipConfig());
        var waiting = TaskFor("waiting", new ClipConfig());
        var services = new MaterialClipConnections { AiModel = "before" };
        task.Configuration.SetRanges("highlight", [new(60, 75)]);
        var snapshot = task.CreateRun(services, new ClientSettings());
        task.BeginRun(snapshot);
        Assert.False(task.CanRetry);

        services.AiModel = "after";
        task.Finish("已停止");
        Assert.True(task.CanRetry);
        Assert.Same(snapshot, task.LastRun);
        Assert.Equal("before", task.LastRun!.AiModel);
        Assert.True(waiting.Pending);
        Assert.Null(waiting.LastRun);

        task.Configuration.SetRanges("highlight", [new(80, 95)]);
        task.PrepareForNewRun();
        Assert.True(task.Pending);
        Assert.False(task.CanRetry);
        Assert.Equal(new ClipTimeRange(60, 75), Assert.Single(Assert.Single(snapshot.ModeSettings!).Ranges));
        Assert.Equal(new ClipTimeRange(80, 95), Assert.Single(Assert.Single(task.CreateRun(services, new()).ModeSettings!).Ranges));
    }

    [Fact]
    public void LegacyRangesAndReviewTimelineWorkWithoutNewManifestMetadata()
    {
        var config = new MaterialClipTaskConfiguration(new ClipConfig());
        config.SetModeEnabled("slice", true);
        config.SetRanges("slice", [new(40, 30)]);
        Assert.Contains("下限", config.Validate());
        Assert.Throws<InvalidDataException>(() => config.BuildOptions(new(), new(), "title", "output"));

        var moments = MaterialClipPreviewWindow.BuildMoments(
            [new(2, 4000, 9000, ""), new(1, 7000, 13000, "人物醒来"), new(1, 17000, 22000, "")]);
        Assert.Equal((5, "第 2 集 → 第 1 集"), moments[1]);
        Assert.Equal((5, "画面复评 · 人物醒来"), moments[2]);
        Assert.Equal((13, "结尾 · 第 1 集"), moments[^1]);
    }

    private static MaterialClipQueueItem TaskFor(string title, ClipConfig defaults) =>
        new("project", title, [new(1, "episode.mp4")], Path.Combine("output", title), new(defaults));
}
