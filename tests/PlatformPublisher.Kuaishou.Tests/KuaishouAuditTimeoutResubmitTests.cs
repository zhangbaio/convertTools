using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouAuditTimeoutResubmitTests
{
    private const string Timeout = "您提交的内容审核超时，请您重新提交";
    private const string Mixed = Timeout + ";单集封面/标题审核不通过，请修改";
    private const string Ok = """{"result":1,"data":{}}""";

    [Fact]
    public async Task Scan_distinguishes_mixed_summary_from_an_independent_content_rejection()
    {
        var gateway = new Gateway((_, body) =>
        {
            using var query = JsonDocument.Parse(body);
            Assert.Equal(2, query.RootElement.GetProperty("sellingStatus").GetInt32());
            Assert.Equal(4, query.RootElement.GetProperty("auditStatus").GetInt32());
            return query.RootElement.GetProperty("pageNum").GetInt32() == 1
                ? Page(3, Row(41, Mixed))
                : query.RootElement.GetProperty("pageNum").GetInt32() == 2
                    ? Page(3, Row(42, Mixed, episodeReason: Timeout + "；单集标题侵权"))
                    : Page(3, Row(43, Timeout));
        });
        var results = await Service().ListAsync(gateway, null, CancellationToken.None, pageSize: 1);

        Assert.Equal(
            [KuaishouAuditTimeoutEligibility.MixedSummary, KuaishouAuditTimeoutEligibility.NeedsChanges,
             KuaishouAuditTimeoutEligibility.TimeoutOnly], results.Select(item => item.Eligibility));
        Assert.Equal("第 1 集", results[0].EpisodeLabel);
        Assert.False(results[1].CanSelect);
        Assert.All(gateway.Calls, call => Assert.Equal(KuaishouShelfApi.ListUrl, call.Url));
    }

    [Fact]
    public async Task Mixed_summary_can_be_submitted_individually_and_requires_platform_confirmation()
    {
        var gateway = SubmissionGateway(41);
        var targets = await Service().ListAsync(gateway, null, CancellationToken.None);
        var result = await Service().SubmitAsync(gateway, targets, null, CancellationToken.None);
        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.Accepted, Assert.Single(result).Status);

        var update = Assert.Single(gateway.Calls.Where(call => call.Url == KuaishouCommitmentResubmit.UpdateUrl));
        using var body = JsonDocument.Parse(update.Body);
        Assert.Equal("41", body.RootElement.GetProperty("miniSeriesId").GetString());
        Assert.Equal("原简介", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("original-cover", body.RootElement.GetProperty("coverImgKey").GetString());
        Assert.Equal(1001, body.RootElement.GetProperty("episodes")[0].GetProperty("episodeId").GetInt32());
        Assert.Equal("原分集标题", body.RootElement.GetProperty("episodes")[0].GetProperty("episodeTitle").GetString());
        Assert.Equal(KuaishouApiVideoUploader.SubmitUrl, gateway.Calls[^2].Url);
        Assert.Equal(KuaishouShelfApi.ListUrl, gateway.Calls[^1].Url);
    }

    [Fact]
    public async Task Mixed_summary_batch_and_changed_evidence_do_not_write()
    {
        var gateway = new Gateway((_, body) =>
        {
            using var query = JsonDocument.Parse(body);
            var ids = query.RootElement.GetProperty("seriesIdList");
            if (ids.GetArrayLength() == 0)
                return Page(2, Row(41, Mixed), Row(42, Mixed));
            var id = int.Parse(ids[0].GetString()!);
            return Page(1, Row(id, Mixed, episodeId: id == 42 ? 2002 : 1001));
        });

        var service = Service();
        var targets = await service.ListAsync(gateway, null, CancellationToken.None);
        var batch = await service.SubmitAsync(gateway, targets, null, CancellationToken.None);
        Assert.All(batch, item => Assert.Equal(KuaishouAuditTimeoutSubmitStatus.Skipped, item.Status));

        var changed = await service.SubmitAsync(gateway, [targets[1]], null, CancellationToken.None);
        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.Skipped, Assert.Single(changed).Status);
        Assert.All(gateway.Calls, call => Assert.Equal(KuaishouShelfApi.ListUrl, call.Url));
    }

    [Fact]
    public async Task An_independent_episode_rejection_blocks_update_and_submit()
    {
        var gateway = SubmissionGateway(41, comments: "\"单集封面不通过\"");
        var service = Service();
        var targets = await service.ListAsync(gateway, null, CancellationToken.None);
        var result = await service.SubmitAsync(gateway, targets, null, CancellationToken.None);

        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.Skipped, Assert.Single(result).Status);
        Assert.DoesNotContain(gateway.Calls, call =>
            call.Url == KuaishouCommitmentResubmit.UpdateUrl || call.Url == KuaishouApiVideoUploader.SubmitUrl);
    }

    [Fact]
    public async Task A_lost_submit_response_can_be_confirmed_without_sending_again()
    {
        var gateway = SubmissionGateway(41, loseSubmitResponse: true);
        var service = Service();
        var targets = await service.ListAsync(gateway, null, CancellationToken.None);
        var result = await service.SubmitAsync(gateway, targets, null, CancellationToken.None);

        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.Accepted, Assert.Single(result).Status);
        Assert.Single(gateway.Calls.Where(call => call.Url == KuaishouApiVideoUploader.SubmitUrl));
    }

    [Fact]
    public async Task Unconfirmed_submission_survives_restart_and_scan_can_reconcile_it()
    {
        var settings = Settings();
        var gateway = SubmissionGateway(41, loseSubmitResponse: true, confirm: false);
        var service = new KuaishouAuditTimeoutResubmitService(settings, "demo-account");
        var targets = await service.ListAsync(gateway, null, CancellationToken.None);
        var result = await service.SubmitAsync(gateway, targets, null, CancellationToken.None);
        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.PendingConfirmation, Assert.Single(result).Status);

        var restart = new KuaishouAuditTimeoutResubmitService(settings, "demo-account");
        var noPosts = new Gateway((_, _) => throw new InvalidOperationException("Unexpected request"));
        var repeat = await restart.SubmitAsync(noPosts, targets, null, CancellationToken.None);
        Assert.Equal(KuaishouAuditTimeoutSubmitStatus.PendingConfirmation, Assert.Single(repeat).Status);
        Assert.Empty(noPosts.Calls);

        var confirm = new Gateway((url, body) => body.Contains("\"41\"", StringComparison.Ordinal)
            ? Page(1, Row(41, "", audit: 2))
            : Page(0));
        var reconciled = await restart.ListAsync(confirm, null, CancellationToken.None);
        Assert.Empty(reconciled);

        var readingAgain = new KuaishouAuditTimeoutResubmitService(settings, "demo-account");
        var later = await readingAgain.ListAsync(new Gateway((_, _) => Page(1, Row(41, Timeout))), null,
            CancellationToken.None);
        Assert.False(Assert.Single(later).Pending);
        Assert.Single(gateway.Calls.Where(call => call.Url == KuaishouApiVideoUploader.SubmitUrl));
    }

    private static KuaishouAuditTimeoutResubmitService Service() => new(Settings(), "demo-account");

    private static AccountJsonSettingStore Settings() =>
        new(new PlatformDatabase(Path.Combine(Directory.CreateTempSubdirectory("kdj-timeout-").FullName, "app.db")));

    private static Gateway SubmissionGateway(
        int id, bool loseSubmitResponse = false, bool confirm = true, string comments = "null")
    {
        var sent = false;
        return new Gateway((url, _) =>
        {
            if (url == KuaishouShelfApi.ListUrl)
                return Page(1, Row(id, sent && confirm ? "" : Mixed, audit: sent && confirm ? 2 : 4));
            if (url == KuaishouCommitmentResubmit.BaseInfoUrl)
                return $$$"""{"result":1,"data":{"miniSeriesId":{{{id}}},"auditStatus":4,"sellingStatus":2,"canEdit":true,"courseName":"测试短剧","courseDescription":"原简介","coverImgKey":"original-cover"}}""";
            if (url == KuaishouCommitmentResubmit.EpisodeListUrl)
                return $$$"""{"result":1,"data":{"seriesList":[{"episodeId":1001,"orderNo":0,"episodeTitle":"原分集标题","coverImgKey":"episode-cover","videoReviewComments":{{{comments}}}}]}}""";
            if (url == KuaishouCommitmentResubmit.UpdateUrl)
                return """{"result":1,"data":{"canModifyVideo":true}}""";
            if (url == KuaishouApiVideoUploader.SubmitUrl)
            {
                sent = true;
                if (loseSubmitResponse)
                    throw new HttpRequestException("测试超时");
            }
            return Ok;
        });
    }

    private static string Page(int total, params object[] rows) =>
        JsonSerializer.Serialize(new { result = 1, data = new { totalCount = total, data = rows } });

    private static object Row(int id, string reason, string episodeReason = Timeout, int episodeId = 1001, int audit = 4) => new
    {
        miniSeriesId = id,
        courseName = $"测试短剧 {id}",
        sellingStatus = 2,
        auditStatus = audit,
        failReasonSummary = new { reviewReason = reason },
        checkReasonDisplayInfo = new
        {
            rejectReason = reason,
            rejectedEpisodeNoList = new[] { 0 },
            episodeDisplayInfos = new[] { new { episodeId, orderNo = 0, rejectReason = episodeReason } }
        }
    };

    private sealed class Gateway(Func<string, string, string> route) : IKuaishouKdjGateway
    {
        public List<(string Url, string Body)> Calls { get; } = [];

        public Task<string> PostJsonAsync(string url, string body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((url, body));
            return Task.FromResult(route(url, body));
        }

        public Task<string> GetAsync(string url, CancellationToken ct) => PostJsonAsync(url, "", ct);

        public Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken ct) =>
            throw new InvalidOperationException("重提不应上传文件。");
    }
}
