using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Tests;

public sealed class AccountArchiveMigrationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "account-archive-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup for Windows file locks.
        }
    }

    [Fact]
    public async Task Migration_moves_only_current_account_projects_and_they_can_be_restored()
    {
        var workspace = Path.Combine(_tempRoot, "account-1-workspace");
        var otherWorkspace = Path.Combine(_tempRoot, "account-2-workspace");
        var legacyRoot = Path.Combine(_tempRoot, "legacy-global-archive");
        var targetRoot = Path.Combine(workspace, "archive");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(otherWorkspace);

        var account = new TikTokAccountProfile
        {
            Id = "acct-1",
            Name = "账号1",
            TiktokLoginEmail = "account1@example.com",
            TiktokUploadProfilePath = workspace,
        };
        var otherAccount = new TikTokAccountProfile
        {
            Id = "acct-2",
            Name = "账号2",
            TiktokLoginEmail = "account2@example.com",
            TiktokUploadProfilePath = otherWorkspace,
        };

        CreateArchive(
            legacyRoot,
            "current-by-id",
            "当前账号-ID",
            workspace,
            account.Id,
            account.Name);
        CreateArchive(
            legacyRoot,
            "current-by-path",
            "当前账号-路径",
            workspace,
            accountId: "",
            accountName: "");
        var other = CreateArchive(
            legacyRoot,
            "other-account",
            "其他账号",
            otherWorkspace,
            otherAccount.Id,
            otherAccount.Name);

        var preview = TikTokArchivedProjectService.BuildAccountArchiveMigrationPreview(
            workspace,
            legacyRoot,
            targetRoot,
            account,
            new[] { account, otherAccount });

        preview.SourceProjectCount.Should().Be(3);
        preview.MigratableCount.Should().Be(2);
        preview.SkippedOwnershipCount.Should().Be(1);
        preview.ConflictCount.Should().Be(0);
        preview.Candidates.Select(candidate => candidate.MatchKind).Should().Contain(
            new[]
            {
                AccountArchiveMigrationMatchKind.AccountId,
                AccountArchiveMigrationMatchKind.WorkspacePath,
            });

        var result = await TikTokArchivedProjectService.MigrateAccountArchivesAsync(
            workspace,
            preview,
            account);

        result.MigratedCount.Should().Be(2);
        result.FailedCount.Should().Be(0);
        Directory.Exists(other.SourceDir).Should().BeTrue();
        Directory.Exists(other.WorkflowDir).Should().BeTrue();
        File.Exists(other.MetadataPath).Should().BeTrue();

        var migrated = TikTokArchivedProjectService.List(workspace, targetRoot);
        migrated.Should().HaveCount(2);
        migrated.Should().OnlyContain(item => item.AccountProfileId == account.Id);
        migrated.Should().OnlyContain(item =>
            Path.GetFullPath(item.MetadataPath).StartsWith(
                Path.GetFullPath(targetRoot),
                StringComparison.OrdinalIgnoreCase));

        var restoredItem = migrated.Single(item => item.ProjectKey == "current-by-id");
        using (var document = JsonDocument.Parse(File.ReadAllText(restoredItem.MetadataPath)))
        {
            var root = document.RootElement;
            root.GetProperty("accountProfileId").GetString().Should().Be(account.Id);
            root.GetProperty("archivedSourceDir").GetString().Should().Be(restoredItem.ArchivedSourceDir);
            root.GetProperty("sourceProjectDir").GetString().Should()
                .Be(Path.Combine(workspace, "current-by-id"));
        }

        TikTokArchivedProjectService.Restore(
            workspace,
            restoredItem.ArchiveProjectDir,
            targetRoot);

        File.Exists(Path.Combine(workspace, "current-by-id", "source.txt")).Should().BeTrue();
        File.Exists(Path.Combine(workspace, "workflow", "_current-by-id", "workflow.txt")).Should().BeTrue();
    }

    [Fact]
    public void Preview_does_not_overwrite_existing_target_project()
    {
        var workspace = Path.Combine(_tempRoot, "workspace");
        var legacyRoot = Path.Combine(_tempRoot, "legacy");
        var targetRoot = Path.Combine(workspace, "archive");
        Directory.CreateDirectory(workspace);
        var account = new TikTokAccountProfile
        {
            Id = "acct-1",
            TiktokUploadProfilePath = workspace,
        };
        var archive = CreateArchive(
            legacyRoot,
            "conflict",
            "冲突项目",
            workspace,
            account.Id,
            "账号1");
        Directory.CreateDirectory(Path.Combine(targetRoot, "source", Path.GetFileName(archive.SourceDir)));

        var preview = TikTokArchivedProjectService.BuildAccountArchiveMigrationPreview(
            workspace,
            legacyRoot,
            targetRoot,
            account,
            new[] { account });

        preview.MigratableCount.Should().Be(0);
        preview.ConflictCount.Should().Be(1);
        Directory.Exists(archive.SourceDir).Should().BeTrue();
        File.Exists(archive.MetadataPath).Should().BeTrue();
    }

    private static (string SourceDir, string WorkflowDir, string MetadataPath) CreateArchive(
        string archiveRoot,
        string projectKey,
        string newTitle,
        string originalWorkspace,
        string accountId,
        string accountName)
    {
        var sourceDir = Path.Combine(archiveRoot, "source", projectKey);
        var workflowDir = Path.Combine(archiveRoot, "workflow", "_" + projectKey);
        var metadataPath = Path.Combine(archiveRoot, "meta", projectKey + ".json");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(Path.Combine(sourceDir, "source.txt"), projectKey);
        File.WriteAllText(Path.Combine(workflowDir, "workflow.txt"), projectKey);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = projectKey,
                ["displayName"] = projectKey,
                ["originalTitle"] = "原剧-" + projectKey,
                ["newTitle"] = newTitle,
                ["archiveSource"] = "tiktok",
                ["archivedAt"] = "2026-07-30T12:00:00",
                ["accountProfileId"] = accountId,
                ["accountProfileName"] = accountName,
                ["sourceProjectDir"] = Path.Combine(originalWorkspace, projectKey),
                ["workflowProjectDir"] = Path.Combine(originalWorkspace, "workflow", "_" + projectKey),
                ["archivedSourceDir"] = sourceDir,
                ["archivedWorkflowDir"] = workflowDir,
            }));
        return (sourceDir, workflowDir, metadataPath);
    }
}
