using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouDraftSubmissionTests
{
    private const string SeriesId = "12345678";

    [Theory]
    [InlineData("\"PROCESSING\"")]
    [InlineData("2")]
    public async Task Inspection_is_read_only_and_lists_missing_failed_and_processing_episodes(string processingCode)
    {
        var gateway = new DraftGateway([1], """
            [{"episodeId":104,"orderNo":3,"videoStatus":2,"videoId":"stale"},
             {"episodeId":102,"orderNo":1,"videoUploadStatus":PROCESSING_CODE,"videoStatus":0,"videoId":"stale"},
             {"episodeId":101,"orderNo":0,"videoStatus":1}]
            """.Replace("PROCESSING_CODE", processingCode, StringComparison.Ordinal));

        var check = await KuaishouDraftSubmission.InspectAsync(gateway, SeriesId, 4, CancellationToken.None);

        Assert.Equal(3, check.OnlineEpisodes);
        Assert.Equal(1, check.ReadyEpisodes);
        Assert.Equal(new[] { 2 }, check.ProcessingEpisodes);
        Assert.Equal(new[] { 3, 4 }, check.MissingOrFailedEpisodes);
        Assert.False(check.EpisodeLayoutMatches);
        Assert.False(check.CanSubmit);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task Duplicate_episode_ids_cannot_satisfy_the_bound_episode_count()
    {
        var gateway = new DraftGateway([1], """
            [{"episodeId":101,"orderNo":0,"videoStatus":1},{"episodeId":101,"orderNo":1,"videoStatus":1}]
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KuaishouDraftSubmission.SubmitAsync(gateway, SeriesId, 2, CancellationToken.None));
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_rechecks_the_draft_and_confirms_review_without_resending(bool loseResponse)
    {
        var gateway = new DraftGateway([1, 2], """
            [{"episodeId":102,"orderNo":1,"videoStatus":1},
             {"episodeId":"101","orderNo":"0","videoUploadStatus":2}]
            """) { DropSubmitResponse = loseResponse };

        Assert.True(await KuaishouDraftSubmission.SubmitAsync(gateway, SeriesId, 2, CancellationToken.None));
        Assert.Equal(1, gateway.SubmitCalls);
        Assert.Equal(2, gateway.StatusCalls);
        Assert.Equal(1, gateway.DetailCalls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task An_existing_review_state_never_submits_again(int auditStatus)
    {
        var gateway = new DraftGateway([auditStatus], "[]");
        Assert.False(await KuaishouDraftSubmission.SubmitAsync(gateway, SeriesId, 2, CancellationToken.None));
        Assert.Equal(0, gateway.SubmitCalls);
        Assert.Equal(0, gateway.DetailCalls);
    }

    [Fact]
    public async Task A_lost_response_with_pending_audit_remains_unconfirmed()
    {
        var gateway = new DraftGateway([1, 1, 1, 1], """
            [{"episodeId":101,"orderNo":0,"videoStatus":1}]
            """) { DropSubmitResponse = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KuaishouDraftSubmission.SubmitAsync(gateway, SeriesId, 1, CancellationToken.None));

        Assert.Contains("提审结果未确认", error.Message);
        Assert.Equal(1, gateway.SubmitCalls);
        Assert.Equal(4, gateway.StatusCalls);
    }

    [Fact]
    public async Task A_draft_missing_from_the_current_account_is_never_submitted()
    {
        var gateway = new DraftGateway([1], "[]") { ListedId = "87654321" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KuaishouDraftSubmission.SubmitAsync(gateway, SeriesId, 1, CancellationToken.None));
        Assert.Contains("当前登录账号未找到绑定短剧", error.Message);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    private sealed class DraftGateway(int[] statuses, string episodes) : IKuaishouKdjGateway
    {
        public bool DropSubmitResponse { get; init; }
        public string ListedId { get; init; } = SeriesId;
        public int SubmitCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int DetailCalls { get; private set; }

        public Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(body);
            if (url == KuaishouShelfApi.ListUrl)
            {
                Assert.Equal(SeriesId, request.RootElement.GetProperty("seriesIdList")[0].GetString());
                var status = statuses[StatusCalls++];
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    result = 1,
                    data = new { data = new[] { new { miniSeriesId = ListedId, auditStatus = status, courseName = "测试草稿" } } },
                }));
            }
            Assert.Equal(SeriesId, request.RootElement.GetProperty("miniSeriesId").ToString());
            if (url == KuaishouApiVideoUploader.DetailUrl)
            {
                DetailCalls++;
                return Task.FromResult("{\"result\":1,\"data\":{\"episodeList\":" + episodes + "}}");
            }
            Assert.Equal(KuaishouApiVideoUploader.SubmitUrl, url);
            SubmitCalls++;
            return DropSubmitResponse
                ? Task.FromException<string>(new HttpRequestException("模拟响应丢失"))
                : Task.FromResult("""{"result":1}""");
        }

        public Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("草稿检查或仅提审不应上传本地文件。");
    }
}
