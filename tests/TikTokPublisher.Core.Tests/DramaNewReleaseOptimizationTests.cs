using FluentAssertions;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaNewReleaseOptimizationTests
{
    [Fact]
    public void Both_desktop_entry_points_use_progressive_high_new_release_loading()
    {
        var tiktok = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "ViewModels", "DramaDownloadViewModel.cs"));
        var shortDrama = File.ReadAllText(FindRepoFile(
            "src", "ShortDrama", "ShortDrama.Desktop", "ViewModels", "MainWindowViewModel.cs"));

        tiktok.Should().Contain("LoadProgressiveHighNewReleaseAsync");
        tiktok.Should().Contain("enrich: false");
        tiktok.Should().Contain("正在后台补充详情");
        shortDrama.Should().Contain("LoadProgressiveHighNewReleaseAsync");
        shortDrama.Should().Contain("enrich: false");
        shortDrama.Should().Contain("正在后台补充详情");
    }

    [Fact]
    public void Poster_cache_keeps_bounded_download_concurrency()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Core", "Drama", "DramaPosterCache.cs"));
        source.Should().Contain("new(6, 6)");
        source.Should().Contain("Gate.WaitAsync");
    }

    private static string FindRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
