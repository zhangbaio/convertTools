using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouEpisodeGapUploadTests
{
    [Fact]
    public void Empty_page_and_all_empty_cards_upload_the_whole_batch()
    {
        Assert.Equal(
            KuaishouPersonalEpisodeUploadService.UploadAction.UploadAll,
            KuaishouPersonalEpisodeUploadService.ChooseUploadAction([]));
        Assert.Equal(
            KuaishouPersonalEpisodeUploadService.UploadAction.UploadAll,
            KuaishouPersonalEpisodeUploadService.ChooseUploadAction(
            [
                new(1, false),
                new(2, false),
            ]));
    }

    [Fact]
    public void Partial_page_fills_only_the_missing_episode()
    {
        var slots = KuaishouPersonalEpisodeUploadService.NormalizeEpisodeSlots(
        [
            new(1, true),
            new(2, true),
            new(3, true),
            new(5, true),
        ]);

        Assert.Equal(
        [
            new KuaishouPersonalEpisodeUploadService.EpisodeVideoSlot(1, true),
            new(2, true),
            new(3, true),
            new(4, false),
            new(5, true),
        ], slots);
        Assert.Equal(
            KuaishouPersonalEpisodeUploadService.UploadAction.FillMissing,
            KuaishouPersonalEpisodeUploadService.ChooseUploadAction(slots));
    }

    [Fact]
    public void Complete_page_skips_another_upload()
    {
        Assert.Equal(
            KuaishouPersonalEpisodeUploadService.UploadAction.Skip,
            KuaishouPersonalEpisodeUploadService.ChooseUploadAction([new(1, true), new(2, true)]));
    }

    [Theory]
    [InlineData("第4集微光映归途上传视频上传链接", false)]
    [InlineData("第1集 微光映归途 上传视频 链接上传 视频预览", true)]
    [InlineData("为避免上传失败，请检查网络", false)]
    public void Uploaded_card_is_recognized_by_preview_text(string text, bool uploaded)
    {
        Assert.Equal(uploaded, KuaishouPersonalEpisodeUploadService.CardShowsUploadedVideo(text));
    }

    [Fact]
    public void Missing_episode_matches_only_its_own_file()
    {
        var paths = new[]
        {
            @"E:\videos\微光映归途-第1集.mp4",
            @"E:\videos\微光映归途-第4集.mp4",
            @"E:\videos\微光映归途-第14集.mp4",
        };

        Assert.Equal(@"E:\videos\微光映归途-第4集.mp4", KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(paths, 4));
        Assert.Null(KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(paths, 6));
    }
}
