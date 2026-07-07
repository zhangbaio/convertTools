using System.Reflection;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueWorkerRunnerTests
{
    [Fact]
    public async Task RunAsync_uploads_all_projects_when_preupload_steps_complete_synchronously()
    {
        var account = new TikTokAccountProfile
        {
            Id = "acct-test",
            Name = "test",
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
    public async Task RunAsync_repairs_stale_account_id_by_profile_name_instead_of_active_account()
    {
        var activeAccount = new TikTokAccountProfile
        {
            Id = "acct-active",
            Name = "账号1",
        };
        var targetAccount = new TikTokAccountProfile
        {
            Id = "acct-current-3",
            Name = "账号3",
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
    public async Task RunAsync_fails_stale_account_binding_instead_of_falling_back_to_active_account()
    {
        var activeAccount = new TikTokAccountProfile
        {
            Id = "acct-active",
            Name = "账号1",
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

    private static QueueProjectItem CreateReadyToUploadItem(int index, TikTokAccountProfile account)
    {
        var item = new QueueProjectItem
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), $"tiktok-ready-upload-{index}"),
            OriginalTitle = $"original-{index}",
            NewTitle = $"title-{index}",
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
}
