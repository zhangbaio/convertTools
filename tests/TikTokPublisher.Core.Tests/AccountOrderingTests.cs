using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[Collection(TikTokAccountInventorySyncCoordinatorTestCollection.Name)]
public sealed class AccountOrderingTests
{
    [Fact]
    public void Reorder_is_relative_to_the_target_and_survives_reload_with_the_same_active_account()
    {
        var root = Directory.CreateTempSubdirectory();
        var previous = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            var store = new AccountStore();
            var first = store.Add("第一个账号");
            var hidden = store.Add("搜索时未显示的账号");
            var selected = store.Add("当前账号");
            var last = store.Add("最后一个账号");
            store.SetActive(selected.Id);

            store.MoveRelativeTo(last.Id, first.Id, after: false).Should().Be(0);
            store.MoveRelativeTo(first.Id, selected.Id, after: true).Should().Be(3);
            store.MoveRelativeTo(first.Id, selected.Id, after: true).Should().BeNull();
            store.MoveRelativeTo(first.Id, "removed-account", after: false).Should().BeNull();

            var reloaded = new AccountStore();
            reloaded.Load();
            reloaded.Accounts.Select(account => account.Id).Should().Equal(last.Id, hidden.Id, selected.Id, first.Id);
            reloaded.ActiveAccountId.Should().Be(selected.Id);
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            root.Delete(recursive: true);
        }
    }
}
