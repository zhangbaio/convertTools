using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.ViewModels;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaDownloadQueueTargetTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "drama-download-target-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void VideoChannelTargetMessageDoesNotMentionTikTok()
    {
        var viewModel = new DramaDownloadViewModel(_databasePath);
        viewModel.ConfigureQueuePlatform("视频号");

        viewModel.UpdateTikTokQueueTarget(null);

        Assert.Equal("加入视频号队列", viewModel.AddToQueueButtonText);
        Assert.Contains("视频号上传工作目录", viewModel.TikTokQueueTargetText);
        Assert.DoesNotContain("TikTok", viewModel.TikTokQueueTargetText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetMessageShowsSelectedAccountAndItsWorkspace()
    {
        var viewModel = new DramaDownloadViewModel(_databasePath);
        viewModel.ConfigureQueuePlatform("视频号");

        viewModel.UpdateTikTokQueueTarget(new TikTokQueueImportTarget("account-1", "账号1", @"D:\账号1工作目录"));

        Assert.Equal(@"目标账号：账号1 · D:\账号1工作目录", viewModel.TikTokQueueTargetText);
    }

    [Theory]
    [InlineData(DramaQueuePlatforms.TikTok)]
    [InlineData(DramaQueuePlatforms.Kuaishou)]
    [InlineData(null)]
    public async Task QueueActionOpensPickerForItsPlatformOrAllPlatforms(string? platformKey)
    {
        var viewModel = new DramaDownloadViewModel(_databasePath);
        viewModel.ConfigurePlatformQueues([(DramaQueuePlatforms.TikTok, "TikTok"), (DramaQueuePlatforms.Kuaishou, "快手")]);
        var selectedDrama = new DramaSearchRowViewModel(new DramaSearchItem { Title = "测试短剧", Selected = true });
        viewModel.SearchResults.Add(selectedDrama);
        TikTokQueueImportTarget[] targets =
        [
            new("tiktok-1", "账号1", @"D:\TikTok1", DramaQueuePlatforms.TikTok, "TikTok"),
            new("tiktok-2", "账号2", @"D:\TikTok2", DramaQueuePlatforms.TikTok, "TikTok"),
            new("kuaishou-1", "账号1", @"D:\快手1", DramaQueuePlatforms.Kuaishou, "快手"),
            new("kuaishou-2", "账号2", @"D:\快手2", DramaQueuePlatforms.Kuaishou, "快手"),
        ];
        viewModel.TikTokQueueTargetsRequested += () => targets;
        viewModel.TikTokQueueTargetRequested += () => targets[0];
        viewModel.PlatformQueueTargetRequested += key => targets.Last(target => target.PlatformKey == key);
        var imported = false;
        viewModel.ImportToQueueRequested += _ =>
        {
            imported = true;
            return Task.CompletedTask;
        };
        var pickerOpened = false;
        viewModel.PickTikTokQueueTargetsAsync = (options, current, dramaCount, _) =>
        {
            pickerOpened = true;
            Assert.Equal(targets.Where(target => platformKey is null || target.PlatformKey == platformKey), options);
            Assert.Equal(platformKey is null ? "tiktok-1" : $"{platformKey}-2", current?.AccountProfileId);
            Assert.Equal(1, dramaCount);
            return Task.FromResult<IReadOnlyList<TikTokQueueImportTarget>?>(null);
        };

        if (platformKey is null)
            await viewModel.AddSelectedToMultipleTikTokQueuesCommand.ExecuteAsync(null);
        else
            await viewModel.PlatformQueueActions.Single(action => action.Key == platformKey).AddCommand.ExecuteAsync(platformKey);

        Assert.True(pickerOpened);
        Assert.False(imported);
        Assert.True(selectedDrama.Selected);
        Assert.Equal("加入队列时选择账号", viewModel.TikTokQueueTargetText);
    }

    [Fact]
    public async Task BatchImportPreviewsAndAssignsFiveDramasAcrossOneHundredAccounts()
    {
        using var batch = new BatchFixture(_databasePath, accountCount: 100, dramaCount: 5);
        var imports = new List<TikTokQueueImportRequest>();
        batch.Page.ConfirmQueueBatchAsync = (preview, hasDuplicates) =>
        {
            Assert.Contains("5 部剧 × 100 个账号，共 500 条", preview);
            Assert.False(hasDuplicates);
            Assert.All(batch.Targets, target => Assert.Empty(Directory.EnumerateFileSystemEntries(target.WorkspaceRoot)));
            return Task.FromResult<bool?>(true);
        };
        batch.Page.ImportToQueueRequested += request =>
        {
            imports.Add(request);
            return Task.CompletedTask;
        };
        batch.Page.ShowQueueBatchResultAsync = (summary, canRetry) =>
        {
            Assert.Contains("已入队 500 条", summary);
            Assert.False(canRetry);
            return Task.FromResult(false);
        };

        await batch.RunAsync();

        Assert.Equal(100, imports.Count);
        Assert.Equal(batch.Targets.Select(target => target.AccountProfileId), imports.Select(request => request.Target.AccountProfileId));
        Assert.All(imports, request =>
        {
            Assert.Equal(5, request.ProjectDirs.Count);
            Assert.All(request.ProjectDirs, path => Assert.Equal(request.Target.WorkspaceRoot, Directory.GetParent(path)?.FullName));
        });
        Assert.All(batch.Page.SearchResults, row => Assert.False(row.Selected));
    }

    [Fact]
    public async Task BatchImportRetryUsesOnlyUnwrittenProjects()
    {
        using var batch = new BatchFixture(_databasePath, accountCount: 2, dramaCount: 2);
        var calls = new List<TikTokQueueImportRequest>();
        var results = new List<string>();
        batch.Page.ImportToQueueRequested += request =>
        {
            calls.Add(request);
            var paths = calls.Count == 2 ? request.ProjectDirs.Take(1).ToArray() : request.ProjectDirs;
            var written = WorkspaceQueueService.AddProjectsToQueue(
                request.Target.WorkspaceRoot, paths, request.Target.AccountProfileId, request.Target.AccountProfileName);
            if (calls.Count == 2)
                throw new QueueImportPartialException(written.Select(item => item.ProjectDir));
            return Task.CompletedTask;
        };
        batch.Page.ShowQueueBatchResultAsync = (summary, canRetry) =>
        {
            results.Add(summary);
            return Task.FromResult(canRetry);
        };

        await batch.RunAsync();

        Assert.Equal(3, calls.Count);
        Assert.Equal(calls[1].Target, calls[2].Target);
        Assert.Equal(calls[1].ProjectDirs[1], Assert.Single(calls[2].ProjectDirs));
        Assert.Contains("已入队 3 条 · 跳过 0 条 · 失败 1 条", results[0]);
        Assert.Contains("已入队 4 条 · 跳过 0 条 · 失败 0 条", results[1]);
        foreach (var target in batch.Targets)
        {
            var snapshot = WorkspaceQueueService.LoadPersistedSnapshot(target.WorkspaceRoot);
            Assert.Equal(2, snapshot.Items.Count);
            Assert.All(snapshot.Items, item => Assert.Equal(target.AccountProfileId, item.AccountProfileId));
        }
    }

    [Fact]
    public async Task BatchPreviewCountsExistingProjectsAndUnavailableDirectories()
    {
        using var batch = new BatchFixture(_databasePath, accountCount: 2, dramaCount: 2);
        var existing = batch.Page.SearchResults[0].Item;
        await ShortDramaDramaServices.BootstrapAsync(batch.Targets[0].WorkspaceRoot, existing,
            "all", "1080P", 1, "source", "", CancellationToken.None);
        Directory.Delete(batch.Targets[1].WorkspaceRoot);
        string? preview = null;
        bool? hasDuplicates = null;
        TikTokQueueImportRequest? imported = null;
        batch.Page.ConfirmQueueBatchAsync = (summary, duplicates) =>
        {
            preview = summary;
            hasDuplicates = duplicates;
            return Task.FromResult<bool?>(true);
        };
        batch.Page.ImportToQueueRequested += request =>
        {
            imported = request;
            return Task.CompletedTask;
        };

        await batch.RunAsync();

        Assert.True(hasDuplicates);
        Assert.Contains("可直接入队 1 条 · 已存在 1 条 · 账号目录异常 2 条", preview);
        Assert.NotNull(imported);
        Assert.Equal(batch.Targets[0], imported.Target);
        Assert.Contains("集数测试剧 2", Assert.Single(imported.ProjectDirs));
    }

    [Fact]
    public async Task StoppingBatchLeavesRemainingAccountsUnwritten()
    {
        using var batch = new BatchFixture(_databasePath, accountCount: 2, dramaCount: 1);
        var imports = new List<TikTokQueueImportRequest>();
        string? result = null;
        batch.Page.ImportToQueueRequested += request =>
        {
            imports.Add(request);
            batch.Page.AddSelectedToMultipleTikTokQueuesCancelCommand.Execute(null);
            return Task.CompletedTask;
        };
        batch.Page.ShowQueueBatchResultAsync = (summary, _) =>
        {
            result = summary;
            return Task.FromResult(false);
        };

        await batch.RunAsync();

        Assert.Equal(batch.Targets[0], Assert.Single(imports).Target);
        Assert.Empty(Directory.EnumerateFileSystemEntries(batch.Targets[1].WorkspaceRoot));
        Assert.Contains("停止后未处理 1 条", result);
        Assert.True(Assert.Single(batch.Page.SearchResults).Selected);
    }

    [Fact]
    public async Task CancelingConfirmationLeavesProjectsAndSelectionIntact()
    {
        using var batch = new BatchFixture(_databasePath, accountCount: 2, dramaCount: 1);
        var called = false;
        batch.Page.ConfirmQueueBatchAsync = (_, _) =>
        {
            called = true;
            return Task.FromResult<bool?>(null);
        };
        batch.Page.ImportToQueueRequested += _ => throw new InvalidOperationException("Unexpected import");

        await batch.RunAsync();

        Assert.True(called);
        Assert.All(batch.Targets, target => Assert.Empty(Directory.EnumerateFileSystemEntries(target.WorkspaceRoot)));
        Assert.True(Assert.Single(batch.Page.SearchResults).Selected);
    }

    private sealed class BatchFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("multi-account-queue-");

        public BatchFixture(string databasePath, int accountCount, int dramaCount)
        {
            Page = new DramaDownloadViewModel(databasePath);
            Targets = Enumerable.Range(1, accountCount).Select(index => new TikTokQueueImportTarget(
                $"account-{index}", $"账号 {index}", _root.CreateSubdirectory($"account-{index}").FullName, DramaQueuePlatforms.TikTok)).ToArray();
            for (var index = 1; index <= dramaCount; index++)
            {
                Page.SearchResults.Add(new DramaSearchRowViewModel(new DramaSearchItem
                {
                    BookId = $"book-{index}",
                    Title = $"集数测试剧 {index}",
                    Selected = true,
                }));
            }
            Page.TikTokQueueTargetsRequested += () => Targets;
            Page.PickTikTokQueueTargetsAsync = (options, _, _, _) => Task.FromResult<IReadOnlyList<TikTokQueueImportTarget>?>(options);
            Page.ConfirmQueueBatchAsync = (_, _) => Task.FromResult<bool?>(true);
        }

        public DramaDownloadViewModel Page { get; }
        public TikTokQueueImportTarget[] Targets { get; }
        public Task RunAsync() => Page.AddSelectedToMultipleTikTokQueuesCommand.ExecuteAsync(null);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            _root.Delete(recursive: true);
        }
    }


    public void Dispose()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
