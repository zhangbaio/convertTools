using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class ArchivedProjectsViewModelTests
{
    [Fact]
    public void SearchText_filters_project_titles_immediately_and_ignores_case()
    {
        var vm = new ArchivedProjectsViewModel();
        vm.Rows.Add(Row("TikTok Project", "tiktok"));
        vm.Rows.Add(Row("kuaishou-project", "kuaishou"));

        vm.SearchText = "TIKTOK";

        vm.FilteredRows.Should().ContainSingle()
            .Which.DisplayName.Should().Be("TikTok Project");
        vm.StatusMessage.Should().Be("搜索结果: 1 / 2（关键词：TIKTOK）");
    }

    [Fact]
    public void SearchText_does_not_match_archive_source()
    {
        var vm = new ArchivedProjectsViewModel();
        vm.Rows.Add(Row("first", "tiktok"));
        vm.Rows.Add(Row("second", "tiktok"));

        vm.SearchText = "tiktok";

        vm.FilteredRows.Should().BeEmpty();
        vm.StatusMessage.Should().Be("搜索结果: 0 / 2（关键词：tiktok）");
    }

    private static ArchivedProjectRowViewModel Row(string name, string source) =>
        new(new ArchivedProjectItem(
            ProjectKey: name,
            DisplayName: name,
            OriginalTitle: name,
            NewTitle: name,
            ArchivedAt: "2026-08-28T00:00:00",
            QueuedAt: "2026-08-27T00:00:00",
            MetadataPath: $@"D:\archive\meta\{name}.json",
            ArchiveProjectDir: $@"D:\archive\meta\{name}.json",
            ArchiveSource: source,
            ArchivedSourceDir: $@"D:\archive\source\{name}",
            ArchivedWorkflowDir: $@"D:\archive\workflow\_{name}"));
}
