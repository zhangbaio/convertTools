using TikTokPublisher.Core.Drama;
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
        viewModel.PickTikTokQueueTargetsAsync = (options, currentId) =>
        {
            pickerOpened = true;
            Assert.Equal(targets.Where(target => platformKey is null || target.PlatformKey == platformKey), options);
            Assert.Equal(platformKey is null ? "tiktok-1" : $"{platformKey}-2", currentId);
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

    public void Dispose()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
