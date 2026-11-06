using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouFailedEpisodeRepairTests
{
    [Fact]
    public void Titles_are_trimmed_and_repeated_names_collapse()
    {
        var titles = KuaishouFailedEpisodeRepair.ParseTitles(" 旷野之上 \n《面馆风波》\r\n旷野之上\n");

        Assert.Equal(["旷野之上", "面馆风波"], titles);
    }

    [Fact]
    public void Failed_episodes_use_video_status_and_zero_based_order()
    {
        var failure = KuaishouFailedEpisodeRepair.ParseSeriesLookup(
            """
            {
              "found": true,
              "seriesId": "54153393",
              "title": "旷野之上",
              "uploadReason": "第9,11,13集上传失败，请重新上传",
              "episodes": [
                {"orderNo": 0, "videoStatus": 1},
                {"orderNo": 8, "videoStatus": 2},
                {"orderNo": 10, "videoStatus": 2}
              ]
            }
            """,
            "旷野之上");

        Assert.NotNull(failure);
        Assert.Equal("54153393", failure.SeriesId);
        Assert.Equal([9, 11], failure.FailedEpisodes);
    }

    [Fact]
    public void Upload_reason_is_used_only_when_the_episode_list_is_missing()
    {
        var failure = KuaishouFailedEpisodeRepair.ParseSeriesLookup(
            """
            {"found": true, "seriesId": "1", "title": "旷野之上", "uploadReason": "第9,11,13集上传失败，请重新上传"}
            """,
            "旷野之上");

        Assert.NotNull(failure);
        Assert.Equal([9, 11, 13], failure.FailedEpisodes);
    }

    [Fact]
    public void A_different_platform_title_is_not_treated_as_the_requested_drama()
    {
        var failure = KuaishouFailedEpisodeRepair.ParseSeriesLookup(
            """
            {"found": true, "seriesId": "1", "title": "其他短剧", "episodes": [{"orderNo": 0, "videoStatus": 2}]}
            """,
            "旷野之上");

        Assert.Null(failure);
    }

    [Theory]
    [InlineData(true, true, false, KuaishouVideoRepairRoute.UploadLocal)]
    [InlineData(true, false, true, KuaishouVideoRepairRoute.RestoreAndDownload)]
    [InlineData(false, false, true, KuaishouVideoRepairRoute.RestoreAndDownload)]
    [InlineData(false, false, false, KuaishouVideoRepairRoute.Skip)]
    [InlineData(true, false, false, KuaishouVideoRepairRoute.Skip)]
    public void Route_prefers_local_videos_and_otherwise_uses_the_archive(
        bool listed,
        bool hasVideos,
        bool archived,
        KuaishouVideoRepairRoute route)
    {
        Assert.Equal(route, KuaishouFailedEpisodeRepair.Decide(listed, hasVideos, archived));
    }

    [Fact]
    public void Missing_failed_episode_files_are_reported()
    {
        var missing = KuaishouFailedEpisodeRepair.MissingEpisodes(
            [@"E:\videos\旷野之上-第9集.mp4", @"E:\videos\旷野之上-第10集.mp4"],
            [9, 11]);

        Assert.Equal([11], missing);
    }

    [Fact]
    public void Playable_video_search_ignores_empty_and_material_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "material-videos"));
        File.WriteAllBytes(Path.Combine(root, "material-videos", "第9集.mp4"), [1, 2, 3]);
        Assert.False(KuaishouFailedEpisodeRepair.ContainsPlayableVideo(root));

        var video = Path.Combine(root, "第9集.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        Assert.True(KuaishouFailedEpisodeRepair.ContainsPlayableVideo(root));
        Directory.Delete(root, true);
    }
}
