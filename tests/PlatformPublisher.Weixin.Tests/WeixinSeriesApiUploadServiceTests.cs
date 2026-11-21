using System.Text.Json;
using System.Text.Json.Nodes;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinSeriesApiUploadServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();
    private readonly RecordingClient _client = new();
    private string Project => _root.FullName;
    private string Config => Path.Combine(Project, "weixin-channel.json");
    private string Ledger => Path.Combine(Project, "ledger");

    [Fact]
    public async Task Resume_reuses_uploaded_files_across_jobs_and_reuploads_changed_file()
    {
        var job = Arrange();
        var service = CreateService();
        Assert.Equal(WeixinSeriesApiResult.Uploaded, await RunAsync(service, job));
        Assert.Equal(5, _client.Uploads.Count);
        Assert.Equal(0, _client.Submissions);

        _client.Uploads.Clear();
        File.AppendAllText(Path.Combine(Project, "第2集.mp4"), "changed");
        job.Id = Guid.NewGuid().ToString("N");
        Assert.Equal(WeixinSeriesApiResult.Uploaded, await RunAsync(CreateService(), job));
        Assert.Equal("第2集.mp4", Assert.Single(_client.Uploads));
    }

    [Fact]
    public async Task Unknown_submission_blocks_both_modes_until_operator_records_outcome()
    {
        var job = Arrange();
        var service = CreateService();
        _client.SubmitError = new TimeoutException();
        await Assert.ThrowsAsync<WeixinSeriesSubmissionPendingException>(() => RunAsync(service, job, submit: true));
        Assert.Equal(1, _client.Submissions);
        Assert.Throws<WeixinSeriesSubmissionPendingException>(() => service.OpenBrowserRun(job));

        _client.SubmitError = null;
        _client.Uploads.Clear();
        await Assert.ThrowsAsync<WeixinSeriesSubmissionPendingException>(() => RunAsync(CreateService(), job, submit: true));
        Assert.Equal(1, _client.Submissions);
        Assert.True(service.ResolveReview(job.AccountId, job.ProjectDirectory, submitted: false));
        Assert.Equal(WeixinSeriesApiResult.Submitted, await RunAsync(CreateService(), job, submit: true));
        Assert.Equal(WeixinSeriesApiResult.AlreadySubmitted, await RunAsync(CreateService(), job, submit: true));
        Assert.Throws<InvalidOperationException>(() => service.OpenBrowserRun(job));
        Assert.Empty(_client.Uploads);
        Assert.Equal(2, _client.Submissions);
    }

    [Fact]
    public async Task Explicit_rejection_releases_submission_guard_but_keeps_uploaded_material()
    {
        var job = Arrange();
        _client.SubmitError = new WeixinSeriesApiRejectedException("平台明确拒绝");
        await Assert.ThrowsAsync<WeixinSeriesApiRejectedException>(() => RunAsync(CreateService(), job, submit: true));
        _client.Uploads.Clear();
        _client.SubmitError = null;
        Assert.Equal(WeixinSeriesApiResult.Submitted, await RunAsync(CreateService(), job, submit: true));
        Assert.Equal(2, _client.Submissions);
        Assert.Empty(_client.Uploads);
    }

    [Fact]
    public async Task Authorizing_the_form_and_confirming_episodes_happens_before_any_upload()
    {
        var job = Arrange(total: 3);
        var service = CreateService();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(service, job, submit: true));
        Assert.Contains("连续", error.Message);
        Assert.Empty(_client.Uploads);

        Arrange();
        var cancelled = await service.RunAsync(job, Config, Options(submit: true), null, (confirmation, _) =>
        {
            Assert.Equal("测试账号", confirmation.Account);
            Assert.Equal("测试剧", confirmation.Title);
            Assert.Equal(2, confirmation.EpisodeCount);
            return Task.FromResult(false);
        }, CancellationToken.None);
        Assert.Equal(WeixinSeriesApiResult.Cancelled, cancelled);
        Assert.Empty(_client.Uploads);
        Assert.Equal(0, _client.Submissions);
    }

    [Fact]
    public async Task Partial_upload_uses_filename_episode_numbers_and_rejects_a_different_login_in_same_slot()
    {
        var job = Arrange(second: 3);
        var options = new WeixinPublishOptions
        {
            SeriesUploadMode = WeixinSeriesUploadMode.ApiUpload, EpisodeSelectionMode = "explicit", EpisodeIndexes = "3",
        };
        Assert.Equal(WeixinSeriesApiResult.Uploaded,
            await CreateService().RunAsync(job, Config, options, null, null, CancellationToken.None));
        Assert.Contains("第3集.mp4", _client.Uploads);
        Assert.DoesNotContain("第1集.mp4", _client.Uploads);

        _client.Uploads.Clear();
        SaveLogin(job.AccountSessionDirectory, "different-finder");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(CreateService(), job));
        Assert.Contains("已绑定", error.Message);
        Assert.Empty(_client.Uploads);
    }

    [Fact]
    public async Task Accounts_are_isolated_and_duplicate_slots_for_one_finder_share_submission_state()
    {
        var first = Arrange();
        Assert.Equal(WeixinSeriesApiResult.Submitted, await RunAsync(CreateService(), first, submit: true));
        _client.Uploads.Clear();
        var alias = Arrange(account: "slot-alias");
        Assert.Equal(WeixinSeriesApiResult.AlreadySubmitted, await RunAsync(CreateService(), alias, submit: true));
        Assert.Empty(_client.Uploads);
        var other = Arrange(account: "slot-other", finder: "distinct-finder");
        Assert.Equal(WeixinSeriesApiResult.Submitted, await RunAsync(CreateService(), other, submit: true));
        Assert.Equal(5, _client.Uploads.Count);
        Assert.Equal(2, _client.Submissions);
    }

    [Fact]
    public async Task Browser_review_can_be_resolved_before_enabling_interface_submission()
    {
        var job = Arrange();
        var review = new WeixinSeriesReviewStore(Path.Combine(Project, "reviews"));
        review.RecordReachedReview(job.AccountId, Project);
        await Assert.ThrowsAsync<WeixinSeriesSubmissionPendingException>(() => RunAsync(CreateService(), job, submit: true));
        Assert.Empty(_client.Uploads);
        Assert.True(CreateService().ResolveReview(job.AccountId, Project, submitted: true));
        Assert.Equal(WeixinSeriesApiResult.AlreadySubmitted, await RunAsync(CreateService(), job, submit: true));
        Assert.Throws<InvalidOperationException>(() => CreateService().OpenBrowserRun(job));
        Assert.Equal(0, _client.Submissions);
    }

    [Fact]
    public async Task Existing_browser_run_holds_the_same_account_lock()
    {
        var job = Arrange();
        using var browser = CreateService().OpenBrowserRun(job);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(CreateService(), job));
        Assert.Empty(_client.Uploads);
    }

    [Fact]
    public async Task Cancellation_during_submission_keeps_pending_record()
    {
        var job = Arrange();
        _client.SubmitError = new OperationCanceledException();
        await Assert.ThrowsAsync<WeixinSeriesSubmissionPendingException>(() => RunAsync(CreateService(), job, submit: true));
        Assert.Throws<WeixinSeriesSubmissionPendingException>(() => CreateService().OpenBrowserRun(job));
        Assert.True(CreateService().ResolveReview(job.AccountId, Project, submitted: true));
        Assert.Equal(WeixinSeriesApiResult.AlreadySubmitted, await RunAsync(CreateService(), job, submit: true));
        Assert.Equal(1, _client.Submissions);
    }

    [Fact]
    public async Task Moving_a_project_preserves_its_committed_submission_record()
    {
        var job = Arrange();
        Assert.Equal(WeixinSeriesApiResult.Submitted, await RunAsync(CreateService(), job, submit: true));
        var moved = Path.Combine(Project, "moved-project");
        Directory.CreateDirectory(moved);
        foreach (var path in Directory.GetFiles(Project))
            File.Move(path, Path.Combine(moved, Path.GetFileName(path)));
        job.ProjectDirectory = moved;
        var movedConfig = Path.Combine(moved, Path.GetFileName(Config));
        Assert.Equal(WeixinSeriesApiResult.AlreadySubmitted, await CreateService().RunAsync(
            job, movedConfig, Options(submit: true), null, null, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => CreateService().OpenBrowserRun(job));
        Assert.Equal(1, _client.Submissions);
    }

    [Fact]
    public async Task An_explicit_stop_before_review_prevents_interface_submission()
    {
        var job = Arrange();
        var config = JsonNode.Parse(File.ReadAllText(Config))!;
        config["second_page"]!["enter_submit_page"] = new JsonObject { ["enabled"] = false };
        File.WriteAllText(Config, config.ToJsonString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(CreateService(), job, submit: true));
        Assert.Contains("提审前停止", error.Message);
        Assert.Empty(_client.Uploads);
    }

    private Task<WeixinSeriesApiResult> RunAsync(WeixinSeriesApiUploadService service, PublishJob job, bool submit = false) =>
        service.RunAsync(job, Config, Options(submit), null, null, CancellationToken.None);

    private static WeixinPublishOptions Options(bool submit) => new()
    {
        SeriesUploadMode = submit ? WeixinSeriesUploadMode.ApiSubmit : WeixinSeriesUploadMode.ApiUpload,
    };

    private WeixinSeriesApiUploadService CreateService() =>
        new(Ledger, new WeixinSeriesReviewStore(Path.Combine(Project, "reviews")), (_, _) => _client);

    private PublishJob Arrange(int second = 2, int total = 2, string account = "slot-one", string finder = "finder-fixture")
    {
        var directory = Path.Combine(Project, account);
        SaveLogin(directory, finder);
        var videos = new[] { "第1集.mp4", $"第{second}集.mp4" };
        foreach (var name in new[] { "海报.jpg", "工程图.png", "承诺函.pdf" }.Concat(videos))
        {
            var path = Path.Combine(Project, name);
            if (!File.Exists(path))
                File.WriteAllText(path, name);
        }
        object Fill(string label, string value) => new { type = "fill", label, value };
        object Upload(string label, string path) => new { type = "upload", label, paths = new[] { path } };
        File.WriteAllText(Config, JsonSerializer.Serialize(new
        {
            first_page = new
            {
                actions = new[]
                {
                    Fill("剧目名称", "测试剧"), Fill("剧目简介", "测试简介"), Fill("制作方名称", "测试制作方"),
                    Fill("总集数", total.ToString()), Fill("试看集数", "1"), Fill("制作成本", "10"),
                    new { type = "choose", field_label = "剧目类型", option_text = "漫剧" },
                    new { type = "set_checked", label = "我已知悉并同意", enabled = true },
                    Upload("剧目海报", "海报.jpg"), Upload("剧目制作证明材料", "工程图.png"), Upload("剧目资质", "承诺函.pdf"),
                }
            },
            second_page = new { upload = new { paths = videos } },
        }));
        return new PublishJob
        {
            ProjectDirectory = Project, ProjectName = "测试剧", AccountId = account,
            AccountName = "测试账号", AccountSessionDirectory = directory, PublishCount = 99,
        };
    }

    private static void SaveLogin(string directory, string finder)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "weixin-series-auth.json"), JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "fixture", value = "offline", domain = "channels.weixin.qq.com", path = "/" } },
            origins = new[] { new { origin = "https://channels.weixin.qq.com", localStorage = new[] { new { name = "finder_username", value = finder } } } },
        }));
    }

    private sealed class RecordingClient : IWeixinSeriesApiClient
    {
        public List<string> Uploads { get; } = [];
        public int Submissions { get; private set; }
        public Exception? SubmitError { get; set; }
        public Task AuthenticateAsync(WeixinSeriesApiSession session, CancellationToken ct) => Task.CompletedTask;
        public Task CheckNameAsync(string title, CancellationToken ct) => Task.CompletedTask;
        public Task CheckMediaAsync(JsonObject payload, CancellationToken ct) => Task.CompletedTask;
        public Task SubmitAsync(JsonObject payload, CancellationToken ct)
        {
            Submissions++;
            Assert.Equal(2, payload["mediaList"]!["mediaId"]!.AsArray().Count);
            return SubmitError is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
        public Task<string> UploadAsync(WeixinSeriesApiFile file, CancellationToken ct)
        {
            Uploads.Add(file.Name);
            return Task.FromResult(file.Episode?.ToString() ?? file.Label);
        }
        public void Dispose() { }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
