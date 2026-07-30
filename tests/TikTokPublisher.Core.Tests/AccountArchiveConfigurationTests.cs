using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class AccountArchiveConfigurationTests
{
    [Fact]
    public void Account_archive_root_defaults_to_workspace_archive()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "tiktok-account-workspace");
        var account = new TikTokAccountProfile();

        account.ResolveArchiveRootPath(workspace)
            .Should().Be(Path.Combine(Path.GetFullPath(workspace), "archive"));
        TikTokArchivedProjectService.ResolveArchiveRoot(workspace)
            .Should().Be(Path.Combine(Path.GetFullPath(workspace), "archive"));
    }

    [Fact]
    public void Account_archive_root_prefers_account_specific_path()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "tiktok-account-workspace");
        var archive = Path.Combine(Path.GetTempPath(), "tiktok-account-archive");
        var account = new TikTokAccountProfile
        {
            TiktokArchiveRootDir = archive,
        };

        account.ResolveArchiveRootPath(workspace).Should().Be(Path.GetFullPath(archive));
        TikTokArchivedProjectService.ResolveArchiveRoot(
                workspace,
                account.ResolveArchiveRootPath(workspace))
            .Should().Be(Path.GetFullPath(archive));
    }

    [Fact]
    public void Legacy_global_archive_root_is_migrated_once_without_overwriting_account_value()
    {
        var inherited = new TikTokAccountProfile();
        var configured = new TikTokAccountProfile
        {
            TiktokArchiveRootDir = @"D:\account-archive",
        };
        var legacySettings = new ClientSettings
        {
            ArchiveRootDir = @"E:\legacy-archive",
        };

        AccountStore.ApplyLegacyArchiveRootConfig(
                [inherited, configured],
                legacySettings)
            .Should().BeTrue();

        inherited.TiktokArchiveRootDir.Should().Be(@"E:\legacy-archive");
        configured.TiktokArchiveRootDir.Should().Be(@"D:\account-archive");
        inherited.TiktokArchiveRootConfigMigrated.Should().BeTrue();
        configured.TiktokArchiveRootConfigMigrated.Should().BeTrue();

        legacySettings.ArchiveRootDir = @"F:\changed";
        AccountStore.ApplyLegacyArchiveRootConfig(
                [inherited, configured],
                legacySettings)
            .Should().BeFalse();
        inherited.TiktokArchiveRootDir.Should().Be(@"E:\legacy-archive");
    }

    [Fact]
    public void New_account_is_marked_as_archive_config_migrated()
    {
        var method = typeof(AccountStore).GetMethod(
            "CreateProfileSkeleton",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic);

        var account = (TikTokAccountProfile)method!.Invoke(
            null,
            ["acct-test", "测试账号"])!;

        account.TiktokArchiveRootConfigMigrated.Should().BeTrue();
        account.TiktokArchiveRootDir.Should().BeEmpty();
    }
}
