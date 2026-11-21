using System.Text.Json;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Publishing;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class ChannelsPublishRunStateTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
    private string StatePath => Path.Combine(_directory.FullName, "publish-run-state.json");

    [Fact]
    public async Task Resume_skips_legacy_records_before_connecting_the_browser()
    {
        var job = CreateJob("account", "素材.mp4");
        var signature = PublishRunStateStore.SignatureFor(job.Account.Id, job.Items[0]);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [signature] = new { At = "2026-01-01T00:00:00Z", Video = "素材.mp4", Account = "account" },
        }));
        File.WriteAllText(StatePath + ".interrupted.tmp", "{");
        job = job with { ResolveCdpEndpoint = _ => throw new InvalidOperationException("已有记录时无需浏览器") };
        var automation = SuccessfulAutomation();
        var progress = new List<PublishProgress>();

        await RunAsync(automation, job, progress.Add, resume: true);

        Assert.Equal(0, automation.Calls);
        var skipped = Assert.Single(progress);
        Assert.True(skipped.Done && skipped.Ok);
        Assert.Contains("续传跳过", skipped.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"key\":null}")]
    public async Task Corrupt_state_stops_submission_and_preserves_the_file(string contents)
    {
        File.WriteAllText(StatePath, contents);
        var automation = SuccessfulAutomation();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(automation, CreateJob("account", "素材.mp4")));

        Assert.Contains(StatePath, error.Message);
        Assert.Equal(0, automation.Calls);
        Assert.Equal(contents, File.ReadAllText(StatePath));
    }

    [Fact]
    public async Task An_interrupted_submission_stays_pending_and_blocks_replay_after_reopening()
    {
        var job = CreateJob("account", "素材.mp4");
        using var cancellation = new CancellationTokenSource();
        var interrupted = new FakeAutomation((beforeSubmit, ct) =>
        {
            beforeSubmit!();
            using var state = JsonDocument.Parse(File.ReadAllText(StatePath));
            var signature = PublishRunStateStore.SignatureFor(job.Account.Id, job.Items[0]);
            Assert.True(state.RootElement.GetProperty(signature).GetProperty("Pending").GetBoolean());
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("应当取消提交");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(interrupted, job, cancellationToken: cancellation.Token));

        var retry = SuccessfulAutomation();
        var progress = new List<PublishProgress>();
        await RunAsync(retry, job, progress.Add);

        Assert.Equal(0, retry.Calls);
        var blocked = Assert.Single(progress);
        Assert.True(blocked.Done);
        Assert.False(blocked.Ok);
        Assert.Contains("待确认", blocked.Message);
    }

    [Fact]
    public async Task A_failure_before_submission_can_be_retried()
    {
        var job = CreateJob("account", "素材.mp4");
        var failed = new FakeAutomation((_, _) => Task.FromResult(PublishResult.Fail("浏览器未登录")));
        await RunAsync(failed, job);
        Assert.False(File.Exists(StatePath));

        var retry = SuccessfulAutomation();
        await RunAsync(retry, job, resume: true);
        Assert.Equal(1, retry.Calls);
    }

    [Fact]
    public async Task Success_is_persisted_before_progress_is_reported()
    {
        var job = CreateJob("account", "素材.mp4");
        var completionReported = false;
        await RunAsync(SuccessfulAutomation(), job, progress =>
        {
            if (!progress.Done)
                return;
            completionReported = true;
            Assert.True(progress.Ok);
            Assert.Same(job.Items[0], progress.Item);
            using var state = JsonDocument.Parse(File.ReadAllText(StatePath));
            var signature = PublishRunStateStore.SignatureFor(progress.AccountId, job.Items[0]);
            Assert.False(state.RootElement.GetProperty(signature).TryGetProperty("Pending", out _));
        });

        Assert.True(completionReported);
    }

    [Fact]
    public async Task Parallel_successes_are_skipped_after_reopening()
    {
        var jobs = new[] { CreateJob("first", "素材.mp4"), CreateJob("second", "素材.mp4") };
        var automation = SuccessfulAutomation();
        await new PublishScheduler(automation, new PublishRunStateStore(StatePath))
            .RunAsync(jobs, FinalAction.Publish, 2, null, CancellationToken.None);
        Assert.Equal(2, automation.Calls);

        var retry = SuccessfulAutomation();
        await new PublishScheduler(retry, new PublishRunStateStore(StatePath))
            .RunAsync(jobs, FinalAction.Publish, 2, null, CancellationToken.None, resume: true);

        Assert.Equal(0, retry.Calls);
    }

    [Fact]
    public async Task A_failed_confirmation_write_preserves_the_pending_entry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var job = CreateJob("account", "素材.mp4");
        FileStream? openFile = null;
        var automation = new FakeAutomation((beforeSubmit, _) =>
        {
            beforeSubmit!();
            openFile = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(PublishResult.Success());
        });
        var progress = new List<PublishProgress>();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => RunAsync(automation, job, progress.Add));
        }
        finally
        {
            openFile?.Dispose();
        }

        var failed = Assert.Single(progress.Where(entry => entry.Done));
        Assert.False(failed.Ok);
        Assert.Contains("平台已确认提交", failed.Message);
        Assert.Empty(_directory.GetFiles("*.tmp"));
        var retry = SuccessfulAutomation();
        await RunAsync(retry, job, resume: true);
        Assert.Equal(0, retry.Calls);
    }

    [Fact]
    public async Task Another_run_and_reset_cannot_open_the_same_state_while_a_run_is_active()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new FakeAutomation(async (beforeSubmit, ct) =>
        {
            beforeSubmit!();
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return PublishResult.Success();
        });
        var job = CreateJob("account", "素材.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstRun = RunAsync(inFlight, job, cancellationToken: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var second = SuccessfulAutomation();
            await Assert.ThrowsAsync<IOException>(() => RunAsync(second, job));
            Assert.Equal(0, second.Calls);
            Assert.Throws<IOException>(() => new PublishRunStateStore(StatePath).Reset());
        }
        finally
        {
            release.TrySetResult();
            await firstRun.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task A_completed_draft_does_not_prevent_formal_publication()
    {
        var job = CreateJob("account", "素材.mp4");
        var automation = SuccessfulAutomation();
        var scheduler = new PublishScheduler(automation, new PublishRunStateStore(StatePath),
            scopeSubmissionByAction: true);

        await scheduler.RunAsync([job], FinalAction.Draft, 1, null, CancellationToken.None, resume: true);
        await scheduler.RunAsync([job], FinalAction.Draft, 1, null, CancellationToken.None, resume: true);
        await scheduler.RunAsync([job], FinalAction.Publish, 1, null, CancellationToken.None, resume: true);
        await scheduler.RunAsync([job], FinalAction.Publish, 1, null, CancellationToken.None, resume: true);

        Assert.Equal(2, automation.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_draft_result_blocks_publication_until_manually_resolved(bool platformSavedDraft)
    {
        var job = CreateJob("account", "素材.mp4");
        var state = new PublishRunStateStore(StatePath);
        var unknown = new FakeAutomation((beforeSubmit, _) =>
        {
            beforeSubmit!();
            return Task.FromResult(PublishResult.Fail("连接已断开"));
        });
        await new PublishScheduler(unknown, state, scopeSubmissionByAction: true)
            .RunAsync([job], FinalAction.Draft, 1, null, CancellationToken.None);

        var automation = SuccessfulAutomation();
        var scheduler = new PublishScheduler(automation, state, scopeSubmissionByAction: true);
        var progress = new List<PublishProgress>();
        await scheduler.RunAsync([job], FinalAction.Publish, 1, progress.Add, CancellationToken.None, resume: true);
        Assert.Equal(0, automation.Calls);
        Assert.True(Assert.Single(progress).SubmissionPending);

        Assert.True(state.ResolvePending(job.Account.Id, job.Items[0], submitted: platformSavedDraft));
        Assert.False(state.ResolvePending(job.Account.Id, job.Items[0], submitted: platformSavedDraft));

        await scheduler.RunAsync([job], FinalAction.Draft, 1, null, CancellationToken.None, resume: true);
        Assert.Equal(platformSavedDraft ? 0 : 1, automation.Calls);
    }

    private Task RunAsync(FakeAutomation automation, AccountPublishJob job, Action<PublishProgress>? progress = null,
        bool resume = false, CancellationToken cancellationToken = default) =>
        new PublishScheduler(automation, new PublishRunStateStore(StatePath))
            .RunAsync([job], FinalAction.Publish, 1, progress, cancellationToken, resume);

    private AccountPublishJob CreateJob(string account, string fileName) =>
        new(new PublishAccount { Id = account, Name = account }, "", [new PublishItem { VideoPath = Path.Combine(_directory.FullName, fileName) }]);

    private static FakeAutomation SuccessfulAutomation() => new(async (beforeSubmit, _) =>
    {
        beforeSubmit!();
        await Task.Yield();
        return PublishResult.Success();
    });

    private sealed class FakeAutomation(Func<Action?, CancellationToken, Task<PublishResult>> publish) : IPublishAutomation
    {
        private int _calls;
        public int Calls => _calls;

        public Task<PublishResult> PublishAsync(PublishItem item, string cdpEndpoint, FinalAction finalAction,
            Action<string>? log, CancellationToken ct, Action? beforeSubmit = null)
        {
            Interlocked.Increment(ref _calls);
            return publish(beforeSubmit, ct);
        }
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
