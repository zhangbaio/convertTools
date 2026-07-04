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

    [Fact]
    public void ResolvePublishVideoItems_Should_Use_StableEpisodeKeys_For_MaterialClips()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var clipDir = Directory.CreateDirectory(Path.Combine(projectDir, "material-clip-output")).FullName;

        File.WriteAllBytes(Path.Combine(clipDir, "高光-第48集.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(clipDir, "高光-第49集.mp4"), [1]);

        var items = WeixinMaterialPublishPage.ResolvePublishVideoItems(
            projectDir,
            BuildOptions(videoSourceMode: "material_clips"));

        items.Select(item => item.EpisodeIndex).Should().Equal([48, 49]);
    }

    [Fact]
    public void ResolvePublishVideoItems_Should_Fallback_To_PositionalKeys_When_MaterialClipEpisodeIsMissing()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var clipDir = Directory.CreateDirectory(Path.Combine(projectDir, "material-clip-output")).FullName;

        File.WriteAllBytes(Path.Combine(clipDir, "高光-a.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(clipDir, "高光-b.mp4"), [1]);

        var items = WeixinMaterialPublishPage.ResolvePublishVideoItems(
            projectDir,
            BuildOptions(videoSourceMode: "material_clips"));

        items.Select(item => item.EpisodeIndex).Should().Equal([1, 2]);
    }

    [Fact]
    public void ResolvePublishVideoItems_Should_Select_All_ProjectVideos()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(projectDir, "videos")).FullName;

        File.WriteAllBytes(Path.Combine(videosDir, "剧名-第1集.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(videosDir, "剧名-第2集.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(videosDir, "剧名-第3集.mp4"), [1]);

        var items = WeixinMaterialPublishPage.ResolvePublishVideoItems(
            projectDir,
            BuildOptions() with { EpisodeSelectionMode = "all", PublishCount = 1 });

        items.Select(item => item.EpisodeIndex).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void ResolvePublishVideoItems_Should_Use_CustomVideoFiles()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var sourceDir = Directory.CreateTempSubdirectory().FullName;
        var first = Path.Combine(sourceDir, "b-第2集.mp4");
        var second = Path.Combine(sourceDir, "a-第1集.mp4");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [1]);

        var items = WeixinMaterialPublishPage.ResolvePublishVideoItems(
            projectDir,
            BuildOptions(videoSourceMode: "custom_files") with
            {
                EpisodeSelectionMode = "all",
                CustomVideoFiles = [first, second]
            });

        items.Select(item => Path.GetFileName(item.VideoPath)).Should().Equal(["a-第1集.mp4", "b-第2集.mp4"]);
    }

    [Fact]
    public void BuildPublishDescription_Should_Use_PerVideoSidecarDescription()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var videoPath = Path.Combine(projectDir, "素材.mp4");
        File.WriteAllBytes(videoPath, [1]);
        File.WriteAllText(Path.Combine(projectDir, "素材.publish.json"), """{"description":"#旁车描述"}""");

        var description = WeixinMaterialPublishPage.BuildPublishDescription(
            new ProjectInfo("原剧", "新剧", null, null, null, null, 1, 1, 1, "公司", projectDir, ""),
            BuildOptions(),
            new WeixinMaterialPublishPage.PublishVideoItem(1, videoPath));

        description.Should().Be("#旁车描述");
    }

    private static WeixinVideoPublishOptions BuildOptions(string videoSourceMode = "project")
    {
        return new WeixinVideoPublishOptions(
            Enabled: true,
            Navigation: new WeixinNavigationOptions("", "", ""),
            ReadyText: "",
            RunStrategy: "all",
            StateFile: ".weixin-channel-publish-state.json",
            AllowDuplicatePublish: false,
            PauseOnError: true,
            VideoSourceMode: videoSourceMode,
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
