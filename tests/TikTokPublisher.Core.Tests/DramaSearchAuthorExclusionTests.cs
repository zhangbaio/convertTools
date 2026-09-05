using System.Reflection;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Ui.ViewModels;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaSearchAuthorExclusionTests
{
    [Fact]
    public void Author_exclusion_keeps_result_visible_but_blocks_queue_import()
    {
        var viewModel = new DramaDownloadViewModel
        {
            AuthorExclude = "河马",
            SearchKeyword = "我有六个黄毛爹"
        };
        var item = new DramaSearchItem
        {
            BookId = "hghigh:target",
            Title = "我有六个黄毛爹",
            Author = "河马剧场",
            EpisodeTotal = 72
        };

        var visible = Invoke<IReadOnlyList<DramaSearchItem>>(
            viewModel,
            "ApplySearchFilters",
            (object)new[] { item });
        var queued = Invoke<List<DramaSearchItem>>(
            viewModel,
            "FilterAuthorExcludedItems",
            new List<DramaSearchItem> { item },
            "TikTok 队列");

        Assert.Same(item, Assert.Single(visible));
        Assert.Empty(queued);
    }

    private static T Invoke<T>(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return (T)(method.Invoke(target, arguments) ?? throw new InvalidOperationException($"{methodName} returned null"));
    }

}
