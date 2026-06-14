using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class WeixinMaterialPublishPageTests
{
    [Fact]
    public void ResolvePublishVideoPaths_Should_Prefer_MaterialVideos_Directory()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(projectDir, "videos")).FullName;
        var materialVideosDir = Directory.CreateDirectory(Path.Combine(projectDir, "material-videos")).FullName;

        File.WriteAllBytes(Path.Combine(videosDir, "剧名-第1集.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(materialVideosDir, "剧名-第1集.mp4"), [2]);

        var paths = WeixinMaterialPublishPage.ResolvePublishVideoPaths(projectDir, BuildOptions());

        paths.Should().ContainSingle();
        paths[0].Should().StartWith(materialVideosDir);
    }

    private static WeixinVideoPublishOptions BuildOptions()
    {
        return new WeixinVideoPublishOptions(
            Enabled: true,
            Navigation: new WeixinNavigationOptions("", "", ""),
            ReadyText: "",
            RunStrategy: "all",
            StateFile: ".weixin-channel-publish-state.json",
            PauseOnError: true,
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
