using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class WeixinMaterialPublishStateServiceTests
{
    [Fact]
    public void ResolveEffectiveRunStrategy_Should_DefaultMaterialPublishAll_ToResume()
    {
        var options = BuildOptions(runStrategy: "all");

        var result = WeixinMaterialPublishStateService.ResolveEffectiveRunStrategy(options);

        result.Should().Be("resume");
    }

    [Fact]
    public void ResolveEffectiveRunStrategy_Should_ForceResume_WhenDuplicatePublishEnabled()
    {
        var options = BuildOptions(runStrategy: "retry_failed", allowDuplicatePublish: true);

        var result = WeixinMaterialPublishStateService.ResolveEffectiveRunStrategy(options);

        result.Should().Be("resume");
    }

    [Fact]
    public void PrepareDuplicatePublishSession_Should_ClearExistingEntries_AndResumeUnfinishedRound()
    {
        var state = new MaterialPublishState(
            new Dictionary<string, MaterialPublishStateEntry>(StringComparer.Ordinal)
            {
                ["1"] = new("success", @"D:\videos\a.mp4", DateTimeOffset.Now, null),
                ["2"] = new("success", @"D:\videos\b.mp4", DateTimeOffset.Now, null),
            },
            null);
        var requested = new[]
        {
            new WeixinMaterialPublishPage.PublishVideoItem(1, @"D:\videos\a.mp4"),
            new WeixinMaterialPublishPage.PublishVideoItem(2, @"D:\videos\b.mp4"),
        };

        var action = WeixinMaterialPublishStateService.PrepareDuplicatePublishSession(state, requested, enabled: true);

        action.Should().Be("started");
        state.Entries.Should().BeEmpty();

        state.Entries = WeixinMaterialPublishStateService.UpsertEntry(
            state.Entries,
            "1",
            new MaterialPublishStateEntry("success", @"D:\videos\a.mp4", DateTimeOffset.Now, null));

        action = WeixinMaterialPublishStateService.PrepareDuplicatePublishSession(state, requested, enabled: true);

        action.Should().Be("resume");
        WeixinMaterialPublishStateService.SelectPublishItemsByStrategy(requested, "resume", state)
            .Select(item => item.EpisodeIndex)
            .Should()
            .Equal([2]);
    }

    [Fact]
    public void CompleteDuplicatePublishSessionIfDone_Should_CloseActiveSession()
    {
        var requested = new[]
        {
            new WeixinMaterialPublishPage.PublishVideoItem(48, @"D:\clips\高光-第48集.mp4"),
            new WeixinMaterialPublishPage.PublishVideoItem(49, @"D:\clips\高光-第49集.mp4"),
        };
        var state = new MaterialPublishState(
            new Dictionary<string, MaterialPublishStateEntry>(StringComparer.Ordinal)
            {
                ["48"] = new("success", requested[0].VideoPath, DateTimeOffset.Now, null),
                ["49"] = new("success", requested[1].VideoPath, DateTimeOffset.Now, null),
            },
            new MaterialDuplicatePublishSession(
                Active: true,
                Targets: requested.Select(item => new MaterialDuplicatePublishTarget(item.EpisodeIndex, item.VideoPath)).ToArray(),
                StartedAt: DateTimeOffset.Now,
                UpdatedAt: DateTimeOffset.Now,
                CompletedAt: null));

        var completed = WeixinMaterialPublishStateService.CompleteDuplicatePublishSessionIfDone(state, requested);

        completed.Should().BeTrue();
        state.MaterialDuplicateSession.Should().NotBeNull();
        state.MaterialDuplicateSession!.Active.Should().BeFalse();
        state.MaterialDuplicateSession.CompletedAt.Should().NotBeNull();
    }

    private static WeixinVideoPublishOptions BuildOptions(
        string runStrategy,
        bool allowDuplicatePublish = false)
    {
        return new WeixinVideoPublishOptions(
            Enabled: true,
            Navigation: new WeixinNavigationOptions("", "", ""),
            ReadyText: "",
            RunStrategy: runStrategy,
            StateFile: ".weixin-channel-material-publish-state.json",
            AllowDuplicatePublish: allowDuplicatePublish,
            PauseOnError: true,
            VideoSourceMode: "project",
            FillDescription: true,
            FillShortTitle: false,
            DescriptionTemplate: "{新剧名}",
            PrependHashToDescription: true,
            LocationOptionText: "",
            LinkOptionText: "",
            LinkPickerButtonText: "",
            LinkPickerSelector: "",
            LinkDialogTitle: "",
            LinkSearchPlaceholder: "",
            ActivityOptionText: "",
            TimingOptionText: "",
            ShortTitleMaxLength: 15,
            FinalAction: "publish",
            FinalActionText: "发布",
            WaitAfterUploadSeconds: 0.5,
            WaitAfterFinalActionSeconds: 0,
            EpisodeSelectionMode: "range",
            StartEpisodeIndex: 2,
            PublishCount: 4,
            EpisodeIndexes: [],
            VideoUploadSelector: "input[type='file']");
    }
}
