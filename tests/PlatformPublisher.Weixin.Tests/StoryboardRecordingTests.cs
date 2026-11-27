using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class StoryboardRecordingTests
{
    [Fact]
    public void SelectionUsesRealEpisodeNumbersAndRecordingPrefersFourThenOne()
    {
        Assert.Equal(new[] { 1, 3, 4, 7 }, StoryboardRecordingRules.SelectEpisodes("7，3-4、1,4", [1, 3, 4, 7]));
        Assert.Equal(new[] { 4, 1 }, StoryboardRecordingRules.SelectRecordingEpisodes([1, 3, 4, 7]));
        Assert.Equal(new[] { 7, 2 }, StoryboardRecordingRules.SelectRecordingEpisodes([2, 3, 7]));
        Assert.Equal(new[] { 8 }, StoryboardRecordingRules.SelectEpisodes("", [8]));
        Assert.Equal(17, StoryboardRecordingRules.EpisodeNumber("2026短剧-第17集.mp4"));
        Assert.Null(StoryboardRecordingRules.EpisodeNumber("2026素材.mp4"));
        Assert.Throws<ArgumentException>(() => StoryboardRecordingRules.ValidateSelection("3-1"));
        Assert.Contains("第2集", Assert.Throws<InvalidOperationException>(() =>
            StoryboardRecordingRules.SelectEpisodes("1-2147483647", [1, 3, 4])).Message);
    }

    [Fact]
    public async Task MissingEpisodeAndCancellationPreservePublishedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "weixin-storyboard-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "原始剧");
        var workflow = Path.Combine(root, "工作剧");
        var output = Path.Combine(workflow, WeixinStoryboardRecordingService.OutputDirectoryName);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "原始剧-第1集.mp4"), "present");
            var existing = Path.Combine(output, "原始剧操作视频.mp4");
            await File.WriteAllTextAsync(existing, "previous");
            var service = new WeixinStoryboardRecordingService(new UnusedScanner(), new UnusedBrowser());
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateInWorkflowAsync(
                source, workflow, "原始剧", new StoryboardRecordingOptions("1-2", Force: true), null, null, CancellationToken.None));
            Assert.Contains("第2集", missing.Message);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateInWorkflowAsync(
                source, workflow, "原始剧", new StoryboardRecordingOptions("1"), null, null, cancelled.Token));
            Assert.Equal("previous", await File.ReadAllTextAsync(existing));
            Assert.Equal(new[] { output }, Directory.GetDirectories(workflow));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class UnusedScanner : IProjectScanner
    {
        public Task<ProjectScanResult> ScanAsync(string rootDir, string? backupRootDir, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedBrowser : IWeixinBrowserRuntimeService
    {
        public Task<WeixinBrowserRuntimeStatus> InspectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void ConfigureEnvironment(WeixinBrowserRuntimeStatus status) => throw new NotSupportedException();
    }
}
