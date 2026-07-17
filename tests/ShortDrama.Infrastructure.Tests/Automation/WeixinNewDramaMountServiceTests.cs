using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class WeixinNewDramaMountServiceTests
{
    [Fact]
    public void ResolveWorkspaceRoot_Should_Return_WorkflowParent()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var workflow = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(workflow, "_mounted-drama")).FullName;

        WeixinNewDramaMountService.ResolveWorkspaceRoot(project).Should().Be(root);
    }

    [Theory]
    [InlineData("all", 1, 30, "all")]
    [InlineData("range", 3, 4, "3-6")]
    [InlineData("range", 8, 1, "8")]
    public void ResolveEpisodeSelectionText_Should_Match_DownloadSelection(
        string mode,
        int start,
        int count,
        string expected)
    {
        var options = BuildOptions() with
        {
            EpisodeSelectionMode = mode,
            StartEpisodeIndex = start,
            PublishCount = count
        };

        WeixinNewDramaMountService.ResolveEpisodeSelectionText(options).Should().Be(expected);
    }

    [Fact]
    public void ResolveEpisodeSelectionText_Should_Use_ExplicitIndexes()
    {
        var options = BuildOptions() with
        {
            EpisodeSelectionMode = "explicit",
            EpisodeIndexes = [5, 2, 5, 1]
        };

        WeixinNewDramaMountService.ResolveEpisodeSelectionText(options).Should().Be("1,2,5");
    }

    private static WeixinVideoPublishOptions BuildOptions()
    {
        return new WeixinVideoPublishOptions(
            Enabled: true,
            Navigation: new WeixinNavigationOptions("", "", ""),
            ReadyText: "",
            RunStrategy: "all",
            StateFile: ".weixin-channel-publish-state.json",
            AllowDuplicatePublish: false,
            PauseOnError: true,
            VideoSourceMode: "new_drama_mount",
            FillDescription: true,
            FillShortTitle: false,
            DescriptionTemplate: "",
            PrependHashToDescription: true,
            LocationOptionText: "",
            LinkOptionText: "",
            LinkPickerButtonText: "",
            LinkPickerSelector: "",
            LinkDialogTitle: "",
            LinkSearchPlaceholder: "",
            ActivityOptionText: "",
            TimingOptionText: "",
            ShortTitleMaxLength: 20,
            FinalAction: "",
            FinalActionText: "",
            WaitAfterUploadSeconds: 0,
            WaitAfterFinalActionSeconds: 0,
            EpisodeSelectionMode: "range",
            StartEpisodeIndex: 1,
            PublishCount: 1,
            EpisodeIndexes: [],
            VideoUploadSelector: "");
    }
}
