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
    public void RemovingReleasedProjectFromQueueKeepsItsLocalFiles()
    {
        var project = Path.Combine(_root, "Failed drama");
        Directory.CreateDirectory(project);
        var metadata = Path.Combine(project, "shortdrama-project.json");
        File.WriteAllText(metadata, """{"bookId":"dramabox:test","title":"Failed drama","episodeCount":2}""");
        var video = Path.Combine(project, "第1集.mp4");
        File.WriteAllText(video, "media");
        WorkspaceQueueService.AddProjectsToQueue(_root, [project]);
        XingeAutoPublishMarkerStore.Write(project, new XingeAutoPublishAssignment(
            41, "/progress", "dramabox:test", "profile-a", "Failed drama"));
        XingeAutoPublishMarkerStore.MarkFinal(project, 41, XingeAutoPublishStatuses.Failed);
        Assert.NotEmpty(WorkspaceQueueService.ScanProjects(_root));

        Assert.Throws<InvalidOperationException>(() => QueueRetryUseCase.RemoveReleasedProject(
            _root, "wrong-account", new TikTokRetryQueueItem(project, AutoPublishAssignmentId: 41)));
        QueueRetryUseCase.RemoveReleasedProject(
            _root, "profile-a", new TikTokRetryQueueItem(project, AutoPublishAssignmentId: 41));

        Assert.Empty(WorkspaceQueueService.ScanProjects(_root));
        Assert.True(File.Exists(metadata));
        Assert.Equal("media", File.ReadAllText(video));
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
        [InlineData("download", "下载超时", XingeAutoPublishFailureKinds.Download)]
    [InlineData("material_validate", "素材校验未通过", XingeAutoPublishFailureKinds.Source)]
    [InlineData("upload_series", "自动登录失败：未找到账号「A」。", XingeAutoPublishFailureKinds.AccountLogin)]
    [InlineData("upload_series", "已达单日创建剧集上限", XingeAutoPublishFailureKinds.AccountDailyLimit)]
    [InlineData("download", "账号「A」已达单日创建剧集上限", XingeAutoPublishFailureKinds.AccountDailyLimit)]
    [InlineData("rewrite_info", "AI 改写失败", XingeAutoPublishFailureKinds.Upload)]
    [InlineData("", "未找到远程命令指定的 TikTok 账号：acc-1", XingeAutoPublishFailureKinds.AccountLogin)]
    [InlineData("download", "账号未启用远程分发", XingeAutoPublishFailureKinds.AccountRemoteDisabled)]
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
    public void ShouldKeepChecked_UsesLocalDispatchDayAndAccount()
    {
        var today = new DateTime(2026, 11, 10);
        var offset = TimeZoneInfo.Local.GetUtcOffset(today);
        var marker = new XingeAutoPublishMarker
        {
            AssignmentId = 42,
            AccountProfileId = "acc-1",
            CreatedAt = new DateTimeOffset(2026, 11, 10, 10, 29, 0, offset),
            FinalStatus = "failed",
        };

        XingeAutoPublishMarkerStore.ShouldKeepChecked(marker, "acc-1", today).Should().BeTrue();
        XingeAutoPublishMarkerStore.ShouldKeepChecked(marker, "acc-2", today).Should().BeFalse();
        XingeAutoPublishMarkerStore.ShouldKeepChecked(marker, "acc-1", today.AddDays(-1)).Should().BeFalse();
        marker.CreatedAt = new DateTimeOffset(2026, 11, 9, 23, 0, 0, offset);
        XingeAutoPublishMarkerStore.ShouldKeepChecked(marker, "acc-1", today).Should().BeFalse();
        XingeAutoPublishMarkerStore.ShouldKeepChecked(null, "acc-1", today).Should().BeFalse();
        marker.CreatedAt = new DateTimeOffset(2026, 11, 10, 10, 29, 0, offset);
        marker.FinalStatus = XingeAutoPublishStatuses.UserStopped;
        XingeAutoPublishMarkerStore.ShouldKeepChecked(marker, "acc-1", today).Should().BeTrue();
    }

    [Fact]
    public void Recovery_ResumesManualStopWhenTheProjectRemains()
    {
        var marker = new XingeAutoPublishMarker
        {
            AssignmentId = 7,
            FinalStatus = XingeAutoPublishStatuses.UserStopped,
        };

        XingeAutoPublishRecovery.ShouldReclaimOnRestart(marker).Should().BeTrue();
        XingeAutoPublishRecovery.Decide("user_stopped", marker, true).Should().Be(XingeAutoPublishRecoveryAction.Resume);
        XingeAutoPublishRecovery.Decide("accepted", marker, true).Should().Be(XingeAutoPublishRecoveryAction.Resume);
        XingeAutoPublishRecovery.Decide("running", marker, true).Should().Be(XingeAutoPublishRecoveryAction.Resume);
        XingeAutoPublishRecovery.Decide("user_stopped", marker, false).Should().Be(XingeAutoPublishRecoveryAction.None);
        XingeAutoPublishRecovery.Decide("released", marker, true).Should().Be(XingeAutoPublishRecoveryAction.None);
        XingeAutoPublishRecovery.ShouldReclaimOnRestart(new XingeAutoPublishMarker { AssignmentId = 7 }).Should().BeFalse();
    }

    [Fact]
    public void Parse_ReadsRetryQueueItems()
    {
        var command = TikTokRemoteCommandParser.Parse(
            """
            {"command":"tiktok_retry_queue","account_profile_id":"acc-1","account_profile_name":"A",
             "items":[{"project_path":"D:\\ws\\剧A","task_id":"3","auto_publish_assignment_id":42},{"project_path":" "},{"project_path":"D:\\ws\\剧B"}]}
            """);

        command!.IsRetryQueueCommand.Should().BeTrue();
        command.AccountProfileId.Should().Be("acc-1");
        command.RetryItems.Should().Equal(
            new TikTokRetryQueueItem(@"D:\ws\剧A", 3, 42),
            new TikTokRetryQueueItem(@"D:\ws\剧B"));
    }

    [Fact]
    public void Observer_ReportsStatusChangesOnceAndSuccessOnlyForUploadAfterDispatch()
    {
        var (dir, item) = CreateProject(7, DateTimeOffset.Now.AddMinutes(-5));
        var observer = new XingeUploadTaskObserver();

        item.StatusText = QueueStepStatus.Running;
        item.CurrentStep = QueueStepRegistry.Download;
        observer.Observe(Progress(item, "下载中"), uploadStepEnabled: true).Should().Match<XingeUploadTaskReport>(
            report => report.Status == XingeUploadTaskStatuses.Running && report.AssignmentId == 7 && report.SeriesId == "s7");
        observer.Observe(Progress(item, "下载 50%"), uploadStepEnabled: true).Should().BeNull("步骤变化不再单独上报");

        // 早于派单的上传完成时间来自复用的旧项目，不能算作本派单成功。
        item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.Completed;
        item.UploadCompletedAt = DateTimeOffset.Now.AddDays(-1).ToString("o");
        observer.Observe(Progress(item, "上传中"), uploadStepEnabled: true).Should().BeNull();

        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        observer.Observe(Progress(item, "发布成功"), uploadStepEnabled: true)!.Status.Should().Be(XingeUploadTaskStatuses.Success);

        XingeAutoPublishMarkerStore.TryRead(dir)!.FinalStatus.Should().Be(XingeUploadTaskStatuses.Success);
        XingeAutoPublishMarkerStore.IsActive(dir).Should().BeFalse();
        observer.Observe(Progress(item, "后续步骤"), uploadStepEnabled: true).Should().BeNull();
    }

    [Fact]
    public void Observer_TracksManualUploadsOnlyWhenTheRunUploads()
    {
        var item = CreatePlainProject("manual");
        item.AccountProfileName = "";
        item.StatusText = QueueStepStatus.Running;

        new XingeUploadTaskObserver().Observe(Progress(item, "下载中"), uploadStepEnabled: false)
            .Should().BeNull("本次运行不含上传步骤，不是上传任务");

        var observer = new XingeUploadTaskObserver();
        var running = observer.Observe(Progress(item, "下载中"), true, _ => new XingeUploadTaskAccount("acc-1", "账号A"));
        running!.Status.Should().Be(XingeUploadTaskStatuses.Running);
        running.AssignmentId.Should().Be(0);
        running.AccountProfileName.Should().Be("账号A");

        item.StatusText = QueueStepStatus.Failed;
        item.StepStates[QueueStepRegistry.Download] = QueueStepStatus.Failed;
        item.LastError = "剧集下载失败";
        var failed = observer.Observe(Progress(item, "失败"), true);
        failed!.Status.Should().Be(XingeUploadTaskStatuses.Failed);
        failed.FailureKind.Should().Be(XingeAutoPublishFailureKinds.Download);
        failed.FailureReason.Should().Contain("剧集下载失败");

        var uploaded = CreatePlainProject("uploaded");
        uploaded.StatusText = QueueStepStatus.Running;
        uploaded.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.Completed;
        uploaded.UploadCompletedAt = DateTimeOffset.Now.AddHours(-1).ToString("o");
        observer.Observe(Progress(uploaded, "补跑其他步骤"), true).Should().BeNull("上传在本次运行前已完成");
    }

    [Fact]
    public void Observer_RetriesADispatchedProjectLocallyUntilTheThirdFailure()
    {
        var (dir, item) = CreateProject(31, DateTimeOffset.Now.AddMinutes(-5));
        var observer = new XingeUploadTaskObserver();
        item.StatusText = QueueStepStatus.Running;
        observer.Observe(Progress(item, "下载中"), true);

        item.StatusText = QueueStepStatus.Failed;
        item.StepStates[QueueStepRegistry.Download] = QueueStepStatus.Failed;
        item.LastError = "剧集下载失败";

        observer.Observe(Progress(item, "失败"), true).Should().BeNull("前两次失败留在本地");
        XingeAutoPublishMarkerStore.TryRead(dir)!.LocalFailures.Should().Be(1);
        XingeAutoPublishMarkerStore.IsActive(dir).Should().BeTrue();

        item.StatusText = QueueStepStatus.Running;
        observer.Observe(Progress(item, "再次下载"), true);
        item.StatusText = QueueStepStatus.Failed;
        observer.Observe(Progress(item, "又失败"), true).Should().BeNull();
        XingeAutoPublishMarkerStore.TryRead(dir)!.LocalFailures.Should().Be(2);

        item.StatusText = QueueStepStatus.Running;
        observer.Observe(Progress(item, "第三次"), true);
        item.StatusText = QueueStepStatus.Failed;
        var reported = observer.Observe(Progress(item, "第三次失败"), true);
        reported!.Status.Should().Be(XingeUploadTaskStatuses.Failed);
        XingeAutoPublishMarkerStore.TryRead(dir)!.FinalStatus.Should().Be(XingeUploadTaskStatuses.Failed);
        XingeAutoPublishMarkerStore.IsActive(dir).Should().BeFalse();
    }

    [Fact]
    public void Observer_ReportsStoppedForUnfinishedItemsWhenRunEnds()
    {
        var item = CreatePlainProject("stopped");
        var observer = new XingeUploadTaskObserver();
        item.StatusText = QueueStepStatus.Running;
        observer.Observe(Progress(item, "上传中"), true)!.Status.Should().Be(XingeUploadTaskStatuses.Running);

        item.StatusText = QueueStepStatus.Stopped;
        var finished = observer.ObserveRunFinished(_root, [item]);

        finished.Should().ContainSingle();
        finished[0].Status.Should().Be(XingeUploadTaskStatuses.Stopped);
        finished[0].FailureReason.Should().NotBeEmpty();
        observer.ObserveRunFinished(_root, [item]).Should().BeEmpty("运行结束后不再跟踪");
    }

    [Fact]
    public void Observer_DailyLimitAlsoReleasesSameAccountItemsStoppedWithoutEvents()
    {
        var (_, limited) = CreateProject(10, DateTimeOffset.Now, "acc-1");
        var (_, drained) = CreateProject(11, DateTimeOffset.Now, "acc-1");
        var (_, otherAccount) = CreateProject(12, DateTimeOffset.Now, "acc-2");
        var observer = new XingeUploadTaskObserver();

        limited.StatusText = QueueStepStatus.Stopped;
        var report = observer.Observe(Progress(limited, "账号「A」已达单日创建剧集上限，任务队列已停止"), true);
        report!.Status.Should().Be(XingeUploadTaskStatuses.Failed);
        report.FailureKind.Should().Be(XingeAutoPublishFailureKinds.AccountDailyLimit);

        drained.StatusText = QueueStepStatus.Stopped;
        otherAccount.StatusText = QueueStepStatus.Stopped;
        var finished = observer.ObserveRunFinished(_root, [limited, drained, otherAccount]);

        finished.Should().ContainSingle();
        finished[0].AssignmentId.Should().Be(11);
        finished[0].Status.Should().Be(XingeUploadTaskStatuses.Failed);
        finished[0].FailureKind.Should().Be(XingeAutoPublishFailureKinds.AccountDailyLimit);
    }

    [Fact]
    public void Observer_UsesRewrittenMarkerAfterServerRetryReassigns()
    {
        var (dir, item) = CreateProject(20, DateTimeOffset.Now.AddHours(-2));
        XingeAutoPublishMarkerStore.MarkFinal(dir, 20, XingeUploadTaskStatuses.Failed);
        XingeAutoPublishMarkerStore.Write(dir, new XingeAutoPublishAssignment(21, "", "s20", "acc-1", "剧"));

        item.StatusText = QueueStepStatus.Running;
        new XingeUploadTaskObserver().Observe(Progress(item, "下载中"), true)!.AssignmentId.Should().Be(21);
    }

    [Fact]
    public async Task UploadTaskReporter_BatchesCoalescesAndKeepsOutboxUntilDelivered()
    {
        var outbox = Path.Combine(_root, "task-outbox.json");
        var settings = new ClientSettings
        {
            XingeRemoteEnabled = true,
            XingeConnectionMode = XingeRuntimeConnection.Manual,
            XingeServerUrl = "https://xinge.example/",
            XingeClientId = "rc_1",
            XingeClientToken = "token-1",
        };
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable]);
        var bodies = new List<JsonElement>();
        var paths = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
            var status = responses.Count > 0 ? responses.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));

        var offline = new ClientSettings { XingeRemoteEnabled = false };
        var reporter = new XingeUploadTaskReporter(http, outbox, () => offline);
        reporter.Enqueue(new XingeUploadTaskReport(@"D:\ws\a", XingeUploadTaskStatuses.Running));
        reporter.Enqueue(new XingeUploadTaskReport(@"D:\ws\a", XingeUploadTaskStatuses.Failed, FailureKind: "upload", FailureReason: "上传失败"));
        reporter.Enqueue(new XingeUploadTaskReport(@"D:\ws\a", XingeUploadTaskStatuses.Running));
        reporter.Enqueue(new XingeUploadTaskReport(@"D:\ws\b", XingeUploadTaskStatuses.Running, AssignmentId: 9));
        await reporter.FlushAsync(CancellationToken.None);
        reporter.PendingCount.Should().Be(3, "未发出的中间状态被取代，终态保留");
        paths.Should().BeEmpty();

        var restarted = new XingeUploadTaskReporter(http, outbox, () => settings);
        await restarted.FlushAsync(CancellationToken.None);
        restarted.PendingCount.Should().Be(3, "5xx 保留发件箱");
        await restarted.FlushAsync(CancellationToken.None);
        restarted.PendingCount.Should().Be(0);

        paths.Should().OnlyContain(path => path == XingeUploadTaskReporter.ReportPath);
        var tasks = bodies[^1].GetProperty("tasks").EnumerateArray().ToList();
        tasks.Select(task => task.GetProperty("status").GetString())
            .Should().Equal(XingeUploadTaskStatuses.Failed, XingeUploadTaskStatuses.Running, XingeUploadTaskStatuses.Running);
        tasks[0].GetProperty("failure_reason").GetString().Should().Be("上传失败");
        tasks[2].GetProperty("assignment_id").GetInt64().Should().Be(9);
        bodies[^1].GetProperty("client_id").GetString().Should().Be("rc_1");
    }

    [Fact]
    public async Task Reporter_RetriesFromPersistedOutboxAndDropsRejectedReports()
    {
        var outbox = Path.Combine(_root, "outbox.json");
        var settings = new ClientSettings
        {
            XingeRemoteEnabled = true,
            XingeConnectionMode = XingeRuntimeConnection.Manual,
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
    public async Task Reporter_LeavesPreviousConnectionReportsInItsOutboxAfterSwitch()
    {
        var settings = new ClientSettings
        {
            XingeConnectionMode = XingeRuntimeConnection.Manual,
            XingeServerUrl = "https://xinge.example",
            XingeClientId = "rc_owner_a",
            XingeClientToken = "test-token-a",
        };
        var sentClients = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            sentClients.Add(request.Headers.GetValues("X-Remote-Client-ID").Single());
            return Task.FromResult(new HttpResponseMessage(sentClients.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }));
        var reporter = new XingeAutoPublishProgressReporter(http, Path.Combine(_root, "account-outbox.json"), () => settings);
        await reporter.ReportAsync(new XingeAutoPublishProgress(1, "", XingeAutoPublishStatuses.Success), CancellationToken.None);
        settings.XingeClientId = "rc_owner_b";
        settings.XingeClientToken = "test-token-b";
        await reporter.FlushAsync(CancellationToken.None);
        sentClients.Should().Equal("rc_owner_a");
        reporter.PendingCount.Should().Be(1);

        await reporter.ReportAsync(new XingeAutoPublishProgress(1, "", XingeAutoPublishStatuses.Success), CancellationToken.None);
        sentClients.Should().Equal("rc_owner_a", "rc_owner_b");
        reporter.PendingCount.Should().Be(1);
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

    [Fact]
    public async Task Reporter_ReplacesPendingManualStopWhenRestartResumes()
    {
        var settings = new ClientSettings { XingeRemoteEnabled = false };
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            requests.Add(body.GetProperty("status").GetString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            };
        }));
        var reporter = new XingeAutoPublishProgressReporter(http, Path.Combine(_root, "stop-outbox.json"), () => settings);
        await reporter.ReportAsync(
            new XingeAutoPublishProgress(4, "", XingeAutoPublishStatuses.UserStopped, "upload_series", "用户手动停止", XingeAutoPublishFailureKinds.UserStopped),
            CancellationToken.None);
        reporter.PendingCount.Should().Be(1);

        settings.XingeRemoteEnabled = true;
        settings.XingeConnectionMode = XingeRuntimeConnection.Manual;
        settings.XingeServerUrl = "https://xinge.example/";
        settings.XingeClientId = "rc_1";
        settings.XingeClientToken = "token-1";
        (await reporter.ReportAsync(new XingeAutoPublishProgress(4, "", XingeAutoPublishStatuses.Running, "upload_series", "客户端重启后继续执行"), CancellationToken.None))
            .Should().BeTrue();
        requests.Should().Equal(XingeAutoPublishStatuses.Running);
        reporter.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData("accepted", true, true, XingeAutoPublishRecoveryAction.Resume)]
    [InlineData("running", false, false, XingeAutoPublishRecoveryAction.ReportInterrupted)]
    [InlineData("dispatched", false, false, XingeAutoPublishRecoveryAction.None)]
    [InlineData("retry_wait", true, true, XingeAutoPublishRecoveryAction.None)]
    [InlineData("accepted", true, false, XingeAutoPublishRecoveryAction.ReportSuccess)]
    public void Recovery_DecidesFromServerStatusAndLocalProject(
        string serverStatus,
        bool hasMarker,
        bool projectExists,
        XingeAutoPublishRecoveryAction expected)
    {
        XingeAutoPublishMarker? marker = null;
        if (hasMarker)
        {
            marker = new XingeAutoPublishMarker { AssignmentId = 7, FinalStatus = projectExists ? "" : XingeAutoPublishStatuses.Success };
        }

        XingeAutoPublishRecovery.Decide(serverStatus, marker, projectExists && hasMarker).Should().Be(expected);
    }

    [Fact]
    public void NotExecutableDetail_is_the_release_reason()
    {
        XingeAutoPublishRecovery.NotExecutableDetail.Should().Be("目标目录没有可执行项目");
    }

    [Fact]
    public void EnableForResume_checks_projects_without_resetting_finished_steps()
    {
        var stoppedDir = Path.Combine(_root, "stopped");
        var failedDir = Path.Combine(_root, "failed");
        var archivedDir = Path.Combine(_root, "archived");
        var stopped = new QueueProjectItem
        {
            ProjectDir = stoppedDir,
            Enabled = false,
            StatusText = QueueStepStatus.Stopped,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.Download] = QueueStepStatus.Stopped,
                [QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed,
            },
        };
        var failed = new QueueProjectItem
        {
            ProjectDir = failedDir,
            Enabled = false,
            StatusText = QueueStepStatus.Failed,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.Download] = QueueStepStatus.Failed,
            },
        };
        var archived = new QueueProjectItem
        {
            ProjectDir = archivedDir,
            Enabled = false,
            Archived = true,
            StatusText = QueueStepStatus.Stopped,
        };
        var unrelated = new QueueProjectItem
        {
            ProjectDir = Path.Combine(_root, "other"),
            Enabled = false,
        };

        var enabled = XingeAutoPublishRecoveryQueue.EnableForResume(
            [unrelated, archived, failed, stopped],
            [stoppedDir, archivedDir, failedDir]);

        enabled.Select(item => item.ProjectDir).Should().Equal(stoppedDir, failedDir);
        stopped.Enabled.Should().BeTrue();
        stopped.StatusText.Should().Be(QueueStepStatus.Stopped);
        stopped.StepStates[QueueStepKeys.Download].Should().Be(QueueStepStatus.Stopped);
        stopped.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Completed);
        failed.Enabled.Should().BeTrue();
        failed.StepStates[QueueStepKeys.Download].Should().Be(QueueStepStatus.Failed);
        archived.Enabled.Should().BeFalse();
        unrelated.Enabled.Should().BeFalse();
    }

    [Fact]
    public void QuotaFull_holds_until_the_business_day_ends_and_skips_work_that_has_not_started()
    {
        var until = new DateTimeOffset(2026, 11, 4, 8, 50, 0, TimeSpan.FromHours(8));
        XingeQuotaFull.IsHeld(until, until.AddMinutes(-1)).Should().BeTrue();
        XingeQuotaFull.IsHeld(until, until).Should().BeFalse();
        XingeQuotaFull.ShouldSkipRecovery(accountFull: true, "dispatched").Should().BeTrue();
        XingeQuotaFull.ShouldSkipRecovery(accountFull: true, XingeAutoPublishStatuses.Accepted).Should().BeTrue();
        XingeQuotaFull.ShouldSkipRecovery(accountFull: true, XingeAutoPublishStatuses.Running).Should().BeTrue();
        XingeQuotaFull.ShouldSkipRecovery(accountFull: false, "dispatched").Should().BeFalse();

        var uploaded = new QueueProjectItem
        {
            Enabled = true,
            AccountProfileId = "acc-1",
            UploadCompletedAt = "2026-11-03T10:00:00+08:00",
        };
        var failed = new QueueProjectItem
        {
            Enabled = true,
            AccountProfileId = "acc-1",
            StepStates = new Dictionary<string, string> { [QueueStepKeys.Download] = QueueStepStatus.Failed },
        };
        var other = new QueueProjectItem
        {
            Enabled = true,
            AccountProfileId = "acc-2",
            StepStates = new Dictionary<string, string> { [QueueStepKeys.Download] = QueueStepStatus.Failed },
        };
        QueueWorkerRunner.DisableAccountProjectsBeforeUpload([uploaded, failed, other], "acc-1").Should().Be(1);
        uploaded.Enabled.Should().BeTrue();
        failed.Enabled.Should().BeFalse();
        other.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Classify_QueueBusyDoesNotLookLikeAnUploadFailure() =>
        XingeAutoPublishFailureClassifier.Classify("", "当前已有 TikTok 队列在执行，请等待完成后再发起新任务。")
            .Should().Be(XingeAutoPublishFailureKinds.QueueBusy);

    [Fact]
    public void DownloadFailureStopsLaterSteps()
    {
        var item = new QueueProjectItem
        {
            StepStates = new Dictionary<string, string> { [QueueStepKeys.Download] = QueueStepStatus.Failed },
        };
        QueueWorkerRunner.ShouldStopAfterDownloadFailure(item, QueueStepKeys.Download).Should().BeFalse();
        QueueWorkerRunner.ShouldStopAfterDownloadFailure(item, QueueStepKeys.RewriteInfo).Should().BeTrue();
        QueueWorkerRunner.ShouldStopAfterDownloadFailure(item, QueueStepKeys.UploadSeries).Should().BeTrue();

        item.StepStates[QueueStepKeys.Download] = QueueStepStatus.Completed;
        QueueWorkerRunner.ShouldStopAfterDownloadFailure(item, QueueStepKeys.SmallVideoRepair).Should().BeFalse();
    }

    private (string Dir, QueueProjectItem Item) CreateProject(long assignmentId, DateTimeOffset createdAt, string account = "acc-1")
    {
        var dir = Path.Combine(_root, $"project-{assignmentId}");
        Directory.CreateDirectory(dir);
        XingeAutoPublishMarkerStore.Write(dir, new XingeAutoPublishAssignment(assignmentId, "", "s" + assignmentId, account, "剧"), createdAt);
        return (dir, new QueueProjectItem { ProjectDir = dir, AccountProfileId = account });
    }

    private QueueProjectItem CreatePlainProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return new QueueProjectItem { ProjectDir = dir, AccountProfileId = "acc-1" };
    }

    private static QueueWorkerProgress Progress(QueueProjectItem item, string message) =>
        new() { WorkspaceRoot = Path.GetDirectoryName(item.ProjectDir)!, Item = item, Message = message };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
