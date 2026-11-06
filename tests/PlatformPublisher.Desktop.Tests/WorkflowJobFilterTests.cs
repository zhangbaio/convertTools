using PlatformPublisher.Desktop.ViewModels;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class WorkflowJobFilterTests
{
    [Fact]
    public void Blank_query_keeps_every_row()
    {
        Assert.True(WorkflowJobFilter.Matches("  ", "暴雨分手十五年"));
    }

    [Fact]
    public void New_title_matches_even_when_the_folder_name_is_different()
    {
        Assert.True(WorkflowJobFilter.Matches(
            "晚星映月共余生",
            "暴雨分手十五年",
            "晚星映月共余生"));
        Assert.False(WorkflowJobFilter.Matches(
            "晚星映月共余生",
            "暴雨分手十五年",
            "怒江险路货运手"));
    }
}
