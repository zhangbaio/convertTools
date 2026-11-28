using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Persistence;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Services;
using Xunit;
using Account = ChannelsPublisher.Core.Models.PublishAccount;
using AccountStore = ChannelsPublisher.Core.Services.AccountStore;
using SourceDrama = ShortDrama.Core.Models.DramaSearchItem;

namespace PlatformPublisher.Desktop.Tests;

public sealed class WeixinTitleUploadWorkflowTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task OnlyTheNewValidatedRowsReachTheExistingConfirmation(bool confirm, bool stopDuringValidation, bool allInvalid)
    {
        var root = Directory.CreateTempSubdirectory("weixin-title-batch-");
        try
        {
            var account = new Account
            {
                Id = "first",
                Name = "视频号甲",
                IsWeixinWorkspaceAccount = true,
                ProfileDir = root.CreateSubdirectory("session").FullName,
                WeixinWorkRootDirectory = root.CreateSubdirectory("projects").FullName,
            };
            var database = new PlatformDatabase(Path.Combine(root.FullName, "app.db"));
            var jobs = new PublishJobStore(database, Path.Combine(root.FullName, "jobs.json"));
            var accountPath = Path.Combine(root.FullName, "accounts.json");
            await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(new[] { account }));
            var accounts = new AccountStore(database, accountPath);
            accounts.Load();
            var existing = new PublishJob
            {
                Id = "existing",
                AccountId = account.Id,
                ProjectName = "旧任务",
                ProjectDirectory = root.CreateSubdirectory("existing").FullName,
                IsChecked = true,
                StepStates = new()
                {
                    ["material-validate"] = new() { Key = "material-validate", Status = PublishJobStepStatus.Succeeded },
                },
            };
            await jobs.SaveAsync([existing], PublishJobScope.Weixin);
            using var shared = new ServiceCollection().AddSingleton(database).AddSingleton(accounts).AddSingleton(jobs).BuildServiceProvider();
            var providerType = typeof(MainWindowViewModel).Assembly.GetType("PlatformPublisher.Desktop.App")!;
            using var services = (ServiceProvider)providerType.GetMethod("BuildServices", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [true, shared])!;
            var settingsPath = Path.Combine(root.FullName, "settings.db");
            ClientSettingsStore.Save(new TikTokPublisher.Core.Models.ClientSettings { DramaSourceChain = "hongguoapp" }, settingsPath);
            var settings = new YunfanDramaSettingsProvider(settingsPath);
            var adapter = new RecordingAdapter();
            var validator = new RecordingValidator { RejectAll = allInvalid };
            var work = new RecordingWork();
            var vm = ActivatorUtilities.CreateInstance<MainWindowViewModel>(services, new PlatformPublishCoordinator([adapter]), validator,
                work, new DramaTitleImportService(new FakeSearch(), settings), settings, PublishJobScope.Weixin);
            try
            {
                await vm.Initialization;
                vm.DisableAllWeixinStepsCommand.Execute(null);
                vm.PipelineAutoArchiveAfterUpload = false;
                vm.PipelineSyncManagementAfterUpload = false;
                vm.WorkflowFilterText = "旧任务";
                var confirms = new List<int>();
                validator.StopSecond = stopDuringValidation ? vm.Stop : null;
                Func<int, Task<bool>> confirmUpload = count =>
                {
                    confirms.Add(count);
                    Assert.Empty(adapter.Uploads);
                    Assert.Equal(new[] { "通过剧", "校验失败剧" }, validator.Validated);
                    return Task.FromResult(confirm);
                };
                Invoke(vm, "BindEmbeddedWeixinWork", (Func<bool>)(() => true),
                    (Func<string, CancellationToken, Task>)((_, _) => Task.CompletedTask), confirmUpload);
                Invoke(vm, "UseEmbeddedWeixinAccount", accounts.Accounts, accounts.Accounts.Single());
                var target = Invoke(vm, "CaptureWeixinTitleUploadTarget")!;
                var summary = await (Task<string>)Invoke(vm, "UploadWeixinTitlesAsync", target,
                    "通过剧\n校验失败剧", UploadTitleImportService.MatchModeTitle)!;

                var saved = await jobs.LoadAsync(PublishJobScope.Weixin);
                Assert.Equal(3, saved.Count);
                Assert.Equal(0, saved.Single(job => job.Id == "existing").AttemptCount);
                Assert.Equal(2, work.Downloads.Count);
                Assert.Contains("通过剧", work.Downloads);
                Assert.Contains("校验失败剧", work.Downloads);
                Assert.Equal(stopDuringValidation || allInvalid ? Array.Empty<int>() : new[] { 1 }, confirms.ToArray());
                var good = saved.Single(job => job.ProjectName == "通过剧");
                var failed = saved.Single(job => job.ProjectName == "校验失败剧");
                Assert.Equal(allInvalid ? PublishJobStepStatus.Failed : PublishJobStepStatus.Succeeded,
                    good.StepStates["material-validate"].Status);
                Assert.Equal(stopDuringValidation ? PublishJobStepStatus.Pending : PublishJobStepStatus.Failed,
                    failed.StepStates["material-validate"].Status);
                Assert.Equal(account.Id, good.AccountId);
                if (allInvalid)
                {
                    Assert.Empty(adapter.Uploads);
                    Assert.Equal(PublishJobStatus.Failed, good.Status);
                    Assert.Contains("校验通过 0 部", summary);
                }
                else if (confirm && !stopDuringValidation)
                {
                    Assert.Equal(good.Id, Assert.Single(adapter.Uploads).Id);
                    Assert.Equal(PublishJobStatus.Succeeded, good.Status);
                }
                else
                {
                    Assert.Empty(adapter.Uploads);
                    Assert.Equal(PublishJobStatus.Pending, good.Status);
                    Assert.Contains(stopDuringValidation ? "已停止" : "已取消上传", summary);
                }
            }
            finally { vm.Shutdown(); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static object? Invoke(MainWindowViewModel vm, string method, params object?[] arguments) =>
        typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, arguments);

    private sealed class FakeSearch : IDramaSearchService
    {
        public Task<IReadOnlyList<SourceDrama>> SearchAsync(string keyword, int page, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceDrama>>([new(keyword, keyword, "短剧", 43, "简介", "")]);

        public Task<IReadOnlyList<SourceDrama>> GetTodayAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingValidator : IMaterialValidationService
    {
        public List<string> Validated { get; } = [];
        public Action? StopSecond { get; set; }
        public bool RejectAll { get; init; }

        public Task<MaterialValidationResult> ValidateAsync(string workflowProjectDir, CancellationToken cancellationToken)
        {
            var title = Path.GetFileName(workflowProjectDir);
            Validated.Add(title);
            if (title == "校验失败剧") StopSecond?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MaterialValidationResult(RejectAll || title == "校验失败剧"
                ? [new MaterialValidationIssue("fixture", "错误", "缺少分集")]
                : []));
        }
    }

    private sealed class RecordingAdapter : IPlatformPublishAdapter
    {
        public List<PublishJob> Uploads { get; } = [];
        public PublishPlatform Platform => PublishPlatform.WeixinChannel;
        public bool IsAvailable => true;
        public string AvailabilityMessage => "";

        public Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Uploads.Add(job);
            return Task.CompletedTask;
        }

        public Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingWork : IWorkService
    {
        public List<string> Downloads { get; } = [];

        public Task<ProjectWorkResult> RunProjectStepAsync(string sourceProjectDir, string? backupRootDir, string stepType,
            bool force, IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken, string? configOverridePath = null)
        {
            Assert.Equal("download", stepType);
            Assert.False(force);
            var title = Path.GetFileName(sourceProjectDir);
            Downloads.Add(title);
            return Task.FromResult(new ProjectWorkResult(title, title, sourceProjectDir, true, false, null, []));
        }

        public Task<string> EnsureWeixinUploadConfigAsync(string sourceProjectDir, string? backupRootDir, CancellationToken cancellationToken) =>
            Task.FromResult(Path.Combine(sourceProjectDir, "weixin-channel-submit.json"));

        public Task<WorkRunResult> RunAsync(string rootDir, string? backupRootDir, bool force, IProgress<WorkRunEvent>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectWorkResult> RunProjectAsync(string sourceProjectDir, string? backupRootDir, bool force,
            IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectTitleUpdateResult> UpdateProjectTitleAsync(string sourceProjectDir, string? backupRootDir, string newTitle,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RefreshWeixinConfigsAsync(string sourceProjectDir, string? backupRootDir, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProjectInfoAutoFillResult> AutoFillProjectInfoAsync(string sourceProjectDir, string? backupRootDir,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UploadRemuxResult> RemuxUploadVideosAsync(string sourceProjectDir, string? backupRootDir,
            IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
