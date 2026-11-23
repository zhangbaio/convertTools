using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokApiUploadServiceTests
{
    private static readonly TikTokVodSigner.Credentials FakeCredentials = new("AKFAKE", "SKFAKE", "TOKENFAKE");

    [Theory]
    [InlineData("7", 7)]
    [InlineData("0", 0)]
    [InlineData("Sat, 21 Nov 2026 10:00:12 GMT", 12)]
    [InlineData("Sat, 21 Nov 2026 09:59:59 GMT", 0)]
    [InlineData("invalid", null)]
    [InlineData(null, null)]
    public void Retry_after_accepts_seconds_and_http_dates(string? value, int? seconds)
    {
        var now = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        TikTokApiUploadService.ParseRetryAfter(value, now)
            .Should().Be(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null);
    }

    [Fact]
    public async Task Saving_series_retries_the_same_patch_using_retry_after_then_backoff()
    {
        var page = CreateSaveSeriesPage(
            (500, """{"code":500,"message":"gateway error","statusMessage":"[RemoteRPCError]: [1606] Function route has reached max concurrency limiter"}""", "7"),
            (200, """{"code":"1606"}""", "invalid"),
            (500, """{"code":500,"statusMessage":"Function route has reached max concurrency limiter"}""", null),
            (429, "Too Many Requests", null),
            (200, """{"code":0}""", null));
        var waits = new List<TimeSpan>();

        await SaveSeriesAsync(page, 4, waitAsync: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });

        waits.Select(wait => wait.TotalSeconds).Should().Equal(7, 15, 30, 60);
        var requests = ((SaveSeriesPage)(object)page).Requests;
        requests.Should().HaveCount(5);
        requests.Select(request => request.Method).Distinct().Should().Equal("PATCH");
        requests.Select(request => request.Path).Distinct().Should().Equal("/api/content-partner/series/1234567890123456789");
        requests.Select(request => request.Body).Distinct().Should().ContainSingle();
    }

    [Theory]
    [InlineData(1606, 2)]
    [InlineData(500, 1)]
    public async Task Saving_series_stops_at_the_retry_limit_and_does_not_retry_other_errors(
        int code, int expectedRequests)
    {
        var body = JsonSerializer.Serialize(new { code });
        var page = CreateSaveSeriesPage((500, body, null), (500, body, null));
        var waits = new List<TimeSpan>();

        Func<Task> save = () => SaveSeriesAsync(page, 1, waitAsync: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*code {code}*");
        ((SaveSeriesPage)(object)page).Requests.Should().HaveCount(expectedRequests);
        waits.Should().Equal(Enumerable.Repeat(TimeSpan.FromSeconds(3), expectedRequests - 1));
    }

    [Fact]
    public async Task Reading_series_retries_rate_limits_on_the_same_draft_and_records_request_details()
    {
        var page = CreateSaveSeriesPage(
            (500, """{"code":500,"statusMessage":"[RemoteRPCError]: [1606] Function route has reached max concurrency limiter","extra":{"logId":"test-read-log"}}""", "11"),
            (429, "Too Many Requests", null),
            (200, """{"code":0,"data":{"collection":{"title":"Existing draft"}}}""", null));
        var waits = new List<TimeSpan>();
        var logs = new List<string>();

        var collection = await TikTokApiUploadService.ReadCollectionAsync(
            page, "1234567890123456789", "读取草稿发布设置", 3, logs.Add, CancellationToken.None,
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

        collection!["title"]!.GetValue<string>().Should().Be("Existing draft");
        waits.Select(wait => wait.TotalSeconds).Should().Equal(11, 15);
        var requests = ((SaveSeriesPage)(object)page).Requests;
        requests.Should().HaveCount(3).And.OnlyContain(request =>
            request.Method == "GET" && request.Path == "/api/content-partner/series/1234567890123456789" && request.Body == null);
        logs[0].Should().Contain("读取草稿发布设置，GET").And.Contain("HTTP 500，code 500，logId=test-read-log")
            .And.Contain("并发限流 1606").And.Contain("Retry-After 等待 11 秒").And.Contain("（1/3）");
    }

    [Fact]
    public async Task Reading_series_retains_the_last_platform_error_when_the_retry_budget_is_exhausted()
    {
        var page = CreateSaveSeriesPage(
            (500, """{"code":1606}""", null),
            (500, """{"code":1606,"extra":{"logId":"last-read-log"}}""", null));
        var logs = new List<string>();
        Func<Task> read = () => TikTokApiUploadService.ReadCollectionAsync(
            page, "1234567890123456789", "读取草稿发布设置", 1, logs.Add, CancellationToken.None,
            (_, _) => Task.CompletedTask);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GET*last-read-log*重试已用尽*");
        ((SaveSeriesPage)(object)page).Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Stopping_during_series_save_backoff_prevents_another_patch()
    {
        var page = CreateSaveSeriesPage((500, """{"code":1606}""", "30"));
        using var stop = new CancellationTokenSource();

        Func<Task> save = () => SaveSeriesAsync(page, 3, stop.Token, log: message =>
        {
            if (message.Contains("重试草稿", StringComparison.Ordinal))
                stop.Cancel();
        });

        await save.Should().ThrowAsync<OperationCanceledException>();
        ((SaveSeriesPage)(object)page).Requests.Should().ContainSingle();
    }

    private static Task SaveSeriesAsync(
        IPage page,
        int maxRetries,
        CancellationToken ct = default,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? waitAsync = null) =>
        TikTokApiUploadService.WriteSeriesAsync(
            page, "1234567890123456789", null, new TikTokApiSeriesShell { Title = "Draft title" },
            "写入剧集资料", maxRetries, log, ct, waitAsync);

    private static IPage CreateSaveSeriesPage(params (int Status, string Text, string? RetryAfter)[] replies)
    {
        var page = DispatchProxy.Create<IPage, SaveSeriesPage>();
        ((SaveSeriesPage)(object)page).Replies = new Queue<(int, string, string?)>(replies);
        return page;
    }

    public class SaveSeriesPage : DispatchProxy
    {
        public Queue<(int Status, string Text, string? RetryAfter)> Replies { get; set; } = new();
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPage.EvaluateAsync))
                throw new NotSupportedException(targetMethod?.Name);

            var request = (object[])args![1]!;
            if (request is [string path, string[] bodies])
            {
                var results = bodies.Select(body =>
                {
                    Requests.Add(("POST", path, body));
                    var (status, text, retryAfter) = Replies.Dequeue();
                    return new { status, text, retryAfter };
                }).ToArray();
                return Task.FromResult(JsonSerializer.SerializeToElement(results));
            }

            Requests.Add(((string)request[0], (string)request[1], (string?)request[2]));
            var (status, text, retryAfter) = Replies.Dequeue();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { status, text, retryAfter }));
        }
    }

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
    public void Image_signature_uses_the_imagex_service()
    {
        var uri = TikTokApiUploadService.BuildImageApplyUri("https://vod.tiktokdramacenter.com", 123, "abc");
        var authorization = TikTokVodSigner.BuildAuthorization(
            "GET", uri, "20261114T113000Z", FakeCredentials, null, "imagex");

        authorization.Should().Contain("Credential=AKFAKE/20261114/ap-singapore-1/imagex/aws4_request");
    }

    [Fact]
    public void Image_upload_uses_the_imagex_version_on_the_vod_host()
    {
        var apply = TikTokApiUploadService.BuildImageApplyUri("https://vod.tiktokdramacenter.com", 123, "abc");
        apply.Host.Should().Be("vod.tiktokdramacenter.com");
        apply.Query.Should().Contain("Action=ApplyImageUpload");
        apply.Query.Should().Contain("Version=2018-08-01");
        apply.Query.Should().Contain("ServiceId=xbj99bix8q");
        apply.Query.Should().NotContain("2020-11-19");

        var commit = TikTokApiUploadService.BuildImageCommitUri("https://vod.tiktokdramacenter.com");
        commit.Query.Should().Contain("Action=CommitImageUpload");
        commit.Query.Should().Contain("Version=2018-08-01");
    }

    [Fact]
    public void Image_apply_reads_the_upload_address()
    {
        using var document = JsonDocument.Parse(
            """
            {"Result":{"UploadAddress":{
              "SessionKey":"session-1",
              "UploadHosts":["tos-imagex.example.com"],
              "StoreInfos":[{"StoreUri":"tos-alisg-i-xbj99bix8q-sg/cover","Auth":"auth-1"}]
            }}}
            """);

        var ticket = TikTokApiUploadService.ReadImageUploadTicket(
            document.RootElement,
            "https://tos.tiktokdramacenter.com");

        ticket.SessionKey.Should().Be("session-1");
        ticket.StoreUri.Should().Be("tos-alisg-i-xbj99bix8q-sg/cover");
        ticket.Auth.Should().Be("auth-1");
        ticket.UploadHost.Should().Be("https://tos-imagex.example.com");
        ticket.UploadHosts.Should().Equal("https://tos-imagex.example.com");
    }

    [Fact]
    public void Image_apply_keeps_every_distinct_upload_host()
    {
        using var document = JsonDocument.Parse(
            """
            {"Result":{"UploadAddress":{
              "SessionKey":"session-1",
              "UploadHosts":["tos-a.example.com","https://tos-b.example.com/","tos-a.example.com"],
              "StoreInfos":[{"StoreUri":"tos-alisg-i-xbj99bix8q-sg/cover","Auth":"auth-1"}]
            }}}
            """);

        var ticket = TikTokApiUploadService.ReadImageUploadTicket(
            document.RootElement,
            "https://tos.tiktokdramacenter.com");

        ticket.UploadHost.Should().Be("https://tos-a.example.com");
        ticket.UploadHosts.Should().Equal(
            "https://tos-a.example.com",
            "https://tos-b.example.com");
        TikTokApiUploadService.DescribeUploadHost(ticket.UploadHosts[1]).Should().Be("tos-b.example.com");
    }

    [Fact]
    public void Domestic_image_upload_tries_drama_center_hosts_before_the_returned_node()
    {
        var hosts = TikTokApiUploadService.OrderDomesticImageUploadHosts(
        [
            "tos-my216-up.tiktokcdn.com",
            "https://tos.tiktokdramacenter.com/",
            "https://vod.tiktokdramacenter.com",
        ]);

        hosts.Should().Equal(
            "https://tos.tiktokdramacenter.com",
            "https://vod.tiktokdramacenter.com",
            "https://tos-my216-up.tiktokcdn.com");
    }

    [Fact]
    public void Image_ticket_rejection_switches_host_without_looking_like_a_transport_failure()
    {
        var rejected = new InvalidOperationException("TOS 上传失败（HTTP 403，code 4003）：auth");

        TikTokApiUploadService.IsImageTicketRejected(rejected).Should().BeTrue();
        TikTokApiUploadService.IsTransportClosedFailure(rejected).Should().BeFalse();
        TikTokApiUploadService.IsImageTicketRejected(
            new HttpRequestException("由于连接方在一段时间后没有正确答复或连接的主机没有反应"))
            .Should().BeFalse();
    }

    [Fact]
    public void Transport_closed_failure_is_recognized_from_the_ssl_eof()
    {
        var failure = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));

        TikTokApiUploadService.IsTransportClosedFailure(failure).Should().BeTrue();
        TikTokApiUploadService.IsTransportClosedFailure(new InvalidOperationException("TOS 上传失败"))
            .Should().BeFalse();
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
    public void Api_site_defaults_to_domestic_and_international_uses_singapore_upload_hosts()
    {
        TikTokApiSites.Normalize(null).Should().Be(TikTokApiSites.Domestic);
        TikTokApiSites.Normalize(" ").Should().Be(TikTokApiSites.Domestic);
        TikTokApiSites.Normalize("somewhere").Should().Be(TikTokApiSites.Domestic);
        TikTokApiSites.Normalize("INTERNATIONAL").Should().Be(TikTokApiSites.International);

        var account = new TikTokAccountProfile();
        account.TiktokApiSite.Should().Be(TikTokApiSites.Domestic);
        TikTokPublishOptions.FromAccount(account).ApiSite.Should().Be(TikTokApiSites.Domestic);

        TikTokApiSites.Origin(TikTokApiSites.Domestic).Should().Be("https://www.tiktokdramacenter.com");
        TikTokApiSites.VideoApplyBase(TikTokApiSites.Domestic).Should().Be("https://vod.tiktokdramacenter.com");
        TikTokApiSites.VideoObjectFallback(TikTokApiSites.Domestic).Should().Be("https://tos.tiktokdramacenter.com");
        TikTokApiSites.ImageApplyBase(TikTokApiSites.Domestic).Should().Be("https://vod.tiktokdramacenter.com");

        TikTokApiSites.Origin(TikTokApiSites.International).Should().Be("https://drama.tiktok.com");
        TikTokApiSites.LoginUrl(TikTokApiSites.International).Should().Be("https://drama.tiktok.com/login");
        TikTokApiSites.VideoApplyBase(TikTokApiSites.International).Should().Be("https://vod-upload-sg.tiktok.com");
        TikTokApiSites.ImageApplyBase(TikTokApiSites.International).Should().Be("https://imagex-upload-sg.tiktok.com");
        TikTokApiUploadService.ResolveServiceBaseUrl("https://drama.tiktok.com/series/draft/1", "vod")
            .Should().Be("https://vod-upload-sg.tiktok.com");
        TikTokApiUploadService.ResolveServiceBaseUrl("https://drama.tiktok.com/series/draft/1", "imagex")
            .Should().Be("https://imagex-upload-sg.tiktok.com");

        TikTokApiSites.AlignConsoleUrl(
                "https://www.tiktokdramacenter.com/series/draft/7706784720234976308",
                TikTokApiSites.International)
            .Should().Be("https://drama.tiktok.com/series/draft/7706784720234976308");
        TikTokUploadStateStore.NormalizeSeriesDraftDetailUrl("https://drama.tiktok.com/series/7706784720234976308")
            .Should().Be("https://drama.tiktok.com/series/draft/7706784720234976308");
        TikTokApiSites.IsLoginUrl("https://drama.tiktok.com/login").Should().BeTrue();
        TikTokApiSites.IsLoggedInUrl("https://drama.tiktok.com/series/list").Should().BeTrue();
        TikTokApiSites.IsLoggedInUrl("https://www.tiktok.com/").Should().BeFalse();
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
    public void Delete_plan_keeps_the_first_copy_of_each_episode()
    {
        var videos = new TikTokApiUploadService.SeriesVideo[]
        {
            new("ep1", 1, "剧-第1集", 1),
            new("ep2a", 2, "剧-第2集", 2),
            new("ep2b", 3, "剧-第3集-第2集", 2),
            new("ep3a", 4, "剧-第3集", 3),
            new("ep3b", 5, "剧-第3集", 3),
            new("ep4a", 6, "剧-第4集", 4),
            new("ep4b", 7, "剧-第4集", 4),
            new("ep8", 8, "剧-第8集", 8),
            new("blank", 9, "花絮", null),
        };

        TikTokApiUploadService.PlanDeleteExtras(videos).Should().Equal("ep2b", "ep3b", "ep4b", "blank");
        TikTokApiUploadService.PlanDeleteExtras(videos.Take(2).ToArray()).Should().BeEmpty();
    }

    [Fact]
    public void Hidden_husband_draft_deletes_duplicate_copies_and_names_the_gap()
    {
        var videos = new List<TikTokApiUploadService.SeriesVideo>
        {
            new("1", 1, "第1集", 1),
            new("2a", 2, "第2集", 2),
            new("2b", 3, "第2集", 2),
            new("3a", 4, "第3集", 3),
            new("3b", 5, "第3集", 3),
            new("4a", 6, "第4集", 4),
            new("4b", 7, "第4集", 4),
        };
        for (var episode = 8; episode <= 50; episode++)
            videos.Add(new TikTokApiUploadService.SeriesVideo(episode.ToString(), videos.Count + 1, $"第{episode}集", episode));

        TikTokApiUploadService.PlanDeleteExtras(videos).Should().Equal("2b", "3b", "4b");
        TikTokApiUploadService.DescribeEpisodeMismatch(videos, 50)
            .Should().Contain("缺少第 5, 6, 7 集")
            .And.Contain("第 2, 3, 4 集重复");

        var repaired = TikTokApiUploadService.SelectUsableExisting(videos).ToList();
        for (var episode = 5; episode <= 7; episode++)
            repaired.Add(new TikTokApiUploadService.SeriesVideo(episode.ToString(), repaired.Count + 1, $"第{episode}集", episode));
        TikTokApiUploadService.DescribeEpisodeMismatch(repaired, 50).Should().BeNull();
    }

    [Fact]
    public void Submit_check_names_missing_duplicate_and_unreadable_rows()
    {
        TikTokApiUploadService.DescribeEpisodeMismatch(
        [
            new("a", 1, "", 1),
            new("b", 2, "", 2),
            new("b2", 3, "", 2),
            new("c", 4, "", 4),
            new("blank", 5, "花絮", null),
        ], 4).Should().Be(
            "接口直传：正片和总集数不一致（正片 5 条，总集数 4），已停止，不会提交。缺少第 3 集；第 2 集重复；1 行读不出集号。");

        TikTokApiUploadService.DescribeEpisodeMismatch(
        [
            new("a", 1, "", 1),
            new("z", 2, "", 9),
        ], 1).Should().Contain("集号超出总集数：第 9 集");

        TikTokApiUploadService.DescribeEpisodeMismatch(
        [
            new("a", 1, "", 1),
            new("b", 2, "", 2),
            new("c", 3, "", 3),
        ], 3).Should().BeNull();
    }

    [Fact]
    public void Batch_delete_body_sends_contract_and_video_ids()
    {
        using var document = JsonDocument.Parse(
            TikTokApiUploadService.BuildBatchDeleteBody("7707846602790065160", ["v2", "v3"]));

        var root = document.RootElement;
        root.GetProperty("contractId").GetString().Should().Be("7707846602790065160");
        root.GetProperty("videoIds").EnumerateArray().Select(item => item.GetString()).Should().Equal("v2", "v3");
    }

    [Fact]
    public void Series_detail_reads_numeric_video_ids()
    {
        using var document = JsonDocument.Parse(
            """
            {"videos":[{"videoId":7707846602790065161,"orderNum":2,"title":"The Hidden Husband-第3集-第2集"}]}
            """);

        var videos = TikTokApiUploadService.ParseSeriesVideos(document.RootElement);

        videos.Should().ContainSingle();
        videos[0].VideoId.Should().Be("7707846602790065161");
        videos[0].Episode.Should().Be(2);
        TikTokApiUploadService.PlanDeleteExtras(videos).Should().BeEmpty();
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
            TiktokUploadApiConcurrency = 6,
            TiktokUploadStallSeconds = 45,
            TiktokUploadApiFallbackToBatch = false,
            TiktokProxyEnabled = true,
            TiktokProxyHost = "127.0.0.1",
            TiktokProxyPort = 7890,
        };

        var options = TikTokPublishOptions.FromAccount(account);

        options.UseApiUpload.Should().BeTrue();
        options.UseBatchUpload.Should().BeFalse();
        options.UploadApiConcurrency.Should().Be(3);
        options.UploadStallSeconds.Should().Be(180);
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
    public void Add_video_response_distinguishes_transient_failures_from_rejections()
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
        TikTokApiUploadService.InspectAddVideoResponse(429, "Too Many Requests").Retryable.Should().BeTrue();
        TikTokApiUploadService.InspectAddVideoResponse(200, """{"code":500,"message":""}""").Retryable.Should().BeTrue();
        TikTokApiUploadService.InspectAddVideoResponse(200, """{"code":"500"}""").Retryable.Should().BeTrue();
        TikTokApiUploadService.InspectAddVideoResponse(403, """{"code":500}""").Retryable.Should().BeFalse();
        TikTokApiUploadService.InspectAddVideoResponse(200, "<html>").Retryable.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mount_retry_checks_the_draft_and_reuses_the_original_request_only_if_still_missing(bool alreadyMounted)
    {
        var page = alreadyMounted
            ? CreateSaveSeriesPage((200, MountBusy, "7"), (200, MountOk, null), (200, MountOk, null),
                (200, DraftEpisodes(38, 39, 40), null))
            : CreateSaveSeriesPage((200, MountBusy, "7"), (200, MountOk, null), (200, MountOk, null),
                (200, DraftEpisodes(38, 40), null), (200, MountOk, null));
        var mounted = new List<int>();
        var waits = new List<TimeSpan>();
        var request = CreateEpisodeMount(39);

        var failed = await TikTokApiUploadService.MountEpisodeBatchAsync(page,
            [request, CreateEpisodeMount(38)], mounted.Add, CancellationToken.None);
        await TikTokApiUploadService.MountEpisodeBatchAsync(page,
            [CreateEpisodeMount(40)], mounted.Add, CancellationToken.None);
        var unresolved = await TikTokApiUploadService.RetryEpisodeMountsAsync(page, MountSeriesId, failed,
            3, 3, mounted.Add, null, CancellationToken.None, (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

        unresolved.Should().BeEmpty();
        mounted.Should().Equal(38, 40, 39);
        waits.Should().Equal(TimeSpan.FromSeconds(7));
        var requests = ((SaveSeriesPage)(object)page).Requests;
        requests.Select(item => item.Method)
            .Should().Equal(alreadyMounted
                ? new[] { "POST", "POST", "POST", "GET" }
                : new[] { "POST", "POST", "POST", "GET", "POST" });
        requests.Count(item => item.Body == request.Body).Should().Be(alreadyMounted ? 1 : 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mount_retry_reconciles_the_last_attempt_and_preserves_completed_episodes(bool mountedOnLastAttempt)
    {
        var page = CreateSaveSeriesPage((200, MountBusy, null), (200, MountOk, null),
            (200, DraftEpisodes(40), null), (200, MountBusy, null),
            (200, DraftEpisodes(40), null), (200, MountBusy, null),
            (200, mountedOnLastAttempt ? DraftEpisodes(39, 40) : DraftEpisodes(40), null));
        var mounted = new List<int>();
        var waits = new List<TimeSpan>();
        var request = CreateEpisodeMount(39);

        var failed = await TikTokApiUploadService.MountEpisodeBatchAsync(page,
            [request, CreateEpisodeMount(40)], mounted.Add, CancellationToken.None);
        var unresolved = await TikTokApiUploadService.RetryEpisodeMountsAsync(page, MountSeriesId, failed,
            3, 3, mounted.Add, null, CancellationToken.None, (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

        if (mountedOnLastAttempt)
            unresolved.Should().BeEmpty();
        else
            unresolved.Should().ContainSingle().Which.Request.Should().Be(request);
        mounted.Should().Equal(mountedOnLastAttempt ? new[] { 40, 39 } : new[] { 40 });
        waits.Select(delay => delay.TotalSeconds).Should().Equal(2, 4);
        var requests = ((SaveSeriesPage)(object)page).Requests;
        requests.Count(item => item.Body == request.Body).Should().Be(3);
        requests.Count(item => item.Body == CreateEpisodeMount(40).Body).Should().Be(1);
        requests.Should().OnlyContain(item => item.Method == "GET" || item.Method == "POST");
    }

    [Theory]
    [InlineData("""{"code":0,"data":{}}""")]
    [InlineData("""{"code":0,"data":{"videos":[{"title":"第39集"}]}}""")]
    [InlineData("""{"code":0,"data":{"videos":[{"title":"第39集","videoId":"row-1"},{"title":"第39集","videoId":"row-2"}]}}""")]
    public async Task Mount_retry_stops_when_the_draft_list_cannot_confirm_the_missing_episode(string draft)
    {
        var page = CreateSaveSeriesPage((200, MountBusy, null), (200, draft, null));
        var failed = await TikTokApiUploadService.MountEpisodeBatchAsync(page,
            [CreateEpisodeMount(39)], _ => { }, CancellationToken.None);
        Func<Task> retry = () => TikTokApiUploadService.RetryEpisodeMountsAsync(page, MountSeriesId, failed,
            3, 3, _ => { }, null, CancellationToken.None, (_, _) => Task.CompletedTask);
        await retry.Should().ThrowAsync<InvalidOperationException>();
        ((SaveSeriesPage)(object)page).Requests.Select(item => item.Method).Should().Equal("POST", "GET");
    }

    [Fact]
    public async Task Mount_retry_honors_cancellation_before_read_or_resend()
    {
        var page = CreateSaveSeriesPage((200, MountBusy, null));
        var failed = await TikTokApiUploadService.MountEpisodeBatchAsync(page,
            [CreateEpisodeMount(39)], _ => { }, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Func<Task> retry = () => TikTokApiUploadService.RetryEpisodeMountsAsync(page, MountSeriesId, failed,
            3, 3, _ => { }, null, stop.Token, (_, token) =>
            {
                stop.Cancel();
                return Task.FromCanceled(token);
            });
        await retry.Should().ThrowAsync<OperationCanceledException>();
        ((SaveSeriesPage)(object)page).Requests.Should().ContainSingle();
    }

    private const string MountSeriesId = "1234567890123456789";
    private const string MountOk = """{"code":0}""";
    private const string MountBusy = """{"code":500,"message":""}""";

    private static TikTokApiUploadService.EpisodeMount CreateEpisodeMount(int episode) =>
        new(episode, TikTokApiUploadService.BuildAddVideoBody(
            MountSeriesId, new($"vod-{episode}", 2000, "cover"), $"第{episode}集", episode));

    private static string DraftEpisodes(params int[] episodes) => JsonSerializer.Serialize(new
    {
        code = 0,
        data = new
        {
            videos = episodes.Select(episode => new { videoId = $"row-{episode}", title = $"第{episode}集", orderNum = episode })
        }
    });

    [Fact]
    public void Account_api_upload_defaults()
    {
        var account = new TikTokAccountProfile();

        account.TiktokUploadApiConcurrency.Should().Be(3);
        account.TiktokUploadApiFallbackToBatch.Should().BeFalse();
        var options = TikTokPublishOptions.FromAccount(account);
        options.UseApiUpload.Should().BeTrue();
        options.UploadApiFallbackToBatch.Should().BeFalse();

        var savedFallback = new TikTokAccountProfile { TiktokUploadApiFallbackToBatch = true };
        TikTokPublishOptions.FromAccount(savedFallback).UploadApiFallbackToBatch.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(200, false)]
    [InlineData(400, false)]
    public void Transport_status_retries_only_network_and_server_errors(int status, bool retryable)
    {
        TikTokApiUploadService.IsTransientHttpStatus(status).Should().Be(retryable);
    }

    [Theory]
    [InlineData("{\"data\":{\"contractId\":\"7707846602790065160\"}}", "7707846602790065160")]
    [InlineData("{\"id\":7707843447088124935}", "7707843447088124935")]
    [InlineData("{\"data\":{\"videos\":[{\"id\":\"1\"}]}}", null)]
    [InlineData("not json", null)]
    public void Series_id_is_read_from_the_create_response(string json, string? expected)
    {
        TikTokApiUploadService.ReadSeriesIdFromJson(json).Should().Be(expected);
    }

    [Fact]
    public void Existing_draft_id_comes_from_the_series_list()
    {
        const string title = "重生赶海的温柔守护";
        var json = """
            {"code":0,"data":{"list":[
              {"title":"鱼塘边的诚信路","seriesId":"7707000000000000001","presentationStatus":31},
              {"title":"重生赶海的温柔守护","seriesId":"7707915752204129212","presentationStatus":31},
              {"title":"重生赶海的温柔守护","id":"7708000000000000001","orderNum":3,"videoId":"v"}
            ]}}
            """;

        TikTokApiUploadService.FindDraftSeries(json, title).SelectIds()
            .Should().Equal("7707915752204129212");
    }

    [Fact]
    public void Existing_draft_id_is_read_from_a_nested_series()
    {
        var json = """
            {"data":{"records":[{
              "presentationStatus":"草稿",
              "seriesInfo":{"title":"重生赶海的温柔守护","id":"7707915752204129212"}
            }]}}
            """;

        TikTokApiUploadService.FindDraftSeries(json, "  重生赶海的温柔守护  ").SelectIds()
            .Should().Equal("7707915752204129212");
    }

    [Fact]
    public void New_shell_with_collection_status_is_still_a_draft()
    {
        var json = """
            {"data":{"list":[{
              "info":{"contractId":"7707984308811699206","title":"大婚日仙尊母亲归来","status":1},
              "presentationStatus":{"status":31}
            }]}}
            """;

        TikTokApiUploadService.FindDraftSeries(json, "大婚日仙尊母亲归来").SelectIds()
            .Should().Equal("7707984308811699206");
    }

    [Fact]
    public void Published_series_is_not_reused_as_a_draft()
    {
        var json = """
            {"data":{"list":[
              {"title":"重生赶海的温柔守护","seriesId":"7707915752204129212","presentationStatus":1005}
            ]}}
            """;

        TikTokApiUploadService.FindDraftSeries(json, "重生赶海的温柔守护").SelectIds()
            .Should().BeEmpty();
    }

    [Fact]
    public void Several_same_title_drafts_stay_unresolved()
    {
        var json = """
            {"data":{"list":[
              {"title":"重生赶海的温柔守护","seriesId":"7707915752204129212","presentationStatus":31},
              {"title":"重生赶海的温柔守护","seriesId":"7707915752204129999","status":"InDraft"}
            ]}}
            """;

        var ids = TikTokApiUploadService.FindDraftSeries(json, "重生赶海的温柔守护").SelectIds();
        ids.Should().Equal("7707915752204129212", "7707915752204129999");
    }

    [Fact]
    public void Untagged_list_row_is_used_when_the_response_has_no_status()
    {
        var json = """
            {"data":{"list":[{"seriesTitle":"重生赶海的温柔守护","contractId":"7707915752204129212"}]}}
            """;

        TikTokApiUploadService.FindDraftSeries(json, "重生赶海的温柔守护").SelectIds()
            .Should().Equal("7707915752204129212");
    }

    [Fact]
    public void User_price_matches_the_per_episode_option_closest_to_the_episode_count()
    {
        var options = new[]
        {
            "$13.63 | 每集$0.30",
            "$13.64 | 每集$0.30",
            "$15.00 | 每集$0.30",
            "$22.33 | 每集$0.49",
        };

        TikTokApiSeriesDocument.MatchPriceOption(options, "0.3", 50).Should().Be(2);
        TikTokApiSeriesDocument.MatchPriceOption(options, "$13.64", 50).Should().Be(1);
        TikTokApiSeriesDocument.ReadPrice("$0.30").Should().Be("0.3");
    }

    [Fact]
    public void Tos_part_retry_waits_longer_and_stops_at_thirty_seconds()
    {
        TikTokApiUploadService.TosPartAttempts.Should().Be(7);
        TikTokApiUploadService.ResolveMultipartInitAttempts(isVideo: false, configuredRetries: 3)
            .Should().Be(TikTokApiUploadService.TosPartAttempts);
        TikTokApiUploadService.ResolveMultipartInitAttempts(isVideo: true, configuredRetries: 3)
            .Should().Be(3);
        TikTokApiUploadService.TosPartRetryDelay(1).TotalSeconds.Should().Be(3);
        TikTokApiUploadService.TosPartRetryDelay(2).TotalSeconds.Should().Be(6);
        TikTokApiUploadService.TosPartRetryDelay(3).TotalSeconds.Should().Be(12);
        TikTokApiUploadService.TosPartRetryDelay(4).TotalSeconds.Should().Be(24);
        TikTokApiUploadService.TosPartRetryDelay(5).TotalSeconds.Should().Be(30);
        TikTokApiUploadService.TosPartRetryDelay(6).TotalSeconds.Should().Be(30);
    }

    [Fact]
    public void Gateway_timeout_log_omits_the_html_error_page()
    {
        var html = "<!DOCTYPE HTML PUBLIC \"-//IETF//DTD HTML 2.0//EN\"><html><head><title>504 Gateway Time-out</title></head><body></body></html>";
        TikTokApiUploadService.DescribeHttpFailure("TOS", 504, html)
            .Should().Be("TOS HTTP 504 Gateway Time-out");
        TikTokApiUploadService.DescribeHttpFailure("TOS", 502, html)
            .Should().Be("TOS HTTP 502");
    }

    [Fact]
    public void Transport_failure_keeps_the_inner_reason_on_one_line()
    {
        var failure = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("The handshake failed because the connection was closed."));

        TikTokApiUploadService.DescribeTransportFailure(failure)
            .Should().Be(
                "The SSL connection could not be established, see inner exception. " +
                "The handshake failed because the connection was closed.");
    }

    [Fact]
    public void Commit_without_id_or_duration_is_retried_on_the_same_ticket_then_reuploaded()
    {
        TikTokApiUploadService.CommitMetadataRetries.Should().Be(3);
        TikTokApiUploadService.CommitMetadataRetryDelay(1).TotalSeconds.Should().Be(3);
        TikTokApiUploadService.CommitMetadataRetryDelay(2).TotalSeconds.Should().Be(6);
        TikTokApiUploadService.CommitMetadataRetryDelay(3).TotalSeconds.Should().Be(12);

        using var complete = JsonDocument.Parse("""
            {"Vid":"v0200g","PosterUri":"tos-poster","VideoMeta":{"Duration":61.5}}
            """);
        var ready = TikTokApiUploadService.ReadCommitResult(complete.RootElement);
        ready.Missing.Should().BeEmpty();
        ready.Vid.Should().Be("v0200g");
        ready.DurationMs.Should().Be(61500);
        ready.PosterUri.Should().Be("tos-poster");

        using var noDuration = JsonDocument.Parse("""
            {"Vid":"v0200g","PosterUri":"tos-poster","VideoMeta":{"Duration":0}}
            """);
        TikTokApiUploadService.ReadCommitResult(noDuration.RootElement).Missing.Should().Be("时长");

        using var noId = JsonDocument.Parse("""{"Vid":"","VideoMeta":{"Duration":12}}""");
        TikTokApiUploadService.ReadCommitResult(noId.RootElement).Missing.Should().Be("视频 ID");

        using var neither = JsonDocument.Parse("""{"VideoMeta":{}}""");
        TikTokApiUploadService.ReadCommitResult(neither.RootElement).Missing.Should().Be("视频 ID 或时长");

        var incomplete = TikTokApiUploadService.IncompleteCommitFailure("时长");
        incomplete.Message.Should().Be("CommitUploadInner 没有返回时长。");
        TikTokApiUploadService.ShouldRetryEpisode(incomplete).Should().BeTrue();
        TikTokApiUploadService.ShouldRetryEpisode(new InvalidOperationException("TOS 上传失败")).Should().BeFalse();
    }

    [Fact]
    public void Part_list_mismatch_reuploads_the_episode_instead_of_retrying_the_same_address()
    {
        TikTokApiUploadService.IsPartListMismatch(4019, "Mismatch Part List").Should().BeTrue();
        TikTokApiUploadService.IsPartListMismatch(2000, "Success").Should().BeFalse();
        TikTokApiUploadService.IsPartListMismatch(-1, "mismatch part list").Should().BeTrue();

        var mismatch = TikTokApiUploadService.PartListMismatchFailure(200, 4019, "Mismatch Part List");
        mismatch.Message.Should().Be("TOS 上传失败（HTTP 200，code 4019）：Mismatch Part List");
        TikTokApiUploadService.ShouldRetryEpisode(mismatch).Should().BeTrue();
        TikTokApiUploadService.RetriesOnTheSameAddress(mismatch).Should().BeFalse();
        TikTokApiUploadService.RetriesOnTheSameAddress(
            TikTokApiUploadService.IncompleteCommitFailure("时长")).Should().BeTrue();
    }
}
