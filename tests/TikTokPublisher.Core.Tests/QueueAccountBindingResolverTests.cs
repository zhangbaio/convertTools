using System.Reflection;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueAccountBindingResolverTests
{
    [Fact]
    public void RepairForWorkspaceDefault_rebinds_deleted_account_to_explicit_workspace_account()
    {
        var activeAccount = new TikTokAccountProfile
        {
            Id = "acct-active",
            Name = "其他账号",
        };
        var workspaceAccount = new TikTokAccountProfile
        {
            Id = "acct-7c496ad8",
            Name = "账号2",
            TiktokLoginEmail = "2720937754@qq.com",
            TiktokProofCopyrightCompanyName = "湖北星跃宸科技有限公司",
            TiktokProofDeclarantCompanyName = "湖北箭派科技有限公司",
            TiktokProofSealPath = @"C:\proof\湖北箭派科技\3.png",
            TiktokProofAccountConfigMigrated = true,
        };
        var store = CreateAccountStore([activeAccount, workspaceAccount], activeAccount.Id);
        var item = new QueueProjectItem
        {
            AccountProfileId = "acct-60cf841c",
            AccountProfileName = "2",
        };

        var changed = QueueAccountBindingResolver.RepairForWorkspaceDefault(
            store,
            item,
            workspaceAccount);

        changed.Should().BeTrue();
        item.AccountProfileId.Should().Be(workspaceAccount.Id);
        item.AccountProfileName.Should().Be(workspaceAccount.DisplayName);
        var resolved = QueueAccountBindingResolver.Resolve(store, item);
        resolved.Should().BeSameAs(workspaceAccount);
        resolved!.TiktokProofCopyrightCompanyName.Should().Be("湖北星跃宸科技有限公司");
        resolved.TiktokProofDeclarantCompanyName.Should().Be("湖北箭派科技有限公司");
        resolved.TiktokProofSealPath.Should().Be(@"C:\proof\湖北箭派科技\3.png");
    }

    [Fact]
    public void RepairForWorkspaceDefault_preserves_valid_per_project_account_binding()
    {
        var workspaceAccount = new TikTokAccountProfile
        {
            Id = "acct-workspace",
            Name = "工作目录账号",
        };
        var projectAccount = new TikTokAccountProfile
        {
            Id = "acct-project",
            Name = "逐项目账号",
            TiktokLoginEmail = "project@example.com",
        };
        var store = CreateAccountStore([workspaceAccount, projectAccount], workspaceAccount.Id);
        var item = new QueueProjectItem
        {
            AccountProfileId = projectAccount.Id,
            AccountProfileName = projectAccount.DisplayName,
        };

        var changed = QueueAccountBindingResolver.RepairForWorkspaceDefault(
            store,
            item,
            workspaceAccount);

        changed.Should().BeFalse();
        item.AccountProfileId.Should().Be(projectAccount.Id);
        item.AccountProfileName.Should().Be(projectAccount.DisplayName);
        QueueAccountBindingResolver.Resolve(store, item).Should().BeSameAs(projectAccount);
    }

    [Fact]
    public void RepairForWorkspaceDefault_rebinds_reused_default_id_when_saved_name_has_no_match()
    {
        var reusedDefaultAccount = new TikTokAccountProfile
        {
            Id = "default",
            Name = "重置后的默认账号",
        };
        var workspaceAccount = new TikTokAccountProfile
        {
            Id = "acct-workspace",
            Name = "重新配置的工作目录账号",
        };
        var store = CreateAccountStore([reusedDefaultAccount, workspaceAccount], reusedDefaultAccount.Id);
        var item = new QueueProjectItem
        {
            AccountProfileId = "default",
            AccountProfileName = "已删除且无同名的新账号",
        };

        var changed = QueueAccountBindingResolver.RepairForWorkspaceDefault(
            store,
            item,
            workspaceAccount);

        changed.Should().BeTrue();
        item.AccountProfileId.Should().Be(workspaceAccount.Id);
        item.AccountProfileName.Should().Be(workspaceAccount.DisplayName);
        QueueAccountBindingResolver.Resolve(store, item).Should().BeSameAs(workspaceAccount);
    }

    private static AccountStore CreateAccountStore(
        IEnumerable<TikTokAccountProfile> profiles,
        string activeAccountId)
    {
        var store = new AccountStore();
        var accountsField = typeof(AccountStore).GetField(
            "_accounts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore account field not found.");
        var activeField = typeof(AccountStore).GetField(
            "_activeAccountId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AccountStore active account field not found.");
        var accounts = (List<TikTokAccountProfile>)accountsField.GetValue(store)!;
        accounts.AddRange(profiles);
        activeField.SetValue(store, activeAccountId);
        return store;
    }
}
