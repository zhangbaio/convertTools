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
        tiktok.Should().Contain("pagePipelineTask");
        shortDrama.Should().Contain("LoadProgressiveHighNewReleaseAsync");
        shortDrama.Should().Contain("enrich: false");
        shortDrama.Should().Contain("正在后台补充详情");
    }

    [Fact]
    public void TikTok_new_release_pages_are_enriched_by_a_single_fifo_consumer_before_display()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "ViewModels", "DramaDownloadViewModel.cs"));

        source.Should().Contain("Channel.CreateUnbounded<IReadOnlyList<DramaSearchItem>>");
        source.Should().Contain("SingleReader = true");
        source.Should().Contain("await foreach (var pageItems in pageQueue.Reader.ReadAllAsync");
        source.Should().Contain("await ShortDramaDramaServices.EnrichHighNewReleaseItemsAsync");
        source.Should().Contain("FilterEnrichedHighNewReleaseItems");
        source.Should().Contain("AppendLoadedSearchItems(filtered, sourceMode)");
        source.Should().Contain("foreach (var page in pageItems.Chunk(20))");
        source.Should().Contain("await pagePipelineTask");
        source.Should().NotContain("ConcurrentBag<Task<IReadOnlyList<DramaSearchItem>>>");

        source.IndexOf("await ShortDramaDramaServices.EnrichHighNewReleaseItemsAsync", StringComparison.Ordinal)
            .Should().BeLessThan(
                source.IndexOf("FilterEnrichedHighNewReleaseItems", StringComparison.Ordinal),
                "a page must be enriched before it becomes visible");
        source.IndexOf("FilterEnrichedHighNewReleaseItems", StringComparison.Ordinal)
            .Should().BeLessThan(
                source.IndexOf("AppendLoadedSearchItems(filtered, sourceMode)", StringComparison.Ordinal),
                "the authoritative date window must be applied before display");
    }

    [Fact]
    public void TikTok_progressive_new_release_replaces_previous_search_results_before_loading_pages()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "ViewModels", "DramaDownloadViewModel.cs"));
        var methodStart = source.IndexOf(
            "private async Task LoadProgressiveHighNewReleaseAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task CompleteHighNewReleaseAsync",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        method.Should().Contain("ReplaceLoadedSearchItems([], sourceMode, preserveSelection: false)");
        method.IndexOf("ReplaceLoadedSearchItems([], sourceMode, preserveSelection: false)", StringComparison.Ordinal)
            .Should().BeLessThan(
                method.IndexOf("AppendLoadedSearchItems(filtered, sourceMode)", StringComparison.Ordinal),
                "a new progressive query must discard rows from the previous search before appending its pages");
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
