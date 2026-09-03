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

    public void Dispose()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
