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

    private static AccountStore CreateAccountStore(TikTokAccountProfile account)
    {
        var store = new AccountStore();
        var accountsField = typeof(AccountStore).GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore account field not found.");
        var activeField = typeof(AccountStore).GetField("_activeAccountId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore active account field not found.");
        var accounts = (List<TikTokAccountProfile>)accountsField.GetValue(store)!;
        accounts.Add(account);
        activeField.SetValue(store, account.Id);
        return store;
    }

    private sealed class ImmediatePublishHost : IQueuePublishHost
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
            return Task.FromResult(PublishResult.Success("ok"));
        }
    }
}
