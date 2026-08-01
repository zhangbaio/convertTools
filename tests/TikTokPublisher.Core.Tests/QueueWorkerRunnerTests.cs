using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueWorkerRunnerTests
{
    private static readonly Lazy<byte[]> TinyMp4Bytes = new(CreateTinyMp4Bytes);

    [Fact]
    public async Task RunAsync_uploads_all_projects_when_preupload_steps_complete_synchronously()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var items = Enumerable.Range(1, 7)
            .Select(index => CreateReadyToUploadItem(index, account))
            .ToList();
        var host = new ImmediatePublishHost();
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepRegistry.Download, QueueStepRegistry.UploadSeries],
            ProjectConcurrency = 4,
        };
        var progressMessages = new List<string>();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-test"),
            items,
            options,
            host,
            store,
            FinalAction.None,
            onProgress: progress => progressMessages.Add($"{progress.Item?.OriginalTitle}: {progress.StepKey}: {progress.Message}"),
            onPersist: null,
            CancellationToken.None);

        var progressText = string.Join(Environment.NewLine, progressMessages);
        summary.TotalCount.Should().Be(7);
        summary.SuccessCount.Should().Be(7, progressText);
        summary.FailedCount.Should().Be(0, progressText);
        host.PublishedProjectDirs.Should().HaveCount(7, progressText);
        items.Should().OnlyContain(item =>
            item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) == QueueStepStatus.Completed);
    }

    [Fact]
    public async Task StartPreUploadPipeline_returns_before_a_synchronous_pipeline_prefix_completes()
    {
        using var pipelineEntered = new ManualResetEventSlim(false);
        using var releasePipeline = new ManualResetEventSlim(false);
        using var schedulerReturned = new ManualResetEventSlim(false);
        Task? pipelineTask = null;

        var schedulerTask = Task.Run(() =>
        {
            pipelineTask = QueueWorkerRunner.StartPreUploadPipeline(
                () =>
                {
                    pipelineEntered.Set();
                    releasePipeline.Wait(TimeSpan.FromSeconds(5));
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            schedulerReturned.Set();
        });

        var returnedWithoutBlocking = schedulerReturned.Wait(TimeSpan.FromSeconds(2));
        var pipelineStarted = pipelineEntered.Wait(TimeSpan.FromSeconds(2));
        releasePipeline.Set();

        await schedulerTask.WaitAsync(TimeSpan.FromSeconds(5));
        await pipelineTask!.WaitAsync(TimeSpan.FromSeconds(5));

        returnedWithoutBlocking.Should().BeTrue(
            "the scheduler must be able to fill the remaining project concurrency slots");
        pipelineStarted.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_rejects_empty_or_all_whitespace_explicit_project_filter()
    {
        var account = new TikTokAccountProfile { Id = "acct-filter-empty", Name = "filter-empty" };
        var store = CreateAccountStore(account);
        var item = CreateReadyToUploadItem(1, account);
        var host = new ImmediatePublishHost();
        var options = new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] };
        IReadOnlyCollection<string>[] invalidFilters =
        [
            Array.Empty<string>(),
            ["", "   "],
        ];

        foreach (var filter in invalidFilters)
        {
            var action = () => new QueueWorkerRunner().RunAsync(
                Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-empty-filter-test"),
                [item],
                options,
                host,
                store,
                FinalAction.None,
                onProgress: null,
                onPersist: null,
                CancellationToken.None,
                projectDirFilter: filter);

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*项目筛选*");
        }

        host.PublishedProjectDirs.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_rejects_explicit_project_filter_with_zero_candidates()
    {
        var account = new TikTokAccountProfile { Id = "acct-filter-miss", Name = "filter-miss" };
        var store = CreateAccountStore(account);
        var item = CreateReadyToUploadItem(1, account);
        var host = new ImmediatePublishHost();
        var nonMatchingProjectDir = Path.Combine(Path.GetTempPath(), $"tiktok-filter-miss-{Guid.NewGuid():N}");

        var action = () => new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-missing-filter-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None,
            projectDirFilter: [nonMatchingProjectDir]);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未匹配到可执行的队列项目*");
        host.PublishedProjectDirs.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_executes_only_filtered_projects_in_filter_order()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-filter-order",
            Name = "filter-order",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var first = CreateReadyToUploadItem(1, account);
        var skipped = CreateReadyToUploadItem(2, account);
        var last = CreateReadyToUploadItem(3, account);
        var host = new ImmediatePublishHost();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-filter-order-test"),
            [first, skipped, last],
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 1,
            },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None,
            projectDirFilter: [last.ProjectDir, first.ProjectDir]);

        summary.TotalCount.Should().Be(2);
        host.PublishedProjectDirs.Should().Equal(last.ProjectDir, first.ProjectDir);
        skipped.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries)
            .Should().NotBe(QueueStepStatus.Completed);
    }

    [Fact]
    public async Task RunAsync_allows_null_filter_when_no_project_needs_work()
    {
        var account = new TikTokAccountProfile { Id = "acct-filter-null", Name = "filter-null" };
        var store = CreateAccountStore(account);
        var completed = CreateCompletedItem(1, account);
        var host = new ImmediatePublishHost();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-null-filter-test"),
            [completed],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None,
            projectDirFilter: null);

        summary.TotalCount.Should().Be(0);
        host.PublishedProjectDirs.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_copyright_proof_completion_edits_project_when_historical_upload_is_completed()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-copyright-proof-completion",
            Name = "copyright-proof-completion",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var completed = CreateCompletedItem(1, account);
        var host = new ImmediatePublishHost();
        var options = new QueueRunOptions();
        options.ConfigureForCopyrightProofCompletion();
        options.EnabledSteps = [QueueStepRegistry.UploadSeries];
        var progress = new List<string>();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-copyright-proof-completion-test"),
            [completed],
            options,
            host,
            store,
            FinalAction.None,
            onProgress: update => progress.Add(update.Message),
            onPersist: null,
            CancellationToken.None);

        var progressText = string.Join(Environment.NewLine, progress);
        summary.TotalCount.Should().Be(1);
        summary.SuccessCount.Should().Be(1, progressText);
        summary.FailedCount.Should().Be(0, progressText);
        host.BrowserReadyCalls.Should().Be(1, progressText);
        host.PublishedProjectDirs.Should().Equal([completed.ProjectDir], progressText);
        completed.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
    }

    [Fact]
    public async Task RunAsync_rejects_matching_filter_when_no_selected_step_needs_work()
    {
        var account = new TikTokAccountProfile { Id = "acct-filter-complete", Name = "filter-complete" };
        var store = CreateAccountStore(account);
        var completed = CreateCompletedItem(1, account);
        var host = new ImmediatePublishHost();

        var action = () => new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-complete-filter-test"),
            [completed],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None,
            projectDirFilter: [completed.ProjectDir]);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未匹配到可执行的队列项目*");
        host.PublishedProjectDirs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_skips_failed_upload_and_continues_same_account_queue(bool throwException)
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-skip-failure",
            Name = "skip-failure",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var failedItem = CreateReadyToUploadItem(1, account);
        var nextItem = CreateReadyToUploadItem(2, account);
        failedItem.QueuedAt = "2026-01-01T00:00:00";
        nextItem.QueuedAt = "2026-01-01T00:00:01";
        var items = new List<QueueProjectItem> { failedItem, nextItem };
        var host = new FirstUploadFailsHost(throwException);
        var runner = new QueueWorkerRunner();
        var progressMessages = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var summary = await runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-skip-upload-failure-test"),
            items,
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 2,
            },
            host,
            store,
            FinalAction.None,
            onProgress: progress => progressMessages.Add(progress.Message),
            onPersist: null,
            cts.Token);

        summary.Stopped.Should().BeFalse();
        summary.SuccessCount.Should().Be(1);
        summary.FailedCount.Should().Be(1);
        host.PublishedProjectDirs.Should().Equal(failedItem.ProjectDir, nextItem.ProjectDir);
        failedItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        failedItem.LastError.Should().Contain("TimeoutException");
        nextItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
        runner.ManualIntervention.HasPending.Should().BeFalse();
        progressMessages.Should().Contain(message =>
            message.Contains("已跳过当前项目并继续后续队列", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_still_stops_queue_for_explicit_daily_limit_result()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-daily-limit",
            Name = "daily-limit",
            TiktokAccountNickname = "账号2",
            TiktokLoginEmail = "2720937754@qq.com",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var firstItem = CreateReadyToUploadItem(1, account);
        var nextItem = CreateReadyToUploadItem(2, account);
        firstItem.QueuedAt = "2026-01-01T00:00:00";
        nextItem.QueuedAt = "2026-01-01T00:00:01";
        var host = new DailyLimitPublishHost();
        var progressMessages = new List<string>();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-daily-limit-test"),
            [firstItem, nextItem],
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 2,
            },
            host,
            store,
            FinalAction.None,
            onProgress: progress => progressMessages.Add(progress.Message),
            onPersist: null,
            CancellationToken.None);

        summary.Stopped.Should().BeTrue();
        summary.SuccessCount.Should().Be(0);
        summary.FailedCount.Should().Be(1);
        host.PublishedProjectDirs.Should().Equal(firstItem.ProjectDir);
        firstItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Stopped);
        progressMessages.Should().Contain(message =>
            message.Contains("账号「账号2（2720937754@qq.com）」已达单日创建剧集上限", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_copyright_proof_completion_ignores_stop_result_and_continues_queue()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-proof-continue",
            Name = "proof-continue",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var firstItem = CreateReadyToUploadItem(1, account);
        var nextItem = CreateReadyToUploadItem(2, account);
        firstItem.QueuedAt = "2026-01-01T00:00:00";
        nextItem.QueuedAt = "2026-01-01T00:00:01";
        var host = new DailyLimitPublishHost();
        var options = new QueueRunOptions { ProjectConcurrency = 2 };
        options.ConfigureForCopyrightProofCompletion();
        options.EnabledSteps = [QueueStepRegistry.UploadSeries];
        var progressMessages = new List<string>();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-proof-continue-test"),
            [firstItem, nextItem],
            options,
            host,
            store,
            FinalAction.None,
            onProgress: progress => progressMessages.Add(progress.Message),
            onPersist: null,
            CancellationToken.None);

        summary.Stopped.Should().BeFalse();
        summary.SuccessCount.Should().Be(0);
        summary.FailedCount.Should().Be(2);
        host.PublishedProjectDirs.Should().Equal(firstItem.ProjectDir, nextItem.ProjectDir);
        firstItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        nextItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        progressMessages.Should().Contain(message =>
            message.Contains("已忽略停止整个队列标记", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_skips_unexpected_upload_pipeline_fault_and_continues_queue()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-pipeline-fault",
            Name = "pipeline-fault",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var failedItem = CreateReadyToUploadItem(1, account);
        var nextItem = CreateReadyToUploadItem(2, account);
        failedItem.QueuedAt = "2026-01-01T00:00:00";
        nextItem.QueuedAt = "2026-01-01T00:00:01";
        var host = new ImmediatePublishHost();
        var injectFault = true;

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-pipeline-fault-test"),
            [failedItem, nextItem],
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 2,
            },
            host,
            store,
            FinalAction.None,
            onProgress: progress =>
            {
                if (injectFault &&
                    ReferenceEquals(progress.Item, failedItem) &&
                    progress.Message.Contains("准备内置浏览器", StringComparison.Ordinal))
                {
                    injectFault = false;
                    throw new InvalidOperationException("unexpected upload pipeline fault");
                }
            },
            onPersist: null,
            CancellationToken.None);

        summary.Stopped.Should().BeFalse();
        summary.SuccessCount.Should().Be(1);
        summary.FailedCount.Should().Be(1);
        failedItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        failedItem.LastError.Should().Contain("unexpected upload pipeline fault");
        nextItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
        host.PublishedProjectDirs.Should().Equal(nextItem.ProjectDir);
    }

    [Fact]
    public async Task RunAsync_upload_only_prepares_current_project_proof_before_browser()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-proof",
            Name = "proof",
            TiktokCopyrightMaterialTypes = ["production_agreement"],
        };
        var store = CreateAccountStore(account);
        var item = CreateReadyToUploadItem(1, account);
        item.StepStates[QueueStepRegistry.GenerateProofMaterial] = QueueStepStatus.Pending;
        var host = new ImmediatePublishHost();
        var ensureCalls = 0;
        string? ensuredPath = null;
        QueueProofMaterialPrerequisite ensure = (project, _, _, _) =>
        {
            ensureCalls++;
            var workflow = ProjectWorkspaceService.EnsureWorkflowProjectDir(project.ProjectDir);
            ensuredPath = TikTokProofMaterialService.GetPdfPath(workflow);
            File.WriteAllBytes(ensuredPath, "%PDF-1.7\nproof"u8.ToArray());
            return Task.FromResult(ensuredPath);
        };
        var progress = new List<string>();

        var summary = await new QueueWorkerRunner(ensure).RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-proof-dependency-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: update => progress.Add(update.Message),
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(1, string.Join(Environment.NewLine, progress));
        summary.FailedCount.Should().Be(0);
        ensureCalls.Should().Be(1);
        ensuredPath.Should().Be(TikTokProofMaterialService.GetPdfPath(
            ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir));
        item.StepStates[QueueStepRegistry.GenerateProofMaterial].Should().Be(QueueStepStatus.Completed);
        item.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
        host.BrowserReadyCalls.Should().Be(1);
        progress.Should().Contain(message => message.Contains("上传前检查当前项目证明材料", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_reuses_completed_proof_without_invoking_generator()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-proof-completed",
            Name = "proof-completed",
            TiktokCopyrightMaterialTypes = ["production_agreement"],
        };
        var store = CreateAccountStore(account);
        var item = CreateReadyToUploadItem(1, account);
        var workflow = ProjectWorkspaceService.EnsureWorkflowProjectDir(item.ProjectDir);
        var proofPath = TikTokProofMaterialService.GetPdfPath(workflow);
        File.WriteAllBytes(proofPath, "%PDF-1.7\nproof"u8.ToArray());
        var host = new ImmediatePublishHost();
        var ensureCalls = 0;
        QueueProofMaterialPrerequisite ensure = (_, _, _, _) =>
        {
            ensureCalls++;
            return Task.FromException<string>(
                new InvalidOperationException("completed proof must not be regenerated"));
        };
        var progress = new List<string>();

        var summary = await new QueueWorkerRunner(ensure).RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-completed-proof-test"),
            [item],
            new QueueRunOptions
            {
                EnabledSteps =
                [
                    QueueStepRegistry.GenerateProofMaterial,
                    QueueStepRegistry.UploadSeries,
                ],
            },
            host,
            store,
            FinalAction.None,
            onProgress: update => progress.Add(update.Message),
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(1, string.Join(Environment.NewLine, progress));
        summary.FailedCount.Should().Be(0);
        ensureCalls.Should().Be(0);
        item.StepStates[QueueStepRegistry.GenerateProofMaterial].Should().Be(QueueStepStatus.Completed);
        item.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
        host.BrowserReadyCalls.Should().Be(1);
        progress.Should().Contain(message =>
            message.Contains("生成证明材料 已完成，跳过", StringComparison.Ordinal));
        progress.Should().Contain(message =>
            message.Contains("仅校验现有文件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_upload_only_fails_clearly_when_proof_preparation_fails()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-proof-failure",
            Name = "proof-failure",
            TiktokCopyrightMaterialTypes = ["production_agreement"],
        };
        var store = CreateAccountStore(account);
        var item = CreateReadyToUploadItem(1, account);
        item.StepStates[QueueStepRegistry.GenerateProofMaterial] = QueueStepStatus.Pending;
        var host = new ImmediatePublishHost();
        QueueProofMaterialPrerequisite ensure = (_, _, _, _) =>
            Task.FromException<string>(new InvalidOperationException("PDF renderer unavailable"));

        var summary = await new QueueWorkerRunner(ensure).RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-proof-failure-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(0);
        summary.FailedCount.Should().Be(1);
        host.BrowserReadyCalls.Should().Be(0);
        host.PublishedProjectDirs.Should().BeEmpty();
        item.StepStates[QueueStepRegistry.GenerateProofMaterial].Should().Be(QueueStepStatus.Failed);
        item.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        item.LastError.Should().Contain("上传合作协议前准备证明材料失败")
            .And.Contain("PDF renderer unavailable");
    }

    [Fact]
    public async Task RunAsync_repairs_stale_account_id_by_profile_name_instead_of_active_account()
    {
        var activeAccount = new TikTokAccountProfile
        {
            Id = "acct-active",
            Name = "账号1",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var targetAccount = new TikTokAccountProfile
        {
            Id = "acct-current-3",
            Name = "账号3",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore([activeAccount, targetAccount], activeAccount.Id);
        var item = CreateReadyToUploadItem(1, targetAccount);
        item.AccountProfileId = "acct-deleted-3";
        item.AccountProfileName = targetAccount.Name;
        var host = new ImmediatePublishHost();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-stale-account-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(1);
        summary.FailedCount.Should().Be(0);
        host.PublishedAccountIds.Should().Equal(targetAccount.Id);
        item.AccountProfileId.Should().Be(targetAccount.Id);
        item.AccountProfileName.Should().Be(targetAccount.DisplayName);
    }

    [Fact]
    public async Task RunAsync_repairs_reused_default_id_by_saved_profile_name_after_account_reset()
    {
        var resetDefaultAccount = new TikTokAccountProfile
        {
            Id = "default",
            Name = "重置后默认账号",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var configuredAccount = new TikTokAccountProfile
        {
            Id = "acct-new-company",
            Name = "武汉斑铬科技有限公司",
            TiktokCopyrightMaterialTypes = ["production_agreement"],
            TiktokProofCopyrightCompanyName = "武汉斑铬科技有限公司",
            TiktokProofDeclarantCompanyName = "湖北斑派科技有限公司",
            TiktokProofSealPath = @"C:\proof\seal.png",
        };
        var store = CreateAccountStore(
            [resetDefaultAccount, configuredAccount],
            resetDefaultAccount.Id);
        var item = CreateReadyToUploadItem(1, configuredAccount);
        item.StepStates[QueueStepRegistry.GenerateProofMaterial] = QueueStepStatus.Pending;
        item.AccountProfileId = "default";
        item.AccountProfileName = configuredAccount.DisplayName;
        var host = new ImmediatePublishHost();
        TikTokAccountProfile? proofAccount = null;
        QueueProofMaterialPrerequisite ensure = (project, account, _, _) =>
        {
            proofAccount = account;
            var workflow = ProjectWorkspaceService.EnsureWorkflowProjectDir(project.ProjectDir);
            var proofPath = TikTokProofMaterialService.GetPdfPath(workflow);
            File.WriteAllBytes(proofPath, "%PDF-1.7\nproof"u8.ToArray());
            return Task.FromResult(proofPath);
        };

        var summary = await new QueueWorkerRunner(ensure).RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-reset-default-account-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(1);
        summary.FailedCount.Should().Be(0);
        proofAccount.Should().BeSameAs(configuredAccount);
        proofAccount!.TiktokProofCopyrightCompanyName.Should().Be("武汉斑铬科技有限公司");
        proofAccount.TiktokProofDeclarantCompanyName.Should().Be("湖北斑派科技有限公司");
        host.PublishedAccountIds.Should().Equal(configuredAccount.Id);
        item.AccountProfileId.Should().Be(configuredAccount.Id);
        item.AccountProfileName.Should().Be(configuredAccount.DisplayName);
    }

    [Fact]
    public async Task RunAsync_keeps_stable_account_id_when_saved_name_points_to_another_account()
    {
        var idAccount = new TikTokAccountProfile
        {
            Id = "acct-stable",
            Name = "已改名账号",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var nameAccount = new TikTokAccountProfile
        {
            Id = "acct-other",
            Name = "旧账号名",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore([idAccount, nameAccount], idAccount.Id);
        var item = CreateReadyToUploadItem(1, idAccount);
        item.AccountProfileName = nameAccount.DisplayName;
        var host = new ImmediatePublishHost();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-stable-account-id-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(1);
        host.PublishedAccountIds.Should().Equal(idAccount.Id);
        item.AccountProfileId.Should().Be(idAccount.Id);
        item.AccountProfileName.Should().Be(idAccount.DisplayName);
    }

    [Fact]
    public async Task RunAsync_fails_stale_account_binding_instead_of_falling_back_to_active_account()
    {
        var activeAccount = new TikTokAccountProfile
        {
            Id = "acct-active",
            Name = "账号1",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(activeAccount);
        var item = CreateReadyToUploadItem(1, activeAccount);
        item.AccountProfileId = "acct-deleted-3";
        item.AccountProfileName = "账号3";
        var host = new ImmediatePublishHost();

        var summary = await new QueueWorkerRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-missing-account-test"),
            [item],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        summary.SuccessCount.Should().Be(0);
        summary.FailedCount.Should().Be(1);
        host.PublishedProjectDirs.Should().BeEmpty();
        item.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
        item.LastError.Should().Contain("避免误用当前账号");
    }

    [Fact]
    public async Task RunAsync_fails_before_browser_when_source_episode_count_is_incomplete()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"tiktok-queue-runner-episode-check-{Guid.NewGuid():N}");
        var projectDir = Path.Combine(workspace, "source");
        Directory.CreateDirectory(projectDir);

        try
        {
            for (var episode = 1; episode <= 99; episode++)
                File.WriteAllBytes(Path.Combine(projectDir, $"show-第{episode}集.mp4"), [1]);

            var account = new TikTokAccountProfile
            {
                Id = "acct-test",
                Name = "test",
                TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
            };
            var store = CreateAccountStore(account);
            var item = CreateReadyToUploadItem(1, account);
            item.ProjectDir = projectDir;
            item.EpisodeCount = 100;
            var host = new ImmediatePublishHost();
            var progressMessages = new List<string>();

            var summary = await new QueueWorkerRunner().RunAsync(
                workspace,
                [item],
                new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
                host,
                store,
                FinalAction.None,
                onProgress: progress => progressMessages.Add(progress.Message),
                onPersist: null,
                CancellationToken.None);

            summary.SuccessCount.Should().Be(0);
            summary.FailedCount.Should().Be(1);
            host.BrowserReadyCalls.Should().Be(0);
            host.PublishedProjectDirs.Should().BeEmpty();
            item.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Failed);
            string.Join(Environment.NewLine, progressMessages).Should().Contain("第 100 集");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task RunAsync_appends_existing_completed_project_while_queue_is_running()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var runningItem = CreateReadyToUploadItem(1, account);
        var completedItem = CreateCompletedItem(2, account);
        var appendItem = CreateReadyToUploadItem(2, account);
        appendItem.ProjectDir = completedItem.ProjectDir;
        appendItem.DisplayName = completedItem.DisplayName;
        appendItem.OriginalTitle = completedItem.OriginalTitle;
        appendItem.NewTitle = completedItem.NewTitle;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new BlockingPublishHost();
        var runner = new QueueWorkerRunner();
        var runTask = runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-append-existing-test"),
            [runningItem, completedItem],
            new QueueRunOptions { EnabledSteps = [QueueStepRegistry.UploadSeries] },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            cts.Token,
            projectDirFilter: [runningItem.ProjectDir]);

        await host.WaitForPublishCountAsync(1).WaitAsync(cts.Token);
        runner.AddItems([appendItem]).Should().Be(1);

        host.ReleaseNext();
        await host.WaitForPublishCountAsync(2).WaitAsync(cts.Token);
        host.ReleaseNext();

        var summary = await runTask.WaitAsync(cts.Token);

        summary.FailedCount.Should().Be(0);
        host.PublishedProjectDirs.Should().Equal(runningItem.ProjectDir, completedItem.ProjectDir);
        completedItem.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Completed);
    }

    [Fact]
    public async Task RunAsync_preserves_fifo_when_multiple_batches_are_appended_to_the_queue_tail()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var original = CreateReadyToUploadItem(1, account);
        var firstBatch = new[]
        {
            CreateReadyToUploadItem(2, account),
            CreateReadyToUploadItem(3, account),
        };
        var secondBatch = new[]
        {
            CreateReadyToUploadItem(4, account),
            CreateReadyToUploadItem(5, account),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new BlockingPublishHost();
        var runner = new QueueWorkerRunner();
        var runTask = runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-append-batches-test"),
            [original],
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 1,
            },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            cts.Token);

        await host.WaitForPublishCountAsync(1).WaitAsync(cts.Token);
        runner.AddItems(firstBatch).Should().Be(2);
        runner.AddItems(secondBatch).Should().Be(2);

        for (var expectedCount = 2; expectedCount <= 5; expectedCount++)
        {
            host.ReleaseNext();
            await host.WaitForPublishCountAsync(expectedCount).WaitAsync(cts.Token);
        }
        host.ReleaseNext();

        var summary = await runTask.WaitAsync(cts.Token);

        summary.TotalCount.Should().Be(5);
        summary.SuccessCount.Should().Be(5);
        summary.FailedCount.Should().Be(0);
        host.PublishedProjectDirs.Should().Equal(
            original.ProjectDir,
            firstBatch[0].ProjectDir,
            firstBatch[1].ProjectDir,
            secondBatch[0].ProjectDir,
            secondBatch[1].ProjectDir);
    }

    [Fact]
    public async Task AddItems_returns_zero_after_the_runner_has_closed_its_queue_tail()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var original = CreateReadyToUploadItem(1, account);
        var lateItem = CreateReadyToUploadItem(2, account);
        var host = new ImmediatePublishHost();
        var runner = new QueueWorkerRunner();

        var summary = await runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-closed-tail-test"),
            [original],
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 1,
            },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: null,
            CancellationToken.None);

        runner.AddItems([lateItem]).Should().Be(0);
        summary.TotalCount.Should().Be(1);
        summary.SuccessCount.Should().Be(1);
        host.PublishedProjectDirs.Should().Equal(original.ProjectDir);
    }

    [Fact]
    public async Task RunAsync_keeps_an_accepted_tail_item_retryable_when_the_queue_is_cancelled()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
            TiktokCopyrightMaterialTypes = ["work_registration_certificate"],
        };
        var store = CreateAccountStore(account);
        var original = CreateReadyToUploadItem(1, account);
        var appended = CreateReadyToUploadItem(2, account);
        var rejectedAfterStop = CreateReadyToUploadItem(3, account);
        var items = new List<QueueProjectItem> { original };
        IReadOnlyList<QueueProjectItem> lastPersisted = [];

        using var queueCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new BlockingPublishHost();
        var runner = new QueueWorkerRunner();
        var runTask = runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "tiktok-queue-runner-cancel-appended-tail-test"),
            items,
            new QueueRunOptions
            {
                EnabledSteps = [QueueStepRegistry.UploadSeries],
                ProjectConcurrency = 1,
            },
            host,
            store,
            FinalAction.None,
            onProgress: null,
            onPersist: snapshot => lastPersisted = snapshot
                .Select(item => QueueProjectItem.FromPayload(item.ToPayload()))
                .ToList(),
            queueCts.Token);

        await host.WaitForPublishCountAsync(1).WaitAsync(timeoutCts.Token);
        runner.AddItems([appended]).Should().Be(1);

        queueCts.Cancel();
        var summary = await runTask.WaitAsync(timeoutCts.Token);

        summary.Stopped.Should().BeTrue();
        runner.AddItems([rejectedAfterStop]).Should().Be(0);

        var retained = items.Should().ContainSingle(item =>
            string.Equals(item.ProjectDir, appended.ProjectDir, StringComparison.OrdinalIgnoreCase)).Subject;
        retained.StatusText.Should().Be(QueueStepStatus.Stopped);
        retained.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Pending);

        var persisted = lastPersisted.Should().ContainSingle(item =>
            string.Equals(item.ProjectDir, appended.ProjectDir, StringComparison.OrdinalIgnoreCase)).Subject;
        persisted.StatusText.Should().Be(QueueStepStatus.Stopped);
        persisted.StepStates[QueueStepRegistry.UploadSeries].Should().Be(QueueStepStatus.Pending);
    }

    private static QueueProjectItem CreateReadyToUploadItem(int index, TikTokAccountProfile account)
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"tiktok-ready-upload-{Guid.NewGuid():N}-{index}");
        Directory.CreateDirectory(projectDir);
        var videoPath = Path.Combine(projectDir, "episode-1.mp4");
        File.WriteAllBytes(videoPath, TinyMp4Bytes.Value);

        var item = new QueueProjectItem
        {
            ProjectDir = projectDir,
            OriginalTitle = $"original-{index}",
            NewTitle = $"title-{index}",
            EpisodeCount = 1,
            PrimaryVideoPath = videoPath,
            AccountProfileId = account.Id,
            AccountProfileName = account.DisplayName,
            Enabled = true,
            StatusText = QueueStepStatus.Completed,
        };
        item.NormalizeStepStates();
        foreach (var step in QueueStepRegistry.All)
            item.StepStates[step.Key] = step.Key == QueueStepRegistry.UploadSeries
                ? QueueStepStatus.Pending
                : QueueStepStatus.Completed;
        return item;
    }

    private static byte[] CreateTinyMp4Bytes()
    {
        var ffmpeg = ResolveFfmpegForTests();
        var binDir = Path.GetDirectoryName(ffmpeg);
        if (!string.IsNullOrWhiteSpace(binDir))
            PrependPath(binDir);

        var dir = Path.Combine(Path.GetTempPath(), $"tiktok-runner-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "tiny.mp4");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "-y",
                     "-f", "lavfi",
                     "-i", "color=c=black:s=16x16:r=1:d=16",
                     "-an",
                     "-c:v", "libx264",
                     "-pix_fmt", "yuv420p",
                     output,
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start ffmpeg for queue runner tests.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"ffmpeg failed to create test video: {stderr}");

        var minSize = 5L * 1024 * 1024 + 4096;
        var info = new FileInfo(output);
        if (info.Length < minSize)
        {
            using var stream = new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.SetLength(minSize);
        }

        return File.ReadAllBytes(output);
    }

    private static string ResolveFfmpegForTests()
    {
        var root = FindRepositoryRoot();
        var bundled = Path.Combine(root, "packaging", "dependencies", "tools", "win-x64", "ffmpeg", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "convertTools.sln")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void PrependPath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => string.Equals(Path.GetFullPath(part), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)))
            return;

        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + path);
    }

    private static QueueProjectItem CreateCompletedItem(int index, TikTokAccountProfile account)
    {
        var item = CreateReadyToUploadItem(index, account);
        item.StatusText = QueueStepStatus.Completed;
        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
        foreach (var step in QueueStepRegistry.All)
            item.StepStates[step.Key] = QueueStepStatus.Completed;
        return item;
    }

    private static AccountStore CreateAccountStore(TikTokAccountProfile account) =>
        CreateAccountStore([account], account.Id);

    private static AccountStore CreateAccountStore(
        IEnumerable<TikTokAccountProfile> profiles,
        string activeAccountId)
    {
        var store = new AccountStore();
        var accountsField = typeof(AccountStore).GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore account field not found.");
        var activeField = typeof(AccountStore).GetField("_activeAccountId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore active account field not found.");
        var accounts = (List<TikTokAccountProfile>)accountsField.GetValue(store)!;
        accounts.AddRange(profiles);
        activeField.SetValue(store, activeAccountId);
        return store;
    }

    private sealed class ImmediatePublishHost : IQueuePublishHost
    {
        public List<string> PublishedProjectDirs { get; } = new();
        public List<string> PublishedAccountIds { get; } = new();
        public int BrowserReadyCalls { get; private set; }

        public Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
            TikTokAccountProfile account,
            Action<string>? log,
            CancellationToken ct)
        {
            BrowserReadyCalls++;
            return Task.FromResult(QueueBrowserReadyResult.Ready());
        }

        public Task<PublishResult> PublishProjectAsync(
            TikTokAccountProfile account,
            QueueProjectItem project,
            FinalAction finalAction,
            QueueRunOptions options,
            Action<string> log,
            CancellationToken ct)
        {
            PublishedProjectDirs.Add(project.ProjectDir);
            PublishedAccountIds.Add(account.Id);
            return Task.FromResult(PublishResult.Success("ok"));
        }
    }

    private sealed class FirstUploadFailsHost : IQueuePublishHost
    {
        private readonly bool _throwException;
        private int _publishAttempts;

        public FirstUploadFailsHost(bool throwException)
        {
            _throwException = throwException;
        }

        public List<string> PublishedProjectDirs { get; } = new();

        public Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
            TikTokAccountProfile account,
            Action<string>? log,
            CancellationToken ct) =>
            Task.FromResult(QueueBrowserReadyResult.Ready());

        public Task<PublishResult> PublishProjectAsync(
            TikTokAccountProfile account,
            QueueProjectItem project,
            FinalAction finalAction,
            QueueRunOptions options,
            Action<string> log,
            CancellationToken ct)
        {
            PublishedProjectDirs.Add(project.ProjectDir);
            if (Interlocked.Increment(ref _publishAttempts) != 1)
                return Task.FromResult(PublishResult.Success("ok"));

            const string message = "TimeoutException: Timeout 10000ms exceeded.";
            return _throwException
                ? Task.FromException<PublishResult>(new TimeoutException(message))
                : Task.FromResult(PublishResult.Fail(message));
        }
    }

    private sealed class DailyLimitPublishHost : IQueuePublishHost
    {
        public List<string> PublishedProjectDirs { get; } = new();

        public Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
            TikTokAccountProfile account,
            Action<string>? log,
            CancellationToken ct) =>
            Task.FromResult(QueueBrowserReadyResult.Ready());

        public Task<PublishResult> PublishProjectAsync(
            TikTokAccountProfile account,
            QueueProjectItem project,
            FinalAction finalAction,
            QueueRunOptions options,
            Action<string> log,
            CancellationToken ct)
        {
            PublishedProjectDirs.Add(project.ProjectDir);
            return Task.FromResult(PublishResult.FailAndStopQueue("TikTok 单日创建剧集上限：今日额度已用完"));
        }
    }

    private sealed class BlockingPublishHost : IQueuePublishHost
    {
        private readonly object _lock = new();
        private readonly Queue<TaskCompletionSource> _releaseQueue = new();
        private readonly List<(int Count, TaskCompletionSource Source)> _waiters = new();

        public List<string> PublishedProjectDirs { get; } = new();

        public Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
            TikTokAccountProfile account,
            Action<string>? log,
            CancellationToken ct) =>
            Task.FromResult(QueueBrowserReadyResult.Ready());

        public async Task<PublishResult> PublishProjectAsync(
            TikTokAccountProfile account,
            QueueProjectItem project,
            FinalAction finalAction,
            QueueRunOptions options,
            Action<string> log,
            CancellationToken ct)
        {
            TaskCompletionSource release;
            lock (_lock)
            {
                PublishedProjectDirs.Add(project.ProjectDir);
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseQueue.Enqueue(release);
                CompleteSatisfiedWaiters();
            }

            await release.Task.WaitAsync(ct);
            return PublishResult.Success("ok");
        }

        public Task WaitForPublishCountAsync(int count)
        {
            lock (_lock)
            {
                if (PublishedProjectDirs.Count >= count)
                    return Task.CompletedTask;

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, source));
                return source.Task;
            }
        }

        public void ReleaseNext()
        {
            TaskCompletionSource? release = null;
            lock (_lock)
            {
                if (_releaseQueue.Count > 0)
                    release = _releaseQueue.Dequeue();
            }

            release?.SetResult();
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var (count, source) = _waiters[index];
                if (PublishedProjectDirs.Count < count)
                    continue;

                _waiters.RemoveAt(index);
                source.SetResult();
            }
        }
    }
}
