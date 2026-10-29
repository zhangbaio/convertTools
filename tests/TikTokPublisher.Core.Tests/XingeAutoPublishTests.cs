using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeAutoPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xinge-auto-publish-tests", Guid.NewGuid().ToString("N"));

    public XingeAutoPublishTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Parse_ReadsAutoPublishAssignmentFields()
    {
        var command = TikTokRemoteCommandParser.Parse(
            """
            {"command":"tiktok_upload_series","titles":["剧A"],"series":[{"title":"剧A","episode_cnt":60,"series_id":"731"}],
             "match_mode":"title_episode","account_profile_id":"acc-1","auto_publish_assignment_id":"42",
             "auto_publish_progress_path":"/client-api/tiktok/auto-publish/assignments/42/progress"}
            """);

        command!.AutoPublishAssignmentId.Should().Be(42);
        var assignment = XingeAutoPublishAssignment.FromCommand(command);
        assignment.Should().Be(new XingeAutoPublishAssignment(
            42, "/client-api/tiktok/auto-publish/assignments/42/progress", "731", "acc-1", "剧A"));

        var manual = TikTokRemoteCommandParser.Parse("""{"command":"tiktok_upload_series","titles":["剧A"]}""");
        XingeAutoPublishAssignment.FromCommand(manual!).Should().BeNull();
    }

    [Theory]
    [InlineData("download", "下载超时", XingeAutoPublishFailureKinds.Source)]
    [InlineData("material_validate", "素材校验未通过", XingeAutoPublishFailureKinds.Source)]
    [InlineData("upload_series", "自动登录失败：未找到账号「A」。", XingeAutoPublishFailureKinds.AccountLogin)]
    [InlineData("upload_series", "已达单日创建剧集上限", XingeAutoPublishFailureKinds.AccountDailyLimit)]
    [InlineData("download", "账号「A」已达单日创建剧集上限", XingeAutoPublishFailureKinds.AccountDailyLimit)]
    [InlineData("rewrite_info", "AI 改写失败", XingeAutoPublishFailureKinds.Upload)]
    [InlineData("", "未找到远程命令指定的 TikTok 账号：acc-1", XingeAutoPublishFailureKinds.AccountLogin)]
    public void Classify_MapsStepAndMessageToFailureKind(string step, string message, string expected) =>
        XingeAutoPublishFailureClassifier.Classify(step, message).Should().Be(expected);

    [Theory]
    [InlineData("未找到精确匹配结果", XingeAutoPublishFailureKinds.Source)]
    [InlineData("命中作者排除：某作者", XingeAutoPublishFailureKinds.Source)]
    [InlineData("集数 8，小于最小限制 10", XingeAutoPublishFailureKinds.Source)]
    [InlineData("红果搜索未返回任何结果，请检查密钥绑定和本地/脱机直连服务状态", XingeAutoPublishFailureKinds.Upload)]
    [InlineData("网络错误", XingeAutoPublishFailureKinds.Upload)]
    public void ClassifyImportFailure_BlocksOnlyDramaSpecificProblems(string reason, string expected) =>
        XingeAutoPublishFailureClassifier.ClassifyImportFailure(reason).Should().Be(expected);

    [Fact]
    public void Observer_ReportsStepChangesOnceAndSuccessOnlyForUploadAfterDispatch()
    {
        var (dir, item) = CreateProject(7, DateTimeOffset.Now.AddMinutes(-5));
        var observer = new XingeAutoPublishQueueObserver();

        item.StatusText = QueueStepStatus.Running;
        item.CurrentStep = QueueStepRegistry.Download;
        observer.Observe(Progress(item, "下载中")).Should().Match<XingeAutoPublishProgress>(
            report => report.Status == XingeAutoPublishStatuses.Running && report.Step == QueueStepRegistry.Download);
        observer.Observe(Progress(item, "下载 50%")).Should().BeNull();

        // 早于派单的上传完成时间来自复用的旧项目，不能算作本派单成功。
        item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.Completed;
        item.UploadCompletedAt = DateTimeOffset.Now.AddDays(-1).ToString("o");
        item.CurrentStep = QueueStepRegistry.UploadSeries;
        observer.Observe(Progress(item, "上传中"))!.Status.Should().Be(XingeAutoPublishStatuses.Running);

        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        observer.Observe(Progress(item, "发布成功"))!.Status.Should().Be(XingeAutoPublishStatuses.Success);

        XingeAutoPublishMarkerStore.TryRead(dir)!.FinalStatus.Should().Be(XingeAutoPublishStatuses.Success);
        XingeAutoPublishMarkerStore.IsActive(dir).Should().BeFalse();
        observer.Observe(Progress(item, "后续步骤")).Should().BeNull();
    }

    [Fact]
    public void Observer_ClassifiesFailedItemsAndIgnoresProjectsWithoutMarker()
    {
        var (_, item) = CreateProject(8, DateTimeOffset.Now);
        var observer = new XingeAutoPublishQueueObserver();
        item.StatusText = QueueStepStatus.Failed;
        item.StepStates[QueueStepRegistry.Download] = QueueStepStatus.Failed;
        item.LastError = "剧集下载失败";

        var report = observer.Observe(Progress(item, "失败"));
        report!.Status.Should().Be(XingeAutoPublishStatuses.Failed);
        report.Step.Should().Be(QueueStepRegistry.Download);
        report.FailureKind.Should().Be(XingeAutoPublishFailureKinds.Source);

        var plainDir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plainDir);
        var plain = new QueueProjectItem { ProjectDir = plainDir, StatusText = QueueStepStatus.Failed, LastError = "x" };
        observer.Observe(Progress(plain, "失败")).Should().BeNull();
    }

    [Fact]
    public void Observer_DailyLimitAlsoReleasesSameAccountItemsStoppedWithoutEvents()
    {
        var (_, limited) = CreateProject(10, DateTimeOffset.Now, "acc-1");
        var (_, drained) = CreateProject(11, DateTimeOffset.Now, "acc-1");
        var (_, otherAccount) = CreateProject(12, DateTimeOffset.Now, "acc-2");
        var observer = new XingeAutoPublishQueueObserver();

        limited.StatusText = QueueStepStatus.Stopped;
        var report = observer.Observe(Progress(limited, "账号「A」已达单日创建剧集上限，任务队列已停止"));
        report!.FailureKind.Should().Be(XingeAutoPublishFailureKinds.AccountDailyLimit);

        drained.StatusText = QueueStepStatus.Stopped;
        otherAccount.StatusText = QueueStepStatus.Stopped;
        var finished = observer.ObserveRunFinished([limited, drained, otherAccount]);

        finished.Should().ContainSingle();
        finished[0].AssignmentId.Should().Be(11);
        finished[0].FailureKind.Should().Be(XingeAutoPublishFailureKinds.AccountDailyLimit);
    }

    [Fact]
    public async Task Reporter_RetriesFromPersistedOutboxAndDropsRejectedReports()
    {
        var outbox = Path.Combine(_root, "outbox.json");
        var settings = new ClientSettings
        {
            XingeRemoteEnabled = true,
            XingeServerUrl = "https://xinge.example/",
            XingeClientId = "rc_1",
            XingeClientToken = "token-1",
        };
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.InternalServerError]);
        var requests = new List<(string Path, JsonElement Body)>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            requests.Add((request.RequestUri!.AbsolutePath, body));
            var status = responses.Count > 0 ? responses.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") };
        }));

        var reporter = new XingeAutoPublishProgressReporter(http, outbox, () => settings);
        var failed = new XingeAutoPublishProgress(5, "/evil/path", XingeAutoPublishStatuses.Failed, "upload_series", "上传失败", XingeAutoPublishFailureKinds.Upload);
        (await reporter.ReportAsync(failed, CancellationToken.None)).Should().BeFalse();
        File.Exists(outbox).Should().BeTrue();

        // 终态未送达时，迟到的中间进度不能覆盖它。
        await reporter.ReportAsync(new XingeAutoPublishProgress(5, "", XingeAutoPublishStatuses.Running, "upload_series"), CancellationToken.None);

        var restarted = new XingeAutoPublishProgressReporter(http, outbox, () => settings);
        restarted.PendingCount.Should().Be(0, "第二次 ReportAsync 已把发件箱中的终态补发成功");
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(request => request.Path == "/client-api/tiktok/auto-publish/assignments/5/progress");
        requests[1].Body.GetProperty("status").GetString().Should().Be(XingeAutoPublishStatuses.Failed);
        requests[1].Body.GetProperty("failure_kind").GetString().Should().Be(XingeAutoPublishFailureKinds.Upload);
        requests[1].Body.GetProperty("client_id").GetString().Should().Be("rc_1");

        responses.Enqueue(HttpStatusCode.NotFound);
        (await restarted.ReportAsync(new XingeAutoPublishProgress(6, "", XingeAutoPublishStatuses.Success), CancellationToken.None))
            .Should().BeTrue("404 表示派单已不存在，重发无意义");
        restarted.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Reporter_KeepsReportsWhileRemoteIsNotConfigured()
    {
        var settings = new ClientSettings { XingeRemoteEnabled = false };
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var reporter = new XingeAutoPublishProgressReporter(http, Path.Combine(_root, "outbox.json"), () => settings);

        (await reporter.ReportAsync(new XingeAutoPublishProgress(9, "", XingeAutoPublishStatuses.Accepted), CancellationToken.None))
            .Should().BeFalse();
        calls.Should().Be(0);
        reporter.PendingCount.Should().Be(1);
    }

    private (string Dir, QueueProjectItem Item) CreateProject(long assignmentId, DateTimeOffset createdAt, string account = "acc-1")
    {
        var dir = Path.Combine(_root, $"project-{assignmentId}");
        Directory.CreateDirectory(dir);
        XingeAutoPublishMarkerStore.Write(dir, new XingeAutoPublishAssignment(assignmentId, "", "s" + assignmentId, account, "剧"), createdAt);
        return (dir, new QueueProjectItem { ProjectDir = dir, AccountProfileId = account });
    }

    private static QueueWorkerProgress Progress(QueueProjectItem item, string message) =>
        new() { WorkspaceRoot = Path.GetDirectoryName(item.ProjectDir)!, Item = item, Message = message };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
