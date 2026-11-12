using System.Text;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokApiUploadServiceTests
{
    private static readonly TikTokVodSigner.Credentials FakeCredentials = new("AKFAKE", "SKFAKE", "TOKENFAKE");

    [Fact]
    public void Vod_signature_for_apply_upload_matches_reference_vector()
    {
        var uri = new Uri("https://vod.example.com/?Action=ApplyUploadInner&Version=2020-11-19&SpaceName=space&FileType=video&IsInner=1&FileSize=123");

        var authorization = TikTokVodSigner.BuildAuthorization("GET", uri, "20261111T054303Z", FakeCredentials, null);

        authorization.Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKFAKE/20261111/ap-singapore-1/vod/aws4_request, " +
            "SignedHeaders=x-amz-date;x-amz-security-token, " +
            "Signature=9d62a26bdd3182cfcf7a9230c2d7ec4a9ebdabe2f501bddfaa4151ed44acc0df");
    }

    [Fact]
    public void Vod_signature_for_commit_signs_the_body_hash()
    {
        var uri = new Uri("https://vod.example.com/?Action=CommitUploadInner&Version=2020-11-19&SpaceName=space");

        var authorization = TikTokVodSigner.BuildAuthorization(
            "POST", uri, "20261111T054305Z", FakeCredentials, "{\"SessionKey\":\"abc\"}");

        authorization.Should().EndWith(
            "SignedHeaders=x-amz-content-sha256;x-amz-date;x-amz-security-token, " +
            "Signature=2232f9aa0f0a0ab1099ec5fa844808c40a12d11f4c02e7209feb49595eb547bf");
    }

    [Fact]
    public void Canonical_query_sorts_and_reencodes_parameters()
    {
        TikTokVodSigner.CanonicalQuery("?b=2&a=x%2Fy&c=hello world")
            .Should().Be("a=x%2Fy&b=2&c=hello%20world");
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        TikTokCrc32.Hex(Encoding.ASCII.GetBytes("123456789")).Should().Be("cbf43926");
    }

    [Fact]
    public void Finish_body_lists_part_crcs_in_order()
    {
        TikTokApiUploadService.BuildFinishBody(["742efc2f", "a41be95c"]).Should().Be("1:742efc2f,2:a41be95c");
    }

    [Theory]
    [InlineData("https://www.tiktokdramacenter.com/series/draft/7706784720234976308", "7706784720234976308")]
    [InlineData("https://www.tiktokdramacenter.com/series/7706784720234976308?tab=1", "7706784720234976308")]
    [InlineData("https://www.tiktokdramacenter.com/series/draft", null)]
    [InlineData("https://www.tiktokdramacenter.com/series/list", null)]
    public void Series_id_is_read_from_editor_urls(string url, string? expected)
    {
        TikTokApiUploadService.TryReadSeriesIdFromUrl(url).Should().Be(expected);
    }

    [Fact]
    public void Service_hosts_follow_the_page_domain()
    {
        TikTokApiUploadService.ResolveServiceBaseUrl("https://www.tiktokdramacenter.com/series/draft/1", "vod")
            .Should().Be("https://vod.tiktokdramacenter.com");
        TikTokApiUploadService.ResolveServiceBaseUrl("https://www.tiktokdramacenter.com/series/draft/1", "tos")
            .Should().Be("https://tos.tiktokdramacenter.com");
    }

    [Fact]
    public void Local_episodes_are_sorted_by_episode_number()
    {
        var episodes = TikTokApiUploadService.ResolveLocalEpisodes(
            [@"E:\x\剧-第10集.mp4", @"E:\x\剧-第2集.mp4", @"E:\x\剧-第1集.mp4"]);

        episodes!.Select(item => item.Episode).Should().Equal(1, 2, 10);
        episodes![0].Title.Should().Be("剧-第1集");
    }

    [Fact]
    public void Local_episodes_reject_unnumbered_or_duplicate_files()
    {
        TikTokApiUploadService.ResolveLocalEpisodes([@"E:\x\剧-第1集.mp4", @"E:\x\花絮.mp4"]).Should().BeNull();
        TikTokApiUploadService.ResolveLocalEpisodes([@"E:\x\剧-第1集.mp4", @"E:\y\剧-第1集.mp4"]).Should().BeNull();
    }

    [Fact]
    public void Series_detail_videos_are_parsed_with_episode_numbers()
    {
        using var document = JsonDocument.Parse(
            """
            {"videos":[
              {"videoId":"11","orderNum":1,"title":"八零林海求生录-第1集"},
              {"videoId":"12","orderNum":2,"title":"第 2 集 八零林海求生录-第3集"},
              {"videoId":"13","orderNum":3,"title":"花絮"}
            ]}
            """);

        var videos = TikTokApiUploadService.ParseSeriesVideos(document.RootElement);

        videos.Select(video => video.Episode).Should().Equal(1, 3, null);
        TikTokApiUploadService.ExistingVideosUsable(videos).Should().BeFalse();
        TikTokApiUploadService.ExistingVideosUsable(videos.Take(2).ToList()).Should().BeTrue();
    }

    [Fact]
    public void Usable_existing_videos_keep_the_first_copy_of_each_episode()
    {
        var videos = new TikTokApiUploadService.SeriesVideo[]
        {
            new("good", 1, "剧-第1集", 1),
            new("blank", 2, "花絮", null),
            new("again", 3, "剧-第1集", 1),
        };

        TikTokApiUploadService.SelectUsableExisting(videos).Select(video => video.VideoId).Should().Equal("good");
        TikTokApiUploadService.PlanDropRejected(videos).Should().BeNull();
        TikTokApiUploadService.PlanDropRejected(
        [
            new("blank", 1, "花絮", null),
            new("good", 2, "剧-第1集", 1),
        ]).Should().Equal("good", "blank");
        TikTokApiUploadService.PlanDropRejected(videos.Take(1).ToList()).Should().BeNull();
        TikTokBrowserActions.BodyContainsSelectedFile("已选择 The Six-Year Lie-第1集.mp4", [@"E:\x\The Six-Year Lie-第1集.mp4"])
            .Should().BeTrue();
        TikTokBrowserActions.BodyContainsSelectedFile("合同、剧名和简介", [@"E:\x\The Six-Year Lie-第1集.mp4"])
            .Should().BeFalse();
    }

    [Fact]
    public void Existing_videos_with_duplicate_episodes_are_not_usable()
    {
        TikTokApiUploadService.ExistingVideosUsable(
        [
            new("1", 1, "剧-第1集", 1),
            new("2", 2, "剧-第1集", 1),
        ]).Should().BeFalse();
    }

    [Fact]
    public void Reorder_plan_is_null_when_already_ascending()
    {
        TikTokApiUploadService.PlanReorder(
        [
            new("a", 1, "", 1),
            new("b", 2, "", 2),
        ]).Should().BeNull();
    }

    [Fact]
    public void Reorder_plan_sorts_video_ids_by_episode()
    {
        TikTokApiUploadService.PlanReorder(
        [
            new("a", 1, "", 1),
            new("c", 2, "", 3),
            new("b", 3, "", 2),
        ]).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Add_video_body_matches_the_web_request()
    {
        var body = TikTokApiUploadService.BuildAddVideoBody(
            "7706784720234976308",
            new TikTokApiUploadService.CommittedVideo("v103", 76323.991, "tos-poster"),
            "八零林海求生录-第1集",
            7);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("contractId").GetString().Should().Be("7706784720234976308");
        var video = root.GetProperty("video");
        video.GetProperty("videoType").GetInt32().Should().Be(3);
        video.GetProperty("duration").GetDouble().Should().Be(76323.991);
        video.GetProperty("vid").GetString().Should().Be("v103");
        video.GetProperty("title").GetString().Should().Be("八零林海求生录-第1集");
        video.GetProperty("coverUri").GetString().Should().Be("tos-poster");
        video.GetProperty("orderNum").GetInt32().Should().Be(7);
    }

    [Theory]
    [InlineData("{\"contractId\":\"7706784720234976308\",\"video\":{}}", "7706784720234976308")]
    [InlineData("{\"video\":{}}", null)]
    [InlineData("not json", null)]
    public void Contract_id_is_read_from_add_request(string postData, string? expected)
    {
        TikTokApiUploadService.ReadContractId(postData).Should().Be(expected);
    }

    [Fact]
    public void Account_api_upload_settings_flow_into_publish_options()
    {
        var account = new TikTokAccountProfile
        {
            TiktokUploadStrategy = "api",
            TiktokUploadApiConcurrency = 9,
            TiktokUploadApiFallbackToBatch = false,
            TiktokProxyEnabled = true,
            TiktokProxyHost = "127.0.0.1",
            TiktokProxyPort = 7890,
        };

        var options = TikTokPublishOptions.FromAccount(account);

        options.UseApiUpload.Should().BeTrue();
        options.UseBatchUpload.Should().BeFalse();
        options.UploadApiConcurrency.Should().Be(6);
        options.UploadApiFallbackToBatch.Should().BeFalse();
        options.NetworkProxyServer.Should().Be("http://127.0.0.1:7890");
    }

    [Fact]
    public void Upload_logs_distinguish_a_finished_upload_from_mounting_it()
    {
        var timing = new TikTokApiUploadService.UploadTiming(
            30L * 1024 * 1024, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));
        TikTokApiUploadService.FormatUploadedLog(25, TimeSpan.FromSeconds(45), timing, 13, 137)
            .Should().Be("接口直传：第 25 集已上传（45 秒，30.0MB，3.0MB/s；传输 10 秒，提交 3 秒，已上传 13/137）。");
        TikTokApiUploadService.FormatUploadedLog(76, TimeSpan.FromSeconds(125), timing, 40, 137)
            .Should().Be("接口直传：第 76 集已上传（2 分 5 秒，30.0MB，3.0MB/s；传输 10 秒，提交 3 秒，已上传 40/137）。");
        TikTokApiUploadService.FormatMountedLog(25, 13, 137)
            .Should().Be("接口直传：第 25 集已挂到剧集（已挂载 13/137）。");
        TikTokApiUploadService.FormatUploadSummaryLog(68, 2048L * 1024 * 1024, TimeSpan.FromSeconds(200))
            .Should().Be("接口直传：68 集共 2.00GB，上传用时 3 分 20 秒，平均 10.2MB/s。");
    }

    [Fact]
    public void Order_numbers_continue_after_the_existing_max()
    {
        TikTokApiUploadService.AssignOrderNums(0, 3).Should().Equal(1, 2, 3);
        TikTokApiUploadService.AssignOrderNums(5, 2).Should().Equal(6, 7);
    }

    [Fact]
    public void Add_video_response_retries_only_server_failures()
    {
        var ok = TikTokApiUploadService.InspectAddVideoResponse(200, "{\"code\":0}");
        ok.Succeeded.Should().BeTrue();
        ok.Retryable.Should().BeFalse();

        var busy = TikTokApiUploadService.InspectAddVideoResponse(500, "{\"code\":500,\"message\":\"busy\"}");
        busy.Succeeded.Should().BeFalse();
        busy.Retryable.Should().BeTrue();
        busy.Error.Should().Contain("HTTP 500");

        var rejected = TikTokApiUploadService.InspectAddVideoResponse(200, "{\"code\":4001,\"message\":\"重复\"}");
        rejected.Succeeded.Should().BeFalse();
        rejected.Retryable.Should().BeFalse();

        TikTokApiUploadService.InspectAddVideoResponse(0, "Failed to fetch").Retryable.Should().BeTrue();
        TikTokApiUploadService.InspectAddVideoResponse(200, "<html>").Retryable.Should().BeFalse();
    }

    [Fact]
    public void Account_api_upload_defaults()
    {
        var account = new TikTokAccountProfile();

        account.TiktokUploadApiConcurrency.Should().Be(3);
        account.TiktokUploadApiFallbackToBatch.Should().BeTrue();
        TikTokPublishOptions.FromAccount(account).UseApiUpload.Should().BeFalse();
    }
}
