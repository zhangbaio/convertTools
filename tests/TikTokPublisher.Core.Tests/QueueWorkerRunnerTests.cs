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
            cts.Token);

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
